using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Lull.Portal;

/// <summary>
/// What the rate limits count against, and the limit on password hashing. The per-client
/// limits themselves are in Program.cs (sign-in pages, Mac registration) and
/// <see cref="Api.DeviceAuthGuard"/> (made-up device tokens).
/// </summary>
public static class RateLimits
{
    /// <summary>
    /// The client a limit counts against: its IPv4 address, or its IPv6 /64. A home or office
    /// gets a whole /64, so counting each IPv6 address on its own would let one client
    /// sidestep every limit by changing address.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }

    public static string ClientKey(HttpContext http) => ClientKey(http.Connection.RemoteIpAddress);

    /// <summary>The pages whose POSTs check or set a password (PBKDF2, about 0.3 s of CPU each).</summary>
    static readonly string[] PasswordPages = ["/Login", "/Setup", "/FirstRun", "/Account"];

    public static bool HashesAPassword(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method) && PasswordPages.Any(p => http.Request.Path.StartsWithSegments(p));

    /// <summary>
    /// For the whole portal, not per client: at most one password hash per CPU at a time, with a
    /// short queue, and 429 beyond it. Per-client limits can't stop many clients at once from
    /// keeping every CPU busy hashing; this keeps the rest of the portal (and the Macs' API) responsive.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> PasswordHashing(int processors) =>
        PartitionedRateLimiter.Create<HttpContext, string>(http => HashesAPassword(http)
            ? RateLimitPartition.GetConcurrencyLimiter("password-hashing", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = processors,
                QueueLimit = 16 * processors,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            })
            : RateLimitPartition.GetNoLimiter("other"));
}
