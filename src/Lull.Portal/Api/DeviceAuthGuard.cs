using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Api;

/// <summary>
/// Device tokens that matched no Mac, per client (<see cref="RateLimits.ClientKey(HttpContext)"/>).
/// Past <see cref="PortalOptions.DeviceAuthFailuresPerMinute"/>, that client's requests to the
/// device API are refused with 429 before the database lookup, so a flood of made-up tokens costs
/// nothing. Real Macs almost never fail (a lost token registers again), so a school's Macs behind
/// one NAT address aren't held back by each other's polling.
/// </summary>
public sealed class DeviceAuthGuard(IOptions<PortalOptions> options) : IDisposable
{
    readonly PartitionedRateLimiter<string> failures = PartitionedRateLimiter.Create<string, string>(client =>
        RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Value.DeviceAuthFailuresPerMinute),
            Window = TimeSpan.FromMinutes(1),
        }));

    /// <summary>This client has used up its failures for now.</summary>
    public bool Blocked(HttpContext http) => failures.GetStatistics(RateLimits.ClientKey(http))?.CurrentAvailablePermits == 0;

    /// <summary>A token from this client matched no Mac.</summary>
    public void Failed(HttpContext http) => failures.AttemptAcquire(RateLimits.ClientKey(http)).Dispose();

    public void Dispose() => failures.Dispose();
}
