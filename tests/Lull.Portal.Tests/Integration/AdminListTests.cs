using System.Net;
using Lull.Portal.Auth;
using Microsoft.AspNetCore.Hosting;

namespace Lull.Portal.Tests.Integration;

/// <summary>
/// 60 Macs (C02PAGE001..060, pinned to pupil001..060) and 55 parents: two pages
/// of each, added once for the test class (xunit makes a new test class instance per test).
/// </summary>
public class AdminListFactory : PortalFactory
{
    Task? seeded;

    public Task SeededAsync() => seeded ??= SeedAsync();

    async Task SeedAsync()
    {
        for (int i = 1; i <= 60; i++)
        {
            var (mac, _) = await RegisterMacAsync($"C02PAGE{i:D3}");
            await CheckInAsync(mac, $"pupil{i:D3}", null);
        }
        for (int i = 1; i <= 55; i++)
            await Users.CreateAsync($"family{i:D3}@parents.test", $"Family {i:D3}", Roles.Parent, [$"pupil{i:D3}"], "test", default);
    }
}

/// <summary>IT's Devices and Users lists, searched and paged in the database.</summary>
public class AdminListTests(AdminListFactory portal) : IClassFixture<AdminListFactory>
{
    static string Decode(string html) => WebUtility.HtmlDecode(html);

    async Task<HttpClient> AdminAsync()
    {
        await portal.SeededAsync();
        return await portal.SignInAdminAsync();
    }

    [Fact]
    public async Task DevicesArePagedAndSearchedByUserOrSerial()
    {
        var admin = await AdminAsync();
        var first = Decode(await admin.GetStringAsync("/Admin/Devices"));
        Assert.Contains("1–50 of 60 Macs.", first);
        Assert.Contains("Page 1 of 2", first);
        Assert.Contains("href=\"/Admin/Devices?p=2\"", first);
        Assert.Contains("C02PAGE050", first);
        Assert.DoesNotContain("C02PAGE051", first);

        var second = Decode(await admin.GetStringAsync("/Admin/Devices?p=2"));
        Assert.Contains("51–60 of 60 Macs.", second);
        Assert.Contains("C02PAGE060", second);
        Assert.DoesNotContain("C02PAGE050", second);
        Assert.Contains("51–60 of 60 Macs.", Decode(await admin.GetStringAsync("/Admin/Devices?p=99"))); // nearest page

        var bySerial = Decode(await admin.GetStringAsync("/Admin/Devices?q=page05"));
        Assert.Contains("1–10 of 10 Macs.", bySerial); // C02PAGE050..059, any case
        Assert.DoesNotContain("Page 1 of", bySerial);
        Assert.Contains("1–1 of 1 Macs.", Decode(await admin.GetStringAsync("/Admin/Devices?q=PUPIL007")));
        Assert.Contains("No Macs match", Decode(await admin.GetStringAsync("/Admin/Devices?q=nobody")));
    }

    [Fact]
    public async Task ListLinksKeepTheFilterAndTheSearch()
    {
        var admin = await AdminAsync();
        var page = Decode(await admin.GetStringAsync("/Admin/Users?f=parents"));
        Assert.Contains("href=\"/Admin/Users?f=parents&p=2\"", page);
        Assert.Contains("name=\"f\" value=\"parents\"", page); // searching stays within it
        Assert.Contains("href=\"/Admin/Users?f=admins\"", page);

        var searched = Decode(await admin.GetStringAsync("/Admin/Users?q=family&f=parents"));
        Assert.Contains("href=\"/Admin/Users?q=family&f=admins\"", searched);
        Assert.Contains("1–50 of 55 users.", searched);

        var admins = Decode(await admin.GetStringAsync("/Admin/Users?f=admins"));
        Assert.Contains("1–1 of 1 users.", admins);
        Assert.DoesNotContain("family001", admins);
        Assert.Contains("1–1 of 1 users.", Decode(await admin.GetStringAsync("/Admin/Users?f=admins&q=it@")));
        Assert.Contains("1–50 of 56 users.", Decode(await admin.GetStringAsync("/Admin/Users?f=nonsense"))); // all of them
        Assert.Contains("1–50 of 55 users.", Decode(await admin.GetStringAsync("/Admin/Users?f=waiting")));
        Assert.Contains("No users.", Decode(await admin.GetStringAsync("/Admin/Users?f=locked")));
    }

    [Theory]
    [InlineData("/Admin/Devices")]
    [InlineData("/Admin/Users")]
    [InlineData("/Admin/Audit")]
    public async Task ListsHaveLabelledSearchAndMarkedUpTables(string path)
    {
        var admin = await AdminAsync();
        var page = await admin.GetStringAsync(path);
        Assert.Contains("<label for=\"q\" class=\"sr-only\">", page);
        Assert.Contains("<thead><tr><th scope=\"col\">", page);
        Assert.Contains("<tbody>", page);
        Assert.Contains("role=\"region\"", page); // scrolls on a phone, so the keyboard has to reach it
        Assert.Contains("tabindex=\"0\"", page);
    }

    [Fact]
    public async Task UsersArePagedAndSearchedByNameEmailOrStudent()
    {
        var admin = await AdminAsync();
        var first = Decode(await admin.GetStringAsync("/Admin/Users"));
        Assert.Contains("1–50 of 56 users.", first); // and the owner
        Assert.Contains("href=\"/Admin/Users?p=2\"", first);
        Assert.Contains("51–56 of 56 users.", Decode(await admin.GetStringAsync("/Admin/Users?p=2")));

        var byStudent = Decode(await admin.GetStringAsync("/Admin/Users?q=Pupil042"));
        Assert.Contains("1–1 of 1 users.", byStudent);
        Assert.Contains("family042@parents.test", byStudent);
        Assert.Contains("1–1 of 1 users.", Decode(await admin.GetStringAsync("/Admin/Users?q=family 017")));
        Assert.Contains("1–1 of 1 users.", Decode(await admin.GetStringAsync("/Admin/Users?q=FAMILY031@")));

        // A search's page links keep the search.
        var wide = Decode(await admin.GetStringAsync("/Admin/Users?q=family"));
        Assert.Contains("1–50 of 55 users.", wide);
        Assert.Contains("href=\"/Admin/Users?q=family&p=2\"", wide);
    }
}

/// <summary>A portal with a tiny sign-in limit, to show registration has its own.</summary>
public class TightRateLimitFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lull:RateLimitPerMinute", "2");
    }
}

public class RateLimitTests(TightRateLimitFactory portal) : IClassFixture<TightRateLimitFactory>
{
    [Fact]
    public async Task MacsRegisteringFromOneAddressDontUseUpTheSignInLimit()
    {
        var browser = portal.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.GetAsync("/Login")).StatusCode);

        // Same client address, far more than the sign-in limit (default 300 a minute).
        for (int i = 1; i <= 10; i++) await portal.RegisterMacAsync($"C02RATE{i:D3}");
    }
}
