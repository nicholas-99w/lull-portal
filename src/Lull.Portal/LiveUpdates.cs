using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lull.Portal;

/// <summary>
/// The parts of a page that live.js fetches while it's open (<c>?handler=Live</c>): tagged with an
/// ETag of what they say, so an unchanged answer is a 304 with no body, and kept from renewing the
/// sign-in, so a page left open still signs out after 8 hours like one that isn't.
/// </summary>
public static partial class LiveUpdates
{
    public const string Handler = "Live";

    public static bool IsLive(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && string.Equals(request.Query["handler"], Handler, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Each form's antiforgery token is new every time, though nothing has changed: left out of the
    /// ETag. The page keeps the token it has, which stays valid.
    /// </summary>
    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]*\"")]
    private static partial Regex Token();

    public static string ETagOf(string html) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token().Replace(html, "")))).ToLowerInvariant()[..32] + "\"";

    public static IApplicationBuilder UseLiveUpdates(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!IsLive(http.Request))
        {
            await next();
            return;
        }
        var body = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await next();
        }
        finally
        {
            http.Response.Body = body;
        }
        if (http.Response.StatusCode != StatusCodes.Status200OK)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(body, http.RequestAborted);
            return;
        }
        var etag = ETagOf(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            http.Response.ContentLength = 0;
            return;
        }
        http.Response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(body, http.RequestAborted);
    });
}
