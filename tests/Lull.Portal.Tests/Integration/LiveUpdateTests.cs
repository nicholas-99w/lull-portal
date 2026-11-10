using System.Net;
using System.Net.Http.Headers;

namespace Lull.Portal.Tests.Integration;

/// <summary>The parts of a parent's pages that live.js fetches while they're open.</summary>
public class LiveUpdateTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static async Task<HttpResponseMessage> LiveAsync(HttpClient browser, string url, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        return await browser.SendAsync(request);
    }

    [Fact]
    public async Task AMacsPageSendsItsChangingPartsAndSaysWhenNothingChanged()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02LIVE00001");
        await PortalFactory.CheckInAsync(mac, "live01", null);
        var parent = await portal.ParentAsync("liveparent", "live01");

        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains($"data-live-url=\"/Device/{id}?handler=Live\"", page);
        Assert.Contains("data-live=\"status\"", page);
        Assert.Contains("data-live=\"controls\"", page);

        var live = await LiveAsync(parent, $"/Device/{id}?handler=Live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var html = await live.Content.ReadAsStringAsync();
        Assert.Contains("data-live=\"status\"", html);
        Assert.Contains("data-live=\"controls\"", html);
        Assert.DoesNotContain("<html", html); // just the parts
        Assert.DoesNotContain("Schedule", html);
        var etag = live.Headers.ETag!.Tag;

        // Unchanged, though its forms' antiforgery tokens are new: 304, no body.
        var same = await LiveAsync(parent, $"/Device/{id}?handler=Live", etag);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
        Assert.Empty(await same.Content.ReadAsByteArrayAsync());

        // The Mac checks in with the latest settings: something to show.
        await PortalFactory.CheckInAsync(mac, "live01", (await PortalFactory.SettingsAsync(mac)).Revision);
        var changed = await LiveAsync(parent, $"/Device/{id}?handler=Live", etag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task OnlyTheParentsOwnMacsAndOnlyForParents()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02LIVE00002");
        await PortalFactory.CheckInAsync(mac, "live02", null);
        var stranger = await portal.ParentAsync("livestranger", "someoneelse02");
        Assert.Equal(HttpStatusCode.NotFound, (await LiveAsync(stranger, $"/Device/{id}?handler=Live")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await LiveAsync(portal.Browser(), $"/Device/{id}?handler=Live")).StatusCode); // to sign in

        var parent = await portal.ParentAsync("liveparent2", "live02");
        var home = await LiveAsync(parent, "/?handler=Live");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains($"/Device/{id}", await home.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await LiveAsync(await portal.SignInAdminAsync(), "/?handler=Live")).StatusCode);
    }

    [Fact]
    public async Task APageLeftOpenDoesntKeepItsParentSignedIn()
    {
        var parent = await portal.ParentAsync("liveidle", "liveidle01");
        // Past half of the 8 hours, a request would renew the sign-in: the page's own polling doesn't.
        portal.Clock.Now = portal.Clock.Now.AddHours(5);
        try
        {
            var live = await LiveAsync(parent, "/?handler=Live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.False(live.Headers.Contains("Set-Cookie"));
            var visit = await parent.GetAsync("/");
            Assert.True(visit.Headers.Contains("Set-Cookie"), "someone using the portal still renews it");
        }
        finally
        {
            portal.Clock.Now = portal.Clock.Now.AddHours(-5);
        }
    }
}
