using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lull.Portal.Tests.Integration;

/// <summary>
/// IT's device list filters, which are SQL so as not to load every Mac: they have to agree with what each
/// row's status says, worked out separately in C#.
/// </summary>
public class DeviceFilterTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>The serials a view of the list shows.</summary>
    async Task<string[]> SerialsAsync(HttpClient admin, string filter)
    {
        var page = await admin.GetStringAsync($"/Admin/Devices?q=C02FILT&f={filter}");
        return [.. Regex.Matches(page, "C02FILT[A-Z]+").Select(m => m.Value).Distinct().Order()];
    }

    static Action<JsonObject> Reports(string phase, DateTimeOffset? start = null, DateTimeOffset? end = null, string? error = null) => n =>
    {
        n["phase"] = phase;
        n["windowStart"] = start?.ToString("O");
        n["windowEnd"] = end?.ToString("O");
        if (error is not null) n["settingsError"] = error;
    };

    async Task<(HttpClient Mac, string Id)> MacAsync(string name, Action<JsonObject> report, bool applied = true)
    {
        var (mac, id) = await portal.RegisterMacAsync($"C02FILT{name}");
        var revision = applied ? (await PortalFactory.SettingsAsync(mac)).Revision : (long?)null;
        await PortalFactory.CheckInAsync(mac, $"filt{name.ToLowerInvariant()}", revision, change: report);
        return (mac, id);
    }

    [Fact]
    public async Task EachViewShowsTheMacsItsStatusSays()
    {
        var now = portal.Clock.Now;
        // Reported, and then 30 minutes pass: everything checked in again below is fresh, the rest offline.
        var (quiet, _) = await MacAsync("QUIET", Reports("normal"));
        portal.Clock.Now = now = now.AddMinutes(30);

        await MacAsync("FINE", Reports("normal", now.AddHours(3), now.AddHours(9)));
        await MacAsync("LOCKED", Reports("locked", now.AddHours(-1), now.AddHours(9)));
        await MacAsync("REFUSED", Reports("normal", now.AddHours(3), now.AddHours(9), error: "overlaps school hours"));
        await MacAsync("PENDING", Reports("normal", now.AddHours(3), now.AddHours(9)), applied: false);
        await MacAsync("OVERDUE", n =>
        {
            Reports("normal")(n);
            n["nextTransition"] = now.AddMinutes(-11).ToString("O");
        });
        await portal.RegisterMacAsync("C02FILTNEVER");
        var (gone, goneId) = await MacAsync("GONE", Reports("normal"));

        var admin = await portal.SignInAdminAsync();
        await PortalFactory.PostFormAsync(admin, $"/Device/{goneId}?handler=Deregister", new() { ["confirm"] = "true" }, formPage: $"/Device/{goneId}");
        var (downMac, downId) = await MacAsync("DOWN", Reports("normal"));
        var lockRes = await PortalFactory.PostFormAsync(admin, $"/Device/{downId}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{downId}");
        Assert.Equal(HttpStatusCode.Redirect, lockRes.StatusCode);
        await PortalFactory.CheckInAsync(downMac, "filtdown", (await PortalFactory.SettingsAsync(downMac)).Revision, change: Reports("normal"));

        Assert.Equal(["C02FILTNEVER", "C02FILTOVERDUE", "C02FILTQUIET", "C02FILTREFUSED"], await SerialsAsync(admin, "attention"));
        Assert.Equal(["C02FILTQUIET"], await SerialsAsync(admin, "offline"));
        Assert.Equal(["C02FILTDOWN", "C02FILTLOCKED"], await SerialsAsync(admin, "downtime"));
        Assert.Equal(["C02FILTPENDING"], await SerialsAsync(admin, "pending"));

        // The rows say the same, and a refusal is spelled out rather than hidden behind what the Mac was doing.
        var all = Decode(await admin.GetStringAsync("/Admin/Devices?q=C02FILT"));
        Assert.Contains("Settings refused", all);
        Assert.Contains("overlaps school hours", all);
        Assert.Contains("Not heard from", all);
        Assert.Contains("De-registered", all);
        Assert.Contains("All <span class=\"filter-count\">9</span>", all);
        Assert.Contains("Offline <span class=\"filter-count\">1</span>", all);
        Assert.Contains("href=\"/Admin/Devices?q=C02FILT&f=offline\"", all);
    }

    [Fact]
    public async Task AnEmptyViewSaysSo()
    {
        var admin = await portal.SignInAdminAsync();
        var page = await admin.GetStringAsync("/Admin/Devices?q=C02NOTHINGHERE&f=pending");
        Assert.Contains("match", page);
        Assert.Contains("name=\"f\" value=\"pending\"", page);
    }
}
