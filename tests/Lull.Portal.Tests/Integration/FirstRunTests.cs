using System.Net;
using System.Net.Http.Json;
using Lull.Portal.Api;

namespace Lull.Portal.Tests.Integration;

/// <summary>A portal with no admin yet: the first visitor creates the owner.</summary>
public sealed class EmptyPortalFactory : PortalFactory
{
    protected override bool CreatesOwner => false;
}

public class FirstRunTests(EmptyPortalFactory portal) : IClassFixture<EmptyPortalFactory>
{
    Task<HttpResponseMessage> FirstRun(HttpClient b, string password, string? confirm = null, string email = "owner@school.test") =>
        PortalFactory.PostFormAsync(b, "/FirstRun",
            new() { ["name"] = "Head of IT", ["email"] = email, ["password"] = password, ["confirm"] = confirm ?? password });

    [Fact]
    public async Task TheFirstVisitorSetsUpTheOwnerOnce()
    {
        // Before set-up: every page leads to the first-run page; Macs and health checks still work.
        Assert.Equal("/FirstRun", (await portal.Browser().GetAsync("/")).Headers.Location?.OriginalString);
        Assert.Equal("/FirstRun", (await portal.Browser().GetAsync("/Login")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await portal.CreateClient().GetAsync("/healthz")).StatusCode);
        var register = await portal.CreateClient().PostAsJsonAsync("/api/device/v1/register",
            new RegisterRequest("C02FIRSTRUN1", "HW-1", PortalFactory.Secret, null));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var b = portal.Browser();
        Assert.Contains("at least 12", WebUtility.HtmlDecode(await (await FirstRun(b, "too-short")).Content.ReadAsStringAsync()));
        Assert.Contains("don't match", WebUtility.HtmlDecode(await (await FirstRun(b, "A-Long-Owner-Password", "different-password")).Content.ReadAsStringAsync()));
        var ok = await FirstRun(b, "A-Long-Owner-Password");
        Assert.Equal("/TwoFactor", ok.Headers.Location?.OriginalString); // an admin sets up two-step sign-in first
        Assert.Equal("/TwoFactor", (await b.GetAsync("/Admin/Users")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.SetUpTwoFactorAsync(b, "owner@school.test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Users")).StatusCode); // signed in as the owner
        var owner = (await portal.Users.FindByEmailAsync("owner@school.test", default))!;
        Assert.True(owner.IsOwner);
        Assert.Equal("Admin", owner.Role);
        Assert.Contains("portal_set_up", await b.GetStringAsync("/Admin/Audit"));

        // From now on the page is gone, and a second visitor can't become owner.
        var late = portal.Browser();
        Assert.Equal("/Login", (await late.GetAsync("/FirstRun")).Headers.Location?.OriginalString);
        var page = await late.GetStringAsync("/Login");
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var second = await late.PostAsync("/FirstRun", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Intruder", ["email"] = "intruder@example.com", ["password"] = "another-long-password",
            ["confirm"] = "another-long-password", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal("/Login", second.Headers.Location?.OriginalString);
        Assert.Null(await portal.Users.FindByEmailAsync("intruder@example.com", default));
        Assert.Equal(HttpStatusCode.OK, (await portal.Browser().GetAsync("/Login")).StatusCode);
    }
}

/// <summary>Two people opening the first-run page at once: exactly one owner.</summary>
public class FirstRunRaceTests(EmptyPortalFactory portal) : IClassFixture<EmptyPortalFactory>
{
    [Fact]
    public async Task OnlyOneOwner()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            portal.Users.CreateOwnerAsync($"racer{i}@school.test", $"Racer {i}", "A-Long-Owner-Password", default)));
        Assert.Single(attempts, a => a is not null);
        Assert.Single(await portal.Users.ListAsync(default), u => u.IsOwner);
    }
}
