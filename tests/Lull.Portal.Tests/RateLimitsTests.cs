using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Lull.Portal.Tests;

public class RateLimitsTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")] // IPv4 arriving on an IPv6 socket
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")] // the same /64: the same client
    public void ClientsAreAddressesOrIPv6Slash64s(string address, string key) =>
        Assert.Equal(key, RateLimits.ClientKey(IPAddress.Parse(address)));

    [Fact]
    public void NoAddressIsOneClient() => Assert.Equal("unknown", RateLimits.ClientKey((IPAddress?)null));

    static HttpContext Request(string method, string path) =>
        new DefaultHttpContext { Request = { Method = method, Path = path } };

    [Theory]
    [InlineData("POST", "/Login", true)]
    [InlineData("POST", "/Setup", true)]
    [InlineData("POST", "/FirstRun", true)]
    [InlineData("POST", "/Account", true)]
    [InlineData("GET", "/Login", false)]
    [InlineData("POST", "/Logout", false)]
    [InlineData("POST", "/Device/6b29fc40-0000-0000-0000-000000000000", false)]
    public void OnlyPasswordPostsAreHashingWork(string method, string path, bool hashes) =>
        Assert.Equal(hashes, RateLimits.HashesAPassword(Request(method, path)));

    [Fact]
    public void PasswordHashingIsLimitedForTheWholePortal()
    {
        using var limiter = RateLimits.PasswordHashing(processors: 1);
        using var first = limiter.AttemptAcquire(Request("POST", "/Login"));
        Assert.True(first.IsAcquired);
        // A second at once waits (or is refused): AttemptAcquire doesn't queue, so here it's refused.
        using (var second = limiter.AttemptAcquire(Request("POST", "/Setup")))
            Assert.False(second.IsAcquired);
        using (var page = limiter.AttemptAcquire(Request("GET", "/Login")))
            Assert.True(page.IsAcquired); // everything else is unlimited
    }
}
