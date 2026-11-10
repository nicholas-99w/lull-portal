using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace Lull.Portal.Tests.Integration;

/// <summary>The portal as deployed (Production), where HSTS is on.</summary>
public class ProductionPortalFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

public class SecurityHeaderTests(ProductionPortalFactory portal) : IClassFixture<ProductionPortalFactory>
{
    [Theory]
    [InlineData("/Login")]     // a page with a form (antiforgery would add its own X-Frame-Options)
    [InlineData("/site.css")]  // a static file
    [InlineData("/healthz")]
    public async Task EveryResponseHasTheHardeningHeaders(string path)
    {
        var res = await portal.Browser().GetAsync(path);
        Assert.True(res.IsSuccessStatusCode, $"{path}: {res.StatusCode}");
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Assert.Single(res.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(res.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(res.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(res.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task PagesAreNeverStoredButStaticFilesAndTheDeviceApiAreLeftAlone()
    {
        var browser = portal.Browser();
        Assert.Equal("no-store", (await browser.GetAsync("/Login")).Headers.CacheControl?.ToString());
        Assert.Equal("no-store", (await browser.GetAsync("/Device/" + Guid.NewGuid())).Headers.CacheControl?.ToString()); // a redirect to sign in
        Assert.NotEqual("no-store", (await browser.GetAsync("/site.css")).Headers.CacheControl?.ToString());
        Assert.Null((await browser.GetAsync("/api/device/v1/settings")).Headers.CacheControl); // 401, as lulld sees it
    }

    [Fact]
    public async Task HstsIsSentOnlyOverHttps()
    {
        var browser = portal.Browser();
        // UseHsts never sends it to localhost, so use a real-looking host name.
        var https = await browser.GetAsync("https://portal.school.test/Login");
        Assert.Equal("max-age=31536000", Assert.Single(https.Headers.GetValues("Strict-Transport-Security")));
        var http = await browser.GetAsync("http://portal.school.test/Login");
        Assert.False(http.Headers.Contains("Strict-Transport-Security"));
    }

    /// <summary>The CSP blocks inline scripts and styles, so they'd silently do nothing. Scripts from wwwroot are fine.</summary>
    [Fact]
    public void PagesHaveNoInlineScriptsOrStyles()
    {
        var pages = Path.Combine(Fixtures.RepoRoot, "src", "Lull.Portal", "Pages");
        var inline = new Regex(@"<script(?![^>]*\ssrc=""~/)|<style|\sstyle=|\son[a-z]+=", RegexOptions.IgnoreCase);
        var offenders = Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => inline.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(pages, x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "Inline script/style (move it to site.css or a .js file): " + string.Join(", ", offenders));
    }
}

/// <summary>The portal behind its reverse proxy, as compose runs it.</summary>
public class BehindProxyPortalFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lull:TrustForwardedHeaders", "true");
        builder.UseSetting("AllowedHosts", "portal.school.test");
    }
}

public class BehindProxyTests(BehindProxyPortalFactory portal) : IClassFixture<BehindProxyPortalFactory>
{
    /// <summary>The proxy terminates TLS, so requests arrive over HTTP: the sign-in cookie is still Secure.</summary>
    [Fact]
    public async Task TheSignInCookieIsAlwaysSecure()
    {
        var (user, code) = await portal.Users.CreateAsync("proxy@parents.test", "Proxy", Lull.Portal.Auth.Roles.Parent, ["kid01"], "test", default);
        await portal.Users.CompleteSetupAsync(user.Email, code, PortalFactory.ParentPassword, default);
        var res = await PortalFactory.PostFormAsync(portal.Browser(), "http://localhost/Login",
            new() { ["email"] = user.Email, ["password"] = PortalFactory.ParentPassword });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("lull.portal="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyThePortalsOwnHostNamesAreServed()
    {
        var browser = portal.Browser();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await browser.GetAsync("http://portal.school.test/Login")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await browser.GetAsync("http://elsewhere.test/Login")).StatusCode);
        // The container's own health check (http://127.0.0.1:8080/healthz) always gets through.
        Assert.Equal(System.Net.HttpStatusCode.OK, (await browser.GetAsync("http://127.0.0.1/healthz")).StatusCode);
    }
}
