using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Lull.Portal.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Lull.Portal.Tests.Integration;

/// <summary>
/// The portal with Entra sign-in set up. Microsoft's side is simulated: Entra's
/// endpoints are fixed (no metadata download), and <see cref="EntraSignInAsync"/>
/// does what the OpenID Connect handler does once Entra has returned a valid
/// ID token: runs the portal's OnTokenValidated with its claims, then signs in.
/// </summary>
public class EntraPortalFactory : PortalFactory
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string ClientId = "66666666-7777-8888-9999-000000000000";
    public const string AuthorizeEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";
    public const string EndSessionEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/logout";
    const string TokenPath = "/test/entra-token";

    protected virtual bool LocalSignIn => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lull:Entra:TenantId", TenantId);
        builder.UseSetting("Lull:Entra:ClientId", ClientId);
        builder.UseSetting("Lull:Entra:ClientSecret", "test-client-secret");
        builder.UseSetting("Lull:LocalSignIn", LocalSignIn ? "true" : "false");
        builder.ConfigureTestServices(s =>
        {
            // Configure (not PostConfigure): runs before the handler's own set-up, which then uses these.
            s.Configure<OpenIdConnectOptions>(EntraSignIn.Scheme, o => o.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0",
                AuthorizationEndpoint = AuthorizeEndpoint,
                EndSessionEndpoint = EndSessionEndpoint,
            });
            s.AddSingleton<IStartupFilter>(new TokenEndpoint());
        });
    }

    /// <summary>Changes a setting for one test (options are read at each sign-in).</summary>
    public EntraOptions Entra => Services.GetRequiredService<IOptions<PortalOptions>>().Value.Entra;

    /// <summary>
    /// Signs <paramref name="browser"/> in as if Entra returned an ID token with these
    /// claims. The response is the handler's: a redirect to the page asked for, or to
    /// <c>/Login?entra=…</c> when the portal refused.
    /// </summary>
    public static async Task<HttpResponseMessage> EntraSignInAsync(HttpClient browser, params (string Type, string Value)[] claims) =>
        await browser.PostAsJsonAsync(TokenPath, claims.Select(c => new[] { c.Type, c.Value }).ToArray());

    /// <summary>The usual token: object ID, name, email, roles and students (omitted when null).</summary>
    public static (string, string)[] Token(Guid oid, string email, string? role, string? students = null, string? name = null)
    {
        var claims = new List<(string, string)> { ("oid", oid.ToString()), ("name", name ?? email), ("email", email) };
        if (role is not null) claims.Add(("roles", role));
        if (students is not null) claims.Add(("students", students));
        return [.. claims];
    }

    /// <summary>A browser signed in with Entra (the sign-in must succeed).</summary>
    public async Task<HttpClient> EntraBrowserAsync(params (string, string)[] claims)
    {
        var browser = Browser();
        var res = await EntraSignInAsync(browser, claims);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
        return browser;
    }

    sealed class TokenEndpoint : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that would make the path the PathBase, and the cookie's path with it.
            app.Use(async (http, nextMiddleware) =>
            {
                if (http.Request.Path != TokenPath)
                {
                    await nextMiddleware();
                    return;
                }
                var pairs = (await http.Request.ReadFromJsonAsync<string[][]>())!;
                var token = new ClaimsPrincipal(new ClaimsIdentity(pairs.Select(p => new Claim(p[0], p[1])), "AuthenticationTypes.Federation"));
                var schemes = http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
                var options = http.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraSignIn.Scheme);
                var properties = new AuthenticationProperties { RedirectUri = "/" };
                var ctx = new TokenValidatedContext(http, (await schemes.GetSchemeAsync(EntraSignIn.Scheme))!, options, token, properties);
                await options.Events.TokenValidated(ctx);
                if (ctx.Result?.Handled == true) return; // refused: the handler already redirected
                var signedIn = ctx.Properties ?? properties; // the portal's handler changes these (no sliding expiry)
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ctx.Principal!, signedIn);
                http.Response.Redirect(signedIn.RedirectUri!);
            });
            next(app);
        };
    }
}

/// <summary>Entra sign-in only: local sign-in off, so passwords are for the owner alone.</summary>
public sealed class EntraOnlyPortalFactory : EntraPortalFactory
{
    protected override bool LocalSignIn => false;
}
