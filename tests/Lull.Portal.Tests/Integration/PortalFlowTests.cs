using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Lull.Portal.Domain;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Lull.Portal.Tests.Integration;

/// <summary>Parents and IT using the portal, with Macs checking in.</summary>
public class PortalFlowTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static Dictionary<string, string> Settings(bool enabled, string ss, string se, string ws, string we)
    {
        // enabled = false: opt out of both kinds (the times are then ignored).
        var mode = enabled ? "custom" : "off";
        return new Dictionary<string, string>
        {
            ["schoolMode"] = mode, ["schoolStart"] = ss, ["schoolEnd"] = se,
            ["weekendMode"] = mode, ["weekendStart"] = ws, ["weekendEnd"] = we,
        };
    }

    Task<HttpClient> ParentWithChildrenAsync(string parent, string children) =>
        portal.ParentAsync(parent, [.. ChildList.Parse(children).Usernames]);

    /// <summary>Reset overrides, labelled for what it ends: a lock-down, an unlock or a skip.</summary>
    static readonly string[] ResetButtons = ["End lock-down", "Cancel unlock", "Cancel skip"];

    /// <summary>The fixture check-in from a Mac that has applied the portal's current settings.</summary>
    static async Task CheckInAppliedAsync(HttpClient mac, string username, Action<JsonObject>? change = null) =>
        await PortalFactory.CheckInAsync(mac, username, (await PortalFactory.SettingsAsync(mac)).Revision, change: change);

    [Fact]
    public async Task SignInSendsParentsHomeAndAdminsToDevices()
    {
        await portal.ParentAsync("signin01", "kid01");
        var res = await PortalFactory.PostFormAsync(portal.Browser(), "/Login", new() { ["email"] = "SignIn01@Parents.test", ["password"] = PortalFactory.ParentPassword });
        Assert.Equal("/", res.Headers.Location?.OriginalString); // email is case-insensitive
        var admin = await portal.PasswordAndCodeAsync(portal.Browser(), PortalFactory.AdminEmail, PortalFactory.AdminPassword);
        Assert.Equal("/Admin/Devices", admin.Headers.Location?.OriginalString);

        var anon = await portal.Browser().GetAsync("/");
        Assert.StartsWith("http://localhost/Login", anon.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ParentOptsOutAndSeesItApplied()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02FLOW00001");
        var revision = (await PortalFactory.SettingsAsync(mac)).Revision;
        await PortalFactory.CheckInAsync(mac, "jsmith26", revision);

        var parent = await ParentWithChildrenAsync("parent1", "JSmith26");
        var home = await parent.GetStringAsync("/");
        Assert.Contains("jsmith26", home);

        // Customise school nights, opt out of the weekend only.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var form = new Dictionary<string, string>
        {
            ["schoolMode"] = "custom", ["schoolStart"] = "21:00", ["schoolEnd"] = "06:30",
            ["weekendMode"] = "off", ["weekendStart"] = "22:30", ["weekendEnd"] = "08:00",
        };
        Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form)).StatusCode);

        var s = await PortalFactory.SettingsAsync(mac);
        Assert.True(s.Enabled);
        Assert.Equal("21:00", s.SchoolNights!.Start);
        Assert.Null(s.SchoolNightsOff);
        Assert.Equal(true, s.WeekendOff);
        Assert.Null(s.Weekend); // an opted-out kind sends no window
        Assert.True(s.Revision > revision);
        var pending = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Change pending", pending);
        Assert.DoesNotContain("Changes pending", pending); // said once, by the status
        var card = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}")); // its page: the home page's card is just the status
        Assert.DoesNotContain("School nights ·", card); // under the status: only when it was last seen
        Assert.Contains("Change pending", card);
        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule"));
        Assert.Matches("value=\"off\" checked", page);  // the weekend's opt-out radio
        Assert.Contains("Serial number C02FLOW00001", await parent.GetStringAsync($"/Device/{id}"));

        await PortalFactory.CheckInAsync(mac, "jsmith26", s.Revision);
        Assert.DoesNotContain("Change pending", await parent.GetStringAsync($"/Device/{id}"));

        // Both off.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        form["schoolMode"] = "off";
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form);
        var both = await PortalFactory.SettingsAsync(mac);
        Assert.Equal((true, true, (Lull.Portal.Api.WindowPayload?)null), (both.SchoolNightsOff, both.WeekendOff, both.SchoolNights));
    }

    [Fact]
    public async Task ParentsCantSeeOrChangeOtherFamiliesMacs()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02OTHER0001");
        await PortalFactory.CheckInAsync(mac, "asmith29", null);
        var before = await PortalFactory.SettingsAsync(mac);

        var other = await ParentWithChildrenAsync("parent2", "someoneelse");
        Assert.DoesNotContain("asmith29", await other.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Device/{id}")).StatusCode);

        // A forged POST (with a valid antiforgery token from another page) changes nothing.
        var forged = await PortalFactory.PostFormAsync(other, $"/Device/{id}/Schedule", Settings(false, "21:00", "06:30", "23:00", "08:00"), formPage: "/Account");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        Assert.Equal(before, await PortalFactory.SettingsAsync(mac));

        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/Admin/Devices")).StatusCode); // to /Denied
    }

    [Fact]
    public async Task PrimaryUserIsPinnedByTheFirstCheckIn()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02PIN000001");
        await PortalFactory.CheckInAsync(mac, "pinned01", null);
        await PortalFactory.CheckInAsync(mac, "visitor02", null); // a classmate signs in later

        var visitorsParent = await ParentWithChildrenAsync("parent2", "visitor02");
        Assert.Equal(HttpStatusCode.NotFound, (await visitorsParent.GetAsync($"/Device/{id}")).StatusCode);
        var ownersParent = await ParentWithChildrenAsync("parent1", "pinned01");
        Assert.Equal(HttpStatusCode.OK, (await ownersParent.GetAsync($"/Device/{id}")).StatusCode);
    }

    static Action<JsonObject> Users(params (string Username, string? FullName)[] users) => node =>
        node["users"] = new JsonArray([.. users.Select(u => new JsonObject
        {
            ["username"] = u.Username, ["exempt"] = false, ["agentConnected"] = true, ["fullName"] = u.FullName,
        })]);

    [Fact]
    public async Task ParentsSeeTheStudentsFullNameAndItSeesBoth()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02FULLNAME1");
        await PortalFactory.CheckInAsync(mac, "fullname01", null, change: Users(("FullName01", "  Jane Smith ")));
        var parent = await ParentWithChildrenAsync("fullnameparent", "fullname01");
        var admin = await portal.SignInAdminAsync();
        Assert.Contains("<h2>Jane Smith</h2>", await parent.GetStringAsync("/"));
        Assert.Contains("<h1 class=\"mb-0\">Jane Smith</h1>", await parent.GetStringAsync($"/Device/{id}"));
        Assert.Contains("<h1 class=\"mb-0\">Jane Smith (fullname01)</h1>", await admin.GetStringAsync($"/Device/{id}"));
        Assert.Contains("Jane Smith (fullname01)</a>", await admin.GetStringAsync("/Admin/Devices?q=jane"));

        // An older lulld (no full name), or a classmate's name, leaves it; the student's own rename changes it.
        await PortalFactory.CheckInAsync(mac, "fullname01", null);
        await PortalFactory.CheckInAsync(mac, "fullname01", null, change: Users(("visitor03", "Sam Visitor"), ("fullname01", null)));
        Assert.Contains("<h2>Jane Smith</h2>", await parent.GetStringAsync("/"));
        await PortalFactory.CheckInAsync(mac, "fullname01", null, change: Users(("fullname01", "Janet Smith")));
        Assert.Contains("<h2>Janet Smith</h2>", await parent.GetStringAsync("/"));

        // IT pins someone else: their username until their own check-in has their name.
        await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=PrimaryUser", new() { ["username"] = "fullname02" }, formPage: $"/Device/{id}");
        Assert.Contains("<h1 class=\"mb-0\">fullname02</h1>", await admin.GetStringAsync($"/Device/{id}"));
        await PortalFactory.CheckInAsync(mac, "fullname01", null, change: Users(("fullname01", "Janet Smith"), ("fullname02", "Alex Smith")));
        Assert.Contains("<h1 class=\"mb-0\">Alex Smith (fullname02)</h1>", await admin.GetStringAsync($"/Device/{id}"));
    }

    [Fact]
    public async Task AMacsScheduleHasItsOwnPageAndSavingLeadsBackToTheMac()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02SPLIT0001");
        await PortalFactory.CheckInAsync(mac, "split01", null);
        var parent = await ParentWithChildrenAsync("splitparent", "split01");

        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}"));
        Assert.DoesNotContain("name=\"schoolMode\"", page);
        Assert.Contains($"href=\"/Device/{id}/Schedule\"", page);
        Assert.Contains($"href=\"/Device/{id}/Schedule/Holidays\"", page);
        Assert.Equal(2, Regex.Matches(page, "Following the school schedule").Count);

        // Each page shows only its own.
        var weeks = await parent.GetStringAsync($"/Device/{id}/Schedule");
        Assert.Contains("name=\"schoolMode\"", weeks);
        Assert.DoesNotContain("name=\"holidayMode\"", weeks);
        Assert.DoesNotContain("handler=Lock", weeks);
        var holidays = await parent.GetStringAsync($"/Device/{id}/Schedule/Holidays");
        Assert.Contains("name=\"holidayMode\"", holidays);
        Assert.Contains("name=\"holidayView\" value=\"simple\" checked", holidays);
        Assert.DoesNotContain("name=\"holidayMode\" value=\"advanced\"", holidays); // that's the switch's now
        Assert.DoesNotContain("name=\"schoolMode\"", holidays);
        Assert.DoesNotContain("name=\"termView\"", holidays);

        var saved = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(false, "21:00", "06:30", "23:00", "08:00"));
        Assert.Equal($"/Device/{id}", saved.Headers.Location?.OriginalString);
        Assert.Contains("Saved. Applies within a few minutes.", await parent.GetStringAsync($"/Device/{id}"));

        // The same protection as the Mac's own page: not found, not refused.
        var other = await ParentWithChildrenAsync("splitother", "somebodyelse01");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Device/{id}/Schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Device/{id}/Schedule/Holidays")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.Browser().GetAsync($"/Device/{id}/Schedule")).StatusCode); // to sign in
    }

    [Fact]
    public async Task EachSchedulePageSavesOnlyWhatItShows()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02SPLIT0002");
        await PortalFactory.CheckInAsync(mac, "split02", null);
        var parent = await ParentWithChildrenAsync("splitparent2", "split02");
        var admin = await portal.SignInAdminAsync();

        // Holiday times, then school weeks: each leaves the other alone.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays",
            new() { ["holidayMode"] = "custom", ["holidayStart"] = "10:00", ["holidayEnd"] = "14:00" });
        Assert.Contains("Custom", await parent.GetStringAsync($"/Device/{id}"));
        Assert.Single(Regex.Matches(await parent.GetStringAsync($"/Device/{id}"), "Following the school schedule"));
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(true, "21:30", "06:30", "23:00", "08:00"));
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(new Lull.Portal.Api.WindowPayload("10:00", "14:00"), s.Holiday);
        Assert.Equal("21:30", s.SchoolNights!.Start);

        // The hint says what to avoid before they try, and doesn't talk about when Friday's times start.
        var weeksPage = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule"));
        Assert.Contains("Can't overlap Friday's school hours", weeksPage);
        Assert.DoesNotContain("On Fridays", weeksPage);

        // A school-week time IT set, which a parent couldn't: a parent saving the holidays doesn't trip over it, or lose it.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(admin, $"/Device/{id}/Schedule", Settings(true, "21:30", "09:00", "23:00", "08:00"));
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var saved = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", new() { ["holidayMode"] = "off" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal("09:00", s.SchoolNights!.End);
        Assert.Equal("off", s.Holiday is null ? "off" : "on");

        // Holidays "same as during term" over custom school weeks aren't following the school.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", new() { ["holidayMode"] = "school" });
        Assert.DoesNotContain("Following the school schedule", await parent.GetStringAsync($"/Device/{id}"));
    }

    [Fact]
    public async Task AMacsPageSaysWhenThereIsNoDowntime()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02SPLIT0003");
        await PortalFactory.CheckInAsync(mac, "split03", null);
        var parent = await ParentWithChildrenAsync("splitparent3", "split03");
        async Task<string[]> HintsAsync() => [.. Regex.Matches(await parent.GetStringAsync($"/Device/{id}"),
            "<span>School (?:weeks|holidays)</span><span class=\"small muted\">([^<]+)</span>").Select(m => m.Groups[1].Value)];
        async Task SaveAsync(string page, Dictionary<string, string> form)
        {
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, page, form)).StatusCode);
        }
        var weeks = $"/Device/{id}/Schedule";
        var holidays = $"/Device/{id}/Schedule/Holidays";
        Assert.Equal(["Following the school schedule", "Following the school schedule"], await HintsAsync());

        // Both kinds off: none in school weeks, and so none in holidays that are the same as term.
        await SaveAsync(weeks, Settings(false, "21:00", "06:30", "23:00", "08:00"));
        Assert.Equal(["No downtime", "No downtime"], await HintsAsync());

        // Only one of them off is still some downtime.
        await SaveAsync(weeks, new() { ["schoolMode"] = "off", ["weekendMode"] = "school" });
        Assert.Equal(["Custom", "Custom"], await HintsAsync());

        // An hour-by-hour schedule with no hours chosen, in term and in the holidays.
        await SaveAsync(weeks, new() { ["termView"] = "advanced" });
        await SaveAsync(holidays, new() { ["holidayView"] = "advanced" });
        Assert.Equal(["No downtime", "No downtime"], await HintsAsync());

        // Opted out of the holidays alone.
        await SaveAsync(weeks, new() { ["schoolMode"] = "school", ["weekendMode"] = "school" });
        await SaveAsync(holidays, new() { ["holidayMode"] = "off" });
        Assert.Equal(["Following the school schedule", "No downtime"], await HintsAsync());
    }

    [Fact]
    public async Task WithLockDownOffParentsAreOfferedOnlyWhatTheyCanDo()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02NOLOCK001");
        await PortalFactory.CheckInAsync(mac, "nolock01", null);
        var parent = await ParentWithChildrenAsync("nolockparent", "nolock01");
        var admin = await portal.SignInAdminAsync();
        await SavePolicyAsync(admin, Policy(lockOn: false));
        try
        {
            const string none = "No controls available at this time.";
            var revision = (await PortalFactory.SettingsAsync(mac)).Revision;

            // Thursday 11 pm, nothing scheduled: nothing to offer, and no word of the setting.
            await PortalFactory.CheckInAsync(mac, "nolock01", revision, change: n =>
            {
                n["phase"] = "normal";
                n["windowStart"] = null;
                n["windowEnd"] = null;
            });
            var page = await parent.GetStringAsync($"/Device/{id}");
            Assert.Contains(none, page);
            Assert.DoesNotContain("turned off", page);
            Assert.DoesNotContain("Lock down now", page);
            Assert.DoesNotContain("Lock down", await parent.GetStringAsync("/"));

            // IT isn't bound by it.
            page = await admin.GetStringAsync($"/Device/{id}");
            Assert.DoesNotContain(none, page);
            Assert.Contains("<h3>Lock down now</h3>", page);

            // In downtime there's something to do, so it says that instead.
            await PortalFactory.CheckInAsync(mac, "nolock01", revision);
            page = await parent.GetStringAsync($"/Device/{id}");
            Assert.Contains("Unlock now", page);
            Assert.DoesNotContain(none, page);
            Assert.DoesNotContain("turned off", page);
        }
        finally
        {
            await SavePolicyAsync(admin, Policy());
        }
    }

    [Fact]
    public async Task LockingDownTakesAChoiceAndThenAButton()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02CHOOSE001");
        await PortalFactory.CheckInAsync(mac, "choose01", null);
        var parent = await ParentWithChildrenAsync("chooseparent", "choose01");
        var revision = (await PortalFactory.SettingsAsync(mac)).Revision;
        await PortalFactory.CheckInAsync(mac, "choose01", revision, change: n =>
        {
            n["phase"] = "normal";
            n["windowStart"] = null;
            n["windowEnd"] = null;
        });
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("name=\"minutes\" value=\"60\" required", page);
        Assert.DoesNotContain("<button type=\"submit\" class=\"secondary\" name=\"minutes\"", page); // no button that locks by itself
        Assert.DoesNotContain("<button type=\"submit\" name=\"minutes\"", page);

        var nothing = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", [], formPage: $"/Device/{id}");
        Assert.Contains("Choose how long", await nothing.Content.ReadAsStringAsync());
        Assert.Null((await PortalFactory.SettingsAsync(mac)).LockUntil);
    }

    [Fact]
    public async Task HomePageListsWhatNeedsAttentionFirst()
    {
        var (fine, _) = await portal.RegisterMacAsync("C02ATTEND001");
        await PortalFactory.CheckInAsync(fine, "attend01", null, change: Users(("attend01", "Abe Attend")));
        var (quiet, quietId) = await portal.RegisterMacAsync("C02ATTEND002");
        await PortalFactory.CheckInAsync(quiet, "attend02", null, change: Users(("attend02", "Zed Attend")));
        var parent = await ParentWithChildrenAsync("attendparent", "attend01 attend02");
        int At(string page, string heading) => page.IndexOf($"<h2>{heading}</h2>", StringComparison.Ordinal);

        var page = await parent.GetStringAsync("/");
        Assert.True(At(page, "Abe Attend") < At(page, "Zed Attend"), "by name when all's well");

        portal.Clock.Now = portal.Clock.Now.AddMinutes(30);
        await PortalFactory.CheckInAsync(fine, "attend01", null, change: Users(("attend01", "Abe Attend")));
        page = await parent.GetStringAsync("/");
        Assert.True(At(page, "Zed Attend") < At(page, "Abe Attend"), "the Mac not heard from comes first");
    }

    [Fact]
    public async Task MacsAreListedByTheStudentsName()
    {
        // By username, zsorted01 would come last and asorted03 first.
        foreach (var (serial, user, name) in new[]
        {
            ("C02SORTED001", "zsorted01", "amy Sorted"), ("C02SORTED002", "msorted02", (string?)null), ("C02SORTED003", "asorted03", "Zoe Sorted"),
        })
        {
            var (mac, _) = await portal.RegisterMacAsync(serial);
            await PortalFactory.CheckInAsync(mac, user, null, change: Users((user, name)));
        }
        var page = await (await ParentWithChildrenAsync("sortedparent", "zsorted01 msorted02 asorted03")).GetStringAsync("/");
        int At(string heading) => page.IndexOf($"<h2>{heading}</h2>", StringComparison.Ordinal);
        Assert.True(At("amy Sorted") >= 0 && At("amy Sorted") < At("msorted02") && At("msorted02") < At("Zoe Sorted"), "any case, username when no name");
    }

    static Dictionary<string, string> Policy(string start = "08:30", string end = "15:30", string lockMaxHours = "12", bool lockOn = true)
    {
        var f = new Dictionary<string, string>
        {
            ["schoolStart"] = start, ["schoolEnd"] = end,
            ["lockMaxHours"] = lockMaxHours,
        };
        if (lockOn) f["lockEnabled"] = "true";
        return f;
    }

    /// <summary>Saves school hours, then parent controls (two sections of Settings), from one <see cref="Policy"/> form.</summary>
    static async Task SavePolicyAsync(HttpClient admin, Dictionary<string, string> form)
    {
        foreach (var page in new[] { "/Admin/Settings", "/Admin/Settings/ParentControls" })
        {
            var res = await PortalFactory.PostFormAsync(admin, page, form);
            Assert.True(res.StatusCode == HttpStatusCode.Redirect, await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task SettingsSectionsSaveOnlyTheirOwnPart()
    {
        var admin = await portal.SignInAdminAsync();
        var store = portal.Services.GetRequiredService<Data.PortalStore>();
        await SavePolicyAsync(admin, Policy());
        try
        {
            // School hours leaves parent controls alone, and the other way round.
            var hours = await PortalFactory.PostFormAsync(admin, "/Admin/Settings", Policy(start: "08:00", lockMaxHours: "3", lockOn: false));
            Assert.Equal(HttpStatusCode.Redirect, hours.StatusCode);
            var policy = await store.GetPolicyAsync(CancellationToken.None);
            Assert.Equal(new TimeOnly(8, 0), policy.SchoolHours.Start);
            Assert.Equal(new LockNowPolicy(true, 12 * 60), policy.LockNow);

            var controls = await PortalFactory.PostFormAsync(admin, "/Admin/Settings/ParentControls", Policy(lockMaxHours: "3", lockOn: false));
            Assert.Equal(HttpStatusCode.Redirect, controls.StatusCode);
            policy = await store.GetPolicyAsync(CancellationToken.None);
            Assert.Equal(new TimeOnly(8, 0), policy.SchoolHours.Start);
            Assert.Equal(new LockNowPolicy(false, 3 * 60), policy.LockNow);

            var refused = await PortalFactory.PostFormAsync(admin, "/Admin/Settings/ParentControls", Policy(lockMaxHours: "0"));
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("longest lock-down must be between", await refused.Content.ReadAsStringAsync());

            var old = await admin.GetAsync("/Admin/Setup");
            Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
            Assert.Equal("/Admin/Settings", old.Headers.Location?.OriginalString);
        }
        finally
        {
            await SavePolicyAsync(admin, Policy());
        }
    }

    static string Sydney(string local) =>
        new DateTimeOffset(DateTime.Parse(local), TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney").GetUtcOffset(DateTime.Parse(local)))
            .ToUniversalTime().ToString("O");

    [Fact]
    public async Task ParentsMayNotTouchSchoolHoursButItMay()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02SCHOOL001");
        await PortalFactory.CheckInAsync(mac, "school01", null);
        var parent = await ParentWithChildrenAsync("parent1", "school01");
        var admin = await portal.SignInAdminAsync();
        await SavePolicyAsync(admin, Policy());
        Assert.Contains("Mon–Fri 8:30 am – 3:30 pm", System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule")));

        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var ok = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(true, "20:00", "07:00", "16:00", "11:00"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var refused = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(true, "21:30", "09:00", "23:00", "08:00"));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("can&#x27;t overlap school hours", await refused.Content.ReadAsStringAsync());
        Assert.Equal("07:00", (await PortalFactory.SettingsAsync(mac)).SchoolNights!.End);

        // IT isn't limited.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var it = await PortalFactory.PostFormAsync(admin, $"/Device/{id}/Schedule", Settings(true, "21:30", "09:00", "23:00", "08:00"));
        Assert.Equal(HttpStatusCode.Redirect, it.StatusCode);

        // Moving school hours earlier trims the parent's (now IT's) 09:00 end.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await SavePolicyAsync(admin, Policy(start: "08:00"));
        Assert.Equal("08:00", (await PortalFactory.SettingsAsync(mac)).SchoolNights!.End);
        Assert.Contains("settings_clamped", await admin.GetStringAsync("/Admin/Audit"));
        await SavePolicyAsync(admin, Policy());
    }

    /// <summary>A grid's cells as a form posts them (week[0]=…, week[1]=…).</summary>
    static void Cells(Dictionary<string, string> form, string name, IEnumerable<int> cells)
    {
        foreach (var (cell, i) in cells.Select((c, i) => (c, i))) form[$"{name}[{i}]"] = cell.ToString();
    }

    [Fact]
    public async Task ParentSetsAnAdvancedScheduleAndGoesBack()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02GRID00001");
        await PortalFactory.CheckInAsync(mac, "grid01", null);
        var parent = await ParentWithChildrenAsync("parent1", "grid01");
        var admin = await portal.SignInAdminAsync();
        await SavePolicyAsync(admin, Policy()); // school 8:30 am – 3:30 pm

        // Simple first: custom school nights, which the advanced schedule keeps for going back (and older Macs).
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(true, "21:30", "06:30", "23:00", "08:00"));
        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule"));
        Assert.Contains("name=\"termView\" value=\"simple\" checked", page);
        Assert.Contains("aria-label=\"Mon 9 pm\" checked", page); // the grid starts from the simple settings
        Assert.Contains("aria-label=\"Mon 10 am: school hours\"", page); // not a checkbox
        Assert.Contains("week-grid.js", page);

        // Monday 3–5 pm (from 3:30, when school ends) and Saturday all day.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var form = new Dictionary<string, string> { ["termView"] = "advanced", ["holidayMode"] = "school" };
        Cells(form, "week", [15, 16, .. Enumerable.Range(5 * 24, 24)]);
        var res = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(new Lull.Portal.Api.WeekWindowPayload[] { new("Mon", "15:30", "17:00"), new("Sat", "00:00", "00:00") }, s.Week!);
        Assert.Equal("21:30", s.SchoolNights!.Start); // kept
        Assert.Contains("name=\"termView\" value=\"advanced\" checked", await parent.GetStringAsync($"/Device/{id}/Schedule"));

        // An hour wholly in school hours is refused, even if posted.
        Cells(form, "week", [10]);
        var refused = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("can&#x27;t be in school hours", await refused.Content.ReadAsStringAsync());

        // Holidays hour by hour, school hours and all.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        form = new() { ["termView"] = "advanced" };
        Cells(form, "week", [15]);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form);
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        form = new() { ["holidayView"] = "advanced" };
        Cells(form, "holidayWeek", [10, 11]);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", form);
        var store = portal.Services.GetRequiredService<Lull.Portal.Data.PortalStore>();
        Assert.Equal(ScheduleMode.Advanced, (await store.GetDeviceAsync(Guid.Parse(id), default))!.Settings.HolidayMode);
        var holidayPage = await parent.GetStringAsync($"/Device/{id}/Schedule/Holidays");
        Assert.Contains("name=\"holidayView\" value=\"advanced\" checked", holidayPage);
        Assert.Contains("name=\"holidayMode\" value=\"school\" checked", holidayPage); // kept for going back

        // New school hours change the windows: the Mac gets a new revision.
        var before = (await PortalFactory.SettingsAsync(mac)).Revision;
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await SavePolicyAsync(admin, Policy(end: "15:00"));
        var moved = await PortalFactory.SettingsAsync(mac);
        Assert.True(moved.Revision > before);
        Assert.Equal(new Lull.Portal.Api.WeekWindowPayload[] { new("Mon", "15:00", "16:00") }, moved.Week!);
        await SavePolicyAsync(admin, Policy());

        // Back to simple: the advanced schedule goes, the simple choices are as they were.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        form = new()
        {
            ["termView"] = "simple", ["schoolMode"] = "custom", ["schoolStart"] = "21:30", ["schoolEnd"] = "06:30",
            ["weekendMode"] = "custom", ["weekendStart"] = "23:00", ["weekendEnd"] = "08:00", ["holidayMode"] = "school",
        };
        Cells(form, "week", [15]);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form);
        var simple = await PortalFactory.SettingsAsync(mac);
        Assert.Null(simple.Week);
        Assert.Equal(ScheduleMode.Advanced, (await store.GetDeviceAsync(Guid.Parse(id), default))!.Settings.HolidayMode); // that's the holidays' page
        Assert.Equal("23:00", simple.Weekend!.Start);

        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", new() { ["holidayMode"] = "school" });
        Assert.Equal(ScheduleMode.School, (await store.GetDeviceAsync(Guid.Parse(id), default))!.Settings.HolidayMode);
    }

    [Fact]
    public async Task FollowTheSchoolOrCustomiseEachKind()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02FOLLOW001");
        await PortalFactory.CheckInAsync(mac, "follow01", null); // reports the school's Mon–Fri 20:00–07:00
        var parent = await ParentWithChildrenAsync("parent2", "follow01");
        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule"));
        Assert.Contains("Follow the school", page);
        // Following the school comes first, and a new Mac does (and keeps its usual settings in the holidays).
        Assert.InRange(page.IndexOf("Follow the school"), 0, page.IndexOf("No downtime"));
        Assert.Contains("name=\"schoolMode\" value=\"school\" checked", page);
        Assert.Contains("name=\"weekendMode\" value=\"school\" checked", page);
        Assert.Contains("name=\"holidayMode\" value=\"school\" checked", await parent.GetStringAsync($"/Device/{id}/Schedule/Holidays"));
        Assert.Contains("Mon–Thu 8:00 pm – 7:00 am", page); // school nights (Sun–Thu starts) on this Mac
        Assert.Contains("Fri 8:00 pm – 7:00 am", page); // weekend

        // Customise school nights only; the weekend keeps following the school.
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var form = new Dictionary<string, string>
        {
            ["schoolMode"] = "custom", ["schoolStart"] = "21:00", ["schoolEnd"] = "06:00",
            ["weekendMode"] = "school", ["weekendStart"] = "10:00", ["weekendEnd"] = "11:00", // ignored: not customised
        };
        Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form)).StatusCode);
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(new Lull.Portal.Api.WindowPayload("21:00", "06:00"), s.SchoolNights);
        Assert.Null(s.Weekend);

        // Times of a kind that follows the school aren't checked against school hours (they're IT's).
        form["schoolMode"] = "school";
        form["weekendStart"] = "09:00";
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", form)).StatusCode);
        var back = await PortalFactory.SettingsAsync(mac);
        Assert.Null(back.SchoolNights);
        Assert.Null(back.Weekend);
        Assert.True(back.Enabled);
    }

    [Fact]
    public async Task ParentLocksDownNowAndResetsIt()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02LOCK00001");
        await PortalFactory.CheckInAsync(mac, "locked01", null); // Thursday 11 pm in Sydney
        var parent = await ParentWithChildrenAsync("parent1", "locked01");
        var card = await parent.GetStringAsync("/");
        Assert.DoesNotContain("Lock down now", card); // parents lock down from the Mac's Settings page
        Assert.Contains("Change pending", card); // the Mac hasn't applied the portal's settings yet
        Assert.DoesNotContain("Unlock now", card); // so what it reports may be out of date
        await CheckInAppliedAsync(mac, "locked01");
        Assert.Contains("Unlock now", await parent.GetStringAsync("/")); // the fixture reports a scheduled window in progress
        var revision = (await PortalFactory.SettingsAsync(mac)).Revision;
        await PortalFactory.CheckInAsync(mac, "locked01", revision, change: n =>
        {
            n["phase"] = "normal"; // and no downtime coming up for the lock-down to run into
            n["windowStart"] = null;
            n["windowEnd"] = null;
        });
        var settingsPage = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("class=\"state\"", settingsPage); // the same status block as the card
        Assert.Contains(">2 hours<", settingsPage);
        Assert.DoesNotContain("4 hours", settingsPage);
        Assert.Contains("From:", await parent.GetStringAsync($"/Device/{id}/Schedule"));
        Assert.Contains($"href=\"/Device/{id}\"", card);

        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        var lockRes = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, lockRes.StatusCode);
        var s = await PortalFactory.SettingsAsync(mac);
        var now = portal.Clock.Now;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), s.LockFrom);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(now.AddHours(1).ToUnixTimeSeconds()), s.LockUntil);
        var home = await parent.GetStringAsync("/");
        Assert.Contains("Lock-down pending", home); // until the Mac checks in with it
        Assert.Contains("Locks within a few minutes", home);
        Assert.DoesNotContain("Unlock now", home); // a lock-down ends with End lock-down
        Assert.Contains("End lock-down", home);
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.DoesNotContain("Unlock now", page);
        Assert.DoesNotContain("<h3>Lock down now</h3>", page);
        Assert.Contains("End lock-down", page);
        var raw = await mac.GetStringAsync("/api/device/v1/settings");
        Assert.Matches("\"lockUntil\":\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z\"", raw);

        portal.Clock.Now = portal.Clock.Now.AddMinutes(10);
        var reset = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: "/");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var after = await PortalFactory.SettingsAsync(mac);
        Assert.Null(after.LockUntil);
        Assert.Null(after.ReleasedAt);
        Assert.True(after.Revision > s.Revision);
        var audit = await (await portal.SignInAdminAsync()).GetStringAsync("/Admin/Audit");
        Assert.Contains("lock_now", audit);
        Assert.Contains("overrides_reset", audit);
    }

    [Fact]
    public async Task ReleaseAScheduledWindow()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02RELEASE01");
        await CheckInAppliedAsync(mac, "release01"); // the fixture reports "locked" (school night)
        var parent = await ParentWithChildrenAsync("parent2", "release01");
        Assert.Contains("Unlock now", await parent.GetStringAsync($"/Device/{id}"));
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.NotNull(s.ReleasedAt);
        Assert.Null(s.LockUntil);
    }

    /// <summary>A check-in in <paramref name="phase"/> for tonight's 9:30 pm to 6:30 am school-night window (Thursday 22 Oct, Sydney).</summary>
    static Task CheckInTonightAsync(HttpClient mac, string username, long? revision, string phase) =>
        PortalFactory.CheckInAsync(mac, username, revision, change: n =>
        {
            n["phase"] = phase;
            n["windowStart"] = Sydney("2026-10-22 21:30");
            n["windowEnd"] = Sydney("2026-10-23 06:30");
        });

    [Fact]
    public async Task SkipTheDowntimeStartingSoonThenResetIt()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:20"));
        var (mac, id) = await portal.RegisterMacAsync("C02SKIP00001");
        var parent = await ParentWithChildrenAsync("parent3", "skip01");
        await CheckInTonightAsync(mac, "skip01", null, "warning");
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("<h2>Controls</h2>", page);
        Assert.DoesNotContain("Skip next downtime", page); // the Mac hasn't applied these settings yet

        var revision = (await PortalFactory.SettingsAsync(mac)).Revision;
        await CheckInTonightAsync(mac, "skip01", revision, "warning");
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Skip next downtime", page);
        Assert.DoesNotContain("Unlock now", page); // not locked
        Assert.DoesNotContain("Cancel skip", page);

        var start = DateTimeOffset.Parse(Sydney("2026-10-22 21:30"));
        var stale = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Skip",
            new() { ["start"] = start.AddDays(-1).ToUnixTimeSeconds().ToString() }, formPage: $"/Device/{id}");
        Assert.Contains("no downtime starting soon", await stale.Content.ReadAsStringAsync());
        var skip = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Skip",
            new() { ["start"] = start.ToUnixTimeSeconds().ToString() }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, skip.StatusCode);
        Assert.Equal(start, (await PortalFactory.SettingsAsync(mac)).ReleasedAt); // lulld cuts that window to nothing
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Skipping the downtime at 9:30 pm today", page);
        Assert.DoesNotContain("Skip next downtime", page);
        Assert.Contains("Cancel skip", page);

        var reset = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Null((await PortalFactory.SettingsAsync(mac)).ReleasedAt);
        Assert.Contains("overrides_reset", await (await portal.SignInAdminAsync()).GetStringAsync("/Admin/Audit"));
    }

    [Fact]
    public async Task ChangingTheTimesCancelsASkipOfTheOldOnes()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:20"));
        var (mac, id) = await portal.RegisterMacAsync("C02SKIPCHG01");
        var parent = await ParentWithChildrenAsync("parent7", "skipchg01");
        await CheckInTonightAsync(mac, "skipchg01", (await PortalFactory.SettingsAsync(mac)).Revision, "warning");
        var start = DateTimeOffset.Parse(Sydney("2026-10-22 21:30"));
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Skip", new() { ["start"] = start.ToUnixTimeSeconds().ToString() }, formPage: $"/Device/{id}");
        Assert.Equal(start, (await PortalFactory.SettingsAsync(mac)).ReleasedAt);

        // Saving the same schedule leaves the skip alone.
        var school = new Dictionary<string, string> { ["schoolMode"] = "school", ["weekendMode"] = "school", ["holidayMode"] = "school" };
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", school);
        Assert.Equal(start, (await PortalFactory.SettingsAsync(mac)).ReleasedAt);

        // New times: lulld would skip a window starting at 9:30 pm, or cut one around it, so the skip goes.
        var custom = new Dictionary<string, string>(school) { ["schoolMode"] = "custom", ["schoolStart"] = "21:00", ["schoolEnd"] = "06:30" };
        var saved = await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", custom);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Null((await PortalFactory.SettingsAsync(mac)).ReleasedAt);
        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}"));
        Assert.Contains("The skip was cancelled: it was for the old times.", page);
        Assert.DoesNotContain("Skipping the downtime", page);
        Assert.Contains("releaseCancelled", await (await portal.SignInAdminAsync()).GetStringAsync("/Admin/Audit"));
    }

    [Fact]
    public async Task AChangeFromAnotherTabOrParentIsntOverwritten()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 23:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02CONCUR001");
        var first = await ParentWithChildrenAsync("parent8a", "concur01");
        var second = await ParentWithChildrenAsync("parent8b", "concur01"); // the other parent
        await CheckInTonightAsync(mac, "concur01", (await PortalFactory.SettingsAsync(mac)).Revision, "locked");
        var shown = (await PortalFactory.SettingsAsync(mac)).Revision; // both pages show this

        Assert.Equal(HttpStatusCode.Redirect,
            (await PortalFactory.PostFormAsync(first, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}")).StatusCode);
        var released = await PortalFactory.SettingsAsync(mac);

        // The second parent's page still has the old revision: each control, and the schedule, is refused.
        foreach (var handler in new[] { "Release", "ResetOverrides", "Lock" })
        {
            var stale = await PortalFactory.PostFormAsync(second, $"/Device/{id}?handler={handler}",
                new() { ["revision"] = shown.ToString(), ["minutes"] = "30" }, formPage: $"/Device/{id}");
            Assert.Contains("changed after the page loaded", System.Net.WebUtility.HtmlDecode(await stale.Content.ReadAsStringAsync()));
        }
        var schedule = await PortalFactory.PostFormAsync(second, $"/Device/{id}/Schedule",
            new() { ["revision"] = shown.ToString(), ["schoolMode"] = "off", ["weekendMode"] = "off", ["holidayMode"] = "school" });
        Assert.Contains("changed after the page loaded", System.Net.WebUtility.HtmlDecode(await schedule.Content.ReadAsStringAsync()));
        Assert.Equal(released, await PortalFactory.SettingsAsync(mac)); // nothing changed

        // And if it changes between loading and saving, the store refuses it too.
        var store = portal.Services.GetRequiredService<Lull.Portal.Data.PortalStore>();
        Assert.Null(await store.ResetOverridesAsync(Guid.Parse(id), shown, "test", default));
        Assert.NotNull(await store.ResetOverridesAsync(Guid.Parse(id), released.Revision, "test", default));
    }

    [Fact]
    public async Task ReleaseIsOfferedOnlyWhileLockedAndResetUndoesIt()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 18:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02OVERRD001");
        var parent = await ParentWithChildrenAsync("parent4", "overrd01");
        await CheckInTonightAsync(mac, "overrd01", null, "normal");
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.DoesNotContain("Unlock now", page);
        Assert.DoesNotContain("Skip next downtime", page);
        Assert.All(ResetButtons, b => Assert.DoesNotContain(b, page));

        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 23:00"));
        parent = await ParentWithChildrenAsync("parent4", "overrd01"); // sign-in cookies follow the test clock (8 h)
        await CheckInTonightAsync(mac, "overrd01", (await PortalFactory.SettingsAsync(mac)).Revision, "locked");
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlock now", page);
        Assert.DoesNotContain("<h3>Lock down now</h3>", page); // already in scheduled downtime
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocked until 6:30 am tomorrow", page);
        Assert.DoesNotContain("Unlock now", page); // the Mac still reports "locked" until its next check-in
        Assert.Contains("Cancel unlock", page);
        Assert.DoesNotContain("<h3>Lock down now</h3>", page); // it would cancel the release: reset first
        await CheckInAppliedAsync(mac, "overrd01", n =>
        {
            n["phase"] = "normal"; // released: tomorrow night's window is next
            n["windowStart"] = Sydney("2026-10-23 21:30");
            n["windowEnd"] = Sydney("2026-10-24 06:30");
        });
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("To lock down now, cancel the unlock first.", page);
        Assert.DoesNotContain("<h3>Lock down now</h3>", page);
        var refused = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "30" }, formPage: $"/Device/{id}");
        Assert.Contains("no longer available", await refused.Content.ReadAsStringAsync());
        Assert.Null((await PortalFactory.SettingsAsync(mac)).LockUntil);

        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: $"/Device/{id}");
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Null(s.ReleasedAt);
        Assert.Null(s.LockUntil);
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.DoesNotContain("<h3>Lock down now</h3>", page); // the Mac reports "normal" until it applies the reset
        Assert.All(ResetButtons, b => Assert.DoesNotContain(b, page));
        Assert.Contains("Waiting for Mac to check in", page);
        await CheckInTonightAsync(mac, "overrd01", s.Revision, "locked"); // downtime resumes
        Assert.Contains("Unlock now", await parent.GetStringAsync($"/Device/{id}"));
    }

    [Fact]
    public async Task ALockDownThatRunsIntoScheduledDowntimeIsHandledAsThatDowntime()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02MERGE0001");
        var parent = await ParentWithChildrenAsync("parent5", "merge01");
        await CheckInTonightAsync(mac, "merge01", (await PortalFactory.SettingsAsync(mac)).Revision, "normal"); // 9:30 pm to 6:30 am next

        var lockRes = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, lockRes.StatusCode);
        var home = await parent.GetStringAsync("/");
        Assert.Contains("until 6:30 am tomorrow", home); // lulld merges the two: locked until the scheduled downtime ends
        Assert.DoesNotContain("Unlock now", home); // not until the Mac has the lock-down
        Assert.Contains("End lock-down", home); // which can still be cancelled
        Assert.Contains("End lock-down", await parent.GetStringAsync($"/Device/{id}"));
        await PortalFactory.CheckInAsync(mac, "merge01", (await PortalFactory.SettingsAsync(mac)).Revision, change: n =>
        {
            n["phase"] = "locked";
            n["windowStart"] = Sydney("2026-10-22 21:00");
            n["windowEnd"] = Sydney("2026-10-23 06:30");
        });
        home = await parent.GetStringAsync("/");
        Assert.Contains("Unlocks 6:30 am tomorrow", home);
        Assert.Contains("Unlock now", home);
        Assert.DoesNotContain("End lock-down", home);
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocks 6:30 am tomorrow", page);
        Assert.Contains("Unlock now", page);
        Assert.DoesNotContain("End lock-down", page);
        Assert.DoesNotContain("<h3>Lock down now</h3>", page);

        Assert.Contains("Downtime still starts at 9:30 pm today", page); // next to Unlock now

        // Before 9:30 pm, Unlock now ends just the lock-down: the scheduled downtime still starts on time.
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:10"));
        var release = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-22 21:10")), s.LockUntil); // parted from the 9:30 pm window
        Assert.Null(s.ReleasedAt);
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.DoesNotContain("<b>Locked down</b>", page);
        Assert.Contains("Lock-down ended. Downtime still starts at 9:30 pm today.", page);
        // The Mac still reports one locked window until it applies this, which mustn't read as scheduled downtime.
        Assert.DoesNotContain("Unlock now", page);
        var again = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}/Schedule");
        Assert.Contains("Wait for the Mac to check in", await again.Content.ReadAsStringAsync());
        Assert.Null((await PortalFactory.SettingsAsync(mac)).ReleasedAt);

        // Locked down again, and now past 9:30 pm: the Mac reports one window, and Unlock now ends both.
        await CheckInTonightAsync(mac, "merge01", s.Revision, "normal");
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:15"));
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        s = await PortalFactory.SettingsAsync(mac);
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:40"));
        await PortalFactory.CheckInAsync(mac, "merge01", s.Revision, change: n =>
        {
            n["phase"] = "locked";
            n["windowStart"] = Sydney("2026-10-22 21:15");
            n["windowEnd"] = Sydney("2026-10-23 06:30");
        });
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocks 6:30 am tomorrow", page);
        Assert.Contains("Unlock now", page);
        Assert.DoesNotContain("Downtime still starts at", page); // already started: Unlock now ends both
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
        s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-22 21:40")), s.ReleasedAt);
        // The lock-down stays, so the Mac keeps it merged with tonight's window and the release cuts both.
        Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-22 22:15")), s.LockUntil);
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocked until 6:30 am tomorrow", page);
        Assert.DoesNotContain("<b>Locked down</b>", page);
        Assert.DoesNotContain("Unlock now", page);
    }

    [Fact]
    public async Task ALockDownThatEndsBeforeScheduledDowntimeStaysSeparate()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 20:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02MERGE0002");
        var parent = await ParentWithChildrenAsync("parent5", "merge02");
        await CheckInTonightAsync(mac, "merge02", (await PortalFactory.SettingsAsync(mac)).Revision, "normal");
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("until 9:00 pm today", page);
        Assert.Contains("End lock-down", page);
        Assert.DoesNotContain("Unlock now", page);
    }

    [Fact]
    public async Task ResettingALockDownOffersNoControlsUntilTheMacHasIt()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 19:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02RSTOVR001");
        var parent = await ParentWithChildrenAsync("parent6", "rstovr01");
        await CheckInTonightAsync(mac, "rstovr01", (await PortalFactory.SettingsAsync(mac)).Revision, "normal");
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        await PortalFactory.CheckInAsync(mac, "rstovr01", (await PortalFactory.SettingsAsync(mac)).Revision, change: n =>
        {
            n["phase"] = "locked"; // the lock-down, on its own
            n["windowStart"] = Sydney("2026-10-22 19:00");
            n["windowEnd"] = Sydney("2026-10-22 20:00");
        });

        var reset = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        // The Mac still reports "locked", but that was the lock-down: no Unlock now, nor anything else, until it checks in.
        foreach (var html in new[] { await parent.GetStringAsync("/"), await parent.GetStringAsync($"/Device/{id}") })
        {
            Assert.Contains("Change pending", html);
            Assert.DoesNotContain("Unlock now", html);
            Assert.All(ResetButtons, b => Assert.DoesNotContain(b, html));
            Assert.DoesNotContain("<h3>Lock down now</h3>", html);
        }
        var stale = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}/Schedule");
        Assert.Contains("Wait for the Mac to check in", await stale.Content.ReadAsStringAsync());
        var again = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); // nothing left to reset
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Null(s.ReleasedAt);
        Assert.Null(s.LockUntil);

        await CheckInTonightAsync(mac, "rstovr01", s.Revision, "normal");
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("<h3>Lock down now</h3>", page);
        Assert.DoesNotContain("Unlock now", page);
        Assert.DoesNotContain("The other controls come back", page);
    }

    [Fact]
    public async Task ControlsWaitForAnOverdueCheckIn()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 23:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02OVERDUE01");
        var parent = await ParentWithChildrenAsync("parent6", "overdue01");
        await CheckInTonightAsync(mac, "overdue01", (await PortalFactory.SettingsAsync(mac)).Revision, "locked"); // until 6:30 am
        Assert.Contains("Unlock now", await parent.GetStringAsync($"/Device/{id}"));

        // Its last poll at 6:28 am, then at 6:45 am it hasn't said it unlocked: it may be asleep, or may not have.
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-23 06:28"));
        await CheckInTonightAsync(mac, "overdue01", (await PortalFactory.SettingsAsync(mac)).Revision, "locked");
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-23 06:45"));
        parent = await ParentWithChildrenAsync("parent6", "overdue01"); // sign-in cookies follow the test clock (8 h)
        foreach (var html in new[] { await parent.GetStringAsync("/"), await parent.GetStringAsync($"/Device/{id}") })
        {
            Assert.Contains("Waiting for check-in", html);
            Assert.Contains("Due to unlock at 6:30 am today. Not heard from since.", html);
            Assert.DoesNotContain("Unlock now", html);
            Assert.DoesNotContain("<h3>Lock down now</h3>", html);
            Assert.DoesNotContain("Offline?", html); // not until 20 minutes without a check-in
        }
        var release = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}/Schedule");
        Assert.Contains("Wait for the Mac to check in", await release.Content.ReadAsStringAsync());
        Assert.Null((await PortalFactory.SettingsAsync(mac)).ReleasedAt);

        await CheckInAppliedAsync(mac, "overdue01", n =>
        {
            n["phase"] = "normal";
            n["windowStart"] = Sydney("2026-10-23 21:30");
            n["windowEnd"] = Sydney("2026-10-24 06:30");
        });
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.DoesNotContain("Waiting for check-in", page);
        Assert.Contains("<h3>Lock down now</h3>", page);
    }

    [Fact]
    public async Task SettingsTheMacRefusedDontHoldBackTheControls()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 23:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02REFUSE001");
        var parent = await ParentWithChildrenAsync("parent6", "refuse01");
        await CheckInTonightAsync(mac, "refuse01", null, "locked");
        Assert.DoesNotContain("Unlock now", await parent.GetStringAsync($"/Device/{id}"));
        // It will never apply them, so waiting for it to would leave no controls at all.
        await PortalFactory.CheckInAsync(mac, "refuse01", null, change: n =>
        {
            n["phase"] = "locked";
            n["settingsError"] = "bad time";
            n["windowStart"] = Sydney("2026-10-22 21:30");
            n["windowEnd"] = Sydney("2026-10-23 06:30");
        });
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("The Mac refused the last settings", page);
        Assert.Contains("Unlock now", page);
    }

    [Fact]
    public async Task ALockDownCanBeShortenedOrExtendedWithoutUnlocking()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 20:00"));
        var (mac, id) = await portal.RegisterMacAsync("C02CHANGE001");
        var parent = await ParentWithChildrenAsync("parent9", "change09");
        await CheckInTonightAsync(mac, "change09", (await PortalFactory.SettingsAsync(mac)).Revision, "normal"); // 9:30 pm to 6:30 am next
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "60" }, formPage: $"/Device/{id}");
        // The Mac applies the latest settings and reports the lock-down (with any downtime it merged with).
        async Task MacHasItAsync(string from, string to)
        {
            var revision = (await PortalFactory.SettingsAsync(mac)).Revision;
            await PortalFactory.CheckInAsync(mac, "change09", revision, change: n =>
            {
                n["phase"] = "locked";
                n["windowStart"] = Sydney(from);
                n["windowEnd"] = Sydney(to);
            });
        }
        Assert.DoesNotContain("<h3>Update lock-down</h3>", await parent.GetStringAsync($"/Device/{id}")); // not until the Mac has it
        await MacHasItAsync("2026-10-22 20:00", "2026-10-22 21:00");
        var page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocks 9:00 pm today", page);
        Assert.Contains("<h3>Update lock-down</h3>", page);
        Assert.Contains("End lock-down", page);

        // Shorter, from now: still on its own.
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 20:10"));
        var shorter = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "30" }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, shorter.StatusCode);
        var s = await PortalFactory.SettingsAsync(mac);
        Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-22 20:40")), s.LockUntil);
        Assert.Null(s.ReleasedAt); // never unlocked in between
        Assert.Contains("Lock-down changed: now until 8:40 pm today.", System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}")));

        // Longer, into tonight's downtime: it then counts as that downtime, and Unlock now before
        // 9:30 pm ends just the lock-down (the portal kept when tonight's downtime starts).
        await MacHasItAsync("2026-10-22 20:10", "2026-10-22 20:40");
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 20:15"));
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["until"] = "22:00" }, formPage: $"/Device/{id}");
        Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-22 22:00")), (await PortalFactory.SettingsAsync(mac)).LockUntil);
        await MacHasItAsync("2026-10-22 20:15", "2026-10-23 06:30"); // merged with tonight's window
        page = await parent.GetStringAsync($"/Device/{id}");
        Assert.Contains("Unlocks 6:30 am tomorrow", page);
        Assert.DoesNotContain("<h3>Update lock-down</h3>", page); // handled as that downtime now
        Assert.Contains("Downtime still starts at 9:30 pm today", page);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
        s = await PortalFactory.SettingsAsync(mac);
        Assert.Null(s.ReleasedAt); // not a release, which would have ended tonight's downtime too
        Assert.False(s.LockUntil > portal.Clock.Now); // the lock-down is over (it began this minute, so it's gone)
    }

    [Fact]
    public async Task EndingSaysWhatItEnds()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-22 21:20"));
        var (mac, id) = await portal.RegisterMacAsync("C02LABELS001");
        var parent = await ParentWithChildrenAsync("parent10", "labels10");
        await CheckInTonightAsync(mac, "labels10", (await PortalFactory.SettingsAsync(mac)).Revision, "warning");
        var start = DateTimeOffset.Parse(Sydney("2026-10-22 21:30"));
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Skip", new() { ["start"] = start.ToUnixTimeSeconds().ToString() }, formPage: $"/Device/{id}");
        await CheckInTonightAsync(mac, "labels10", (await PortalFactory.SettingsAsync(mac)).Revision, "warning");
        var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}"));
        Assert.Contains(">Cancel skip<", page);
        Assert.Contains("The downtime at 9:30 pm today goes ahead", page);
        Assert.Contains("To lock down now, cancel the skip first.", page);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=ResetOverrides", [], formPage: $"/Device/{id}");
        Assert.Contains("Skip cancelled. The downtime at 9:30 pm today goes ahead.", System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}")));
        Assert.Null((await PortalFactory.SettingsAsync(mac)).ReleasedAt);
    }

    [Fact]
    public async Task NoLockDownOrReleaseDuringSchoolHours()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-20 13:00")); // Tuesday 1 pm
        var (mac, id) = await portal.RegisterMacAsync("C02SCHOOLHR1");
        await CheckInAppliedAsync(mac, "schoolhr01");
        var parent = await ParentWithChildrenAsync("parent2", "schoolhr01");
        var admin = await portal.SignInAdminAsync();
        await SavePolicyAsync(admin, Policy(lockMaxHours: "2"));
        try
        {
            var page = await parent.GetStringAsync($"/Device/{id}"); // the fixture reports scheduled downtime
            Assert.Contains("can&#x27;t be unlocked during school hours", page);
            await CheckInAppliedAsync(mac, "schoolhr01", n => n["phase"] = "normal");
            Assert.Contains("isn&#x27;t available during school hours", await parent.GetStringAsync($"/Device/{id}"));
            var refused = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "30" }, formPage: $"/Device/{id}");
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            var noRelease = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Release", [], formPage: $"/Device/{id}");
            Assert.Equal(HttpStatusCode.OK, noRelease.StatusCode);
            var s = await PortalFactory.SettingsAsync(mac);
            Assert.Null(s.LockUntil);
            Assert.Null(s.ReleasedAt);

            // 4 pm, after school: lock-downs are allowed again, up to IT's 2 h maximum.
            portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-20 16:00"));
            var tooLong = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "240" }, formPage: $"/Device/{id}");
            Assert.Contains("at most 2 h", await tooLong.Content.ReadAsStringAsync());
            var ok = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["until"] = "17:30" }, formPage: $"/Device/{id}");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            Assert.Equal(DateTimeOffset.Parse(Sydney("2026-10-20 17:30")), (await PortalFactory.SettingsAsync(mac)).LockUntil);

            portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-21 10:00")); // Wednesday: IT still can
            admin = await portal.SignInAdminAsync(); // sign-in cookies follow the test clock (8 h)
            await CheckInAppliedAsync(mac, "schoolhr01", n => n["phase"] = "normal"); // IT too waits for the last change
            var it = await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "240" }, formPage: $"/Device/{id}");
            Assert.Equal(HttpStatusCode.Redirect, it.StatusCode);
        }
        finally
        {
            portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-21 10:30"));
            await SavePolicyAsync(await portal.SignInAdminAsync(), Policy());
        }
    }

    [Fact]
    public async Task SchoolHolidaysLetParentsLockDownInSchoolHours()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-20 13:00")); // Tuesday 1 pm
        var (mac, id) = await portal.RegisterMacAsync("C02HOLIDAY01");
        await CheckInAppliedAsync(mac, "holiday01", n => n["phase"] = "normal");
        var parent = await ParentWithChildrenAsync("parent3", "holiday01");
        var admin = await portal.SignInAdminAsync();
        await SavePolicyAsync(admin, Policy());
        var holiday = new Dictionary<string, string> { ["name"] = "Mid-term break", ["first"] = "2026-10-19", ["last"] = "2026-10-23" };
        try
        {
            Assert.Contains("isn&#x27;t available during school hours", await parent.GetStringAsync($"/Device/{id}"));

            var added = await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=AddHoliday", holiday, formPage: "/Admin/Settings/Holidays");
            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            var setup = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/Admin/Settings/Holidays"));
            Assert.Contains("Mid-term break", setup);
            Assert.Contains("Mon 19 Oct – Fri 23 Oct 2026", setup);
            var overlap = await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=AddHoliday",
                new() { ["first"] = "2026-10-23", ["last"] = "2026-10-26" }, formPage: "/Admin/Settings/Holidays");
            Assert.Contains("overlap Mid-term break", System.Net.WebUtility.HtmlDecode(await overlap.Content.ReadAsStringAsync()));

            // Saving school hours keeps the holiday.
            await SavePolicyAsync(admin, Policy());
            Assert.Contains("Mid-term break", await admin.GetStringAsync("/Admin/Settings/Holidays"));

            var ok = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Lock", new() { ["minutes"] = "30" }, formPage: $"/Device/{id}");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            Assert.Equal(portal.Clock.Now.AddMinutes(30), (await PortalFactory.SettingsAsync(mac)).LockUntil);
            Assert.Contains("holiday_added", await admin.GetStringAsync("/Admin/Audit"));

            var removed = await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=RemoveHoliday", new() { ["first"] = "2026-10-19" }, formPage: "/Admin/Settings/Holidays");
            Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
            Assert.DoesNotContain("Mid-term break", await admin.GetStringAsync("/Admin/Settings/Holidays"));
            Assert.Contains("holiday_removed", await admin.GetStringAsync("/Admin/Audit"));
        }
        finally
        {
            await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=RemoveHoliday", new() { ["first"] = "2026-10-19" }, formPage: "/Admin/Settings/Holidays");
        }
    }

    [Fact]
    public async Task HolidaySettingIsSentWithTheHolidayPeriods()
    {
        portal.Clock.Now = DateTimeOffset.Parse(Sydney("2026-10-06 18:00")); // Tuesday, a fortnight before
        var (mac, id) = await portal.RegisterMacAsync("C02HOLIDAY02");
        await PortalFactory.CheckInAsync(mac, "holiday02", null, change: n => n["phase"] = "normal");
        var parent = await ParentWithChildrenAsync("parent4", "holiday02");
        var admin = await portal.SignInAdminAsync();
        try
        {
            // Always offered; it applies once the school sets holidays. The fixture's Mac has Lull 0.2.0.
            var page = System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule/Holidays"));
            Assert.Contains("School holidays", page);
            Assert.Contains("Your school hasn't set any holidays yet", page);
            Assert.Contains("This Mac needs Lull 0.3.0 or later", page);

            // Daytime holiday times are fine: no school hours apply.
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            var form = new Dictionary<string, string> { ["holidayMode"] = "custom", ["holidayStart"] = "10:00", ["holidayEnd"] = "14:00" };
            Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", form)).StatusCode);
            var s = await PortalFactory.SettingsAsync(mac);
            Assert.Equal(new Lull.Portal.Api.WindowPayload("10:00", "14:00"), s.Holiday);
            Assert.Null(s.HolidayPeriods); // no holidays yet

            // IT adds one: the Mac gets the holiday period with a new revision.
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=AddHoliday",
                new() { ["name"] = "Term 3 holidays", ["first"] = "2026-10-19", ["last"] = "2026-10-23" }, formPage: "/Admin/Settings/Holidays");
            var after = await PortalFactory.SettingsAsync(mac);
            Assert.True(after.Revision > s.Revision);
            Assert.Equal([new Lull.Portal.Api.HolidayPeriodPayload("2026-10-16T15:30", "2026-10-25")], after.HolidayPeriods!);
            Assert.Contains("Next: Term 3 holidays", System.Net.WebUtility.HtmlDecode(await parent.GetStringAsync($"/Device/{id}/Schedule/Holidays")));

            // School ending earlier moves the start, with a new revision; a change to when school starts doesn't.
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            await SavePolicyAsync(admin, Policy(end: "15:00"));
            var earlier = await PortalFactory.SettingsAsync(mac);
            Assert.True(earlier.Revision > after.Revision);
            Assert.Equal("2026-10-16T15:00", earlier.HolidayPeriods![0].Start);
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            await SavePolicyAsync(admin, Policy(start: "08:00", end: "15:00"));
            Assert.Equal(earlier.Revision, (await PortalFactory.SettingsAsync(mac)).Revision);

            // Back to the usual settings: no holiday fields at all.
            portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
            form["holidayMode"] = "school";
            Assert.Equal(HttpStatusCode.Redirect, (await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule/Holidays", form)).StatusCode);
            var usual = await PortalFactory.SettingsAsync(mac);
            Assert.Null(usual.Holiday);
            Assert.Null(usual.HolidayPeriods);
        }
        finally
        {
            await PortalFactory.PostFormAsync(admin, "/Admin/Settings/Holidays?handler=RemoveHoliday", new() { ["first"] = "2026-10-19" }, formPage: "/Admin/Settings/Holidays");
            await SavePolicyAsync(admin, Policy());
        }
    }

    [Fact]
    public async Task DeregisterNeedsConfirmationThenTheMacIsGoneUntilEnrolledAgain()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02DEREG0001");
        await PortalFactory.CheckInAsync(mac, "dereg01", null);
        var parent = await ParentWithChildrenAsync("parent1", "dereg01");
        var admin = await portal.SignInAdminAsync();
        var page = await admin.GetStringAsync($"/Device/{id}");
        Assert.Contains("De-register device", page);
        Assert.Contains("local configuration", page);
        Assert.Contains("re-registered", page);
        Assert.DoesNotContain("De-register device", await parent.GetStringAsync($"/Device/{id}"));

        // Parents can't do it, and IT must tick the box.
        var byParent = await PortalFactory.PostFormAsync(parent, $"/Device/{id}?handler=Deregister", new() { ["confirm"] = "true" }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.NotFound, byParent.StatusCode);
        var unconfirmed = await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Deregister", [], formPage: $"/Device/{id}");
        Assert.Contains("Tick the box", await unconfirmed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);

        var res = await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Deregister", new() { ["confirm"] = "true" }, formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        // The Mac's token now gets 410 Gone (not 401, which would make it register again).
        Assert.Equal(HttpStatusCode.Gone, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);
        var checkIn = await mac.PostAsync("/api/device/v1/checkin",
            new StringContent(Fixtures.Text("checkin.json"), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Gone, checkIn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync($"/Device/{id}")).StatusCode);
        var home = await parent.GetStringAsync("/");
        Assert.DoesNotContain($"/Device/{id}", home);
        Assert.Contains("No Mac", home); // the child is listed as having no Mac ("No Macs found…" / "No Mac found yet…")
        Assert.Contains("De-registered", await admin.GetStringAsync($"/Device/{id}"));
        Assert.Contains("De-registered", await admin.GetStringAsync("/Admin/Devices"));
        var audit = await admin.GetStringAsync("/Admin/Audit");
        Assert.Contains("deregistered", audit);
        Assert.Contains("C02DEREG0001", audit);

        // Enrolled again: a fresh start with the school defaults and no pinned user.
        var (fresh, freshId) = await portal.RegisterMacAsync("C02DEREG0001");
        Assert.Equal(id, freshId);
        Assert.True((await PortalFactory.SettingsAsync(fresh)).Enabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync($"/Device/{id}")).StatusCode); // not pinned yet
        await PortalFactory.CheckInAsync(fresh, "dereg01", null);
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync($"/Device/{id}")).StatusCode);
        Assert.Contains("re_enrolled", await admin.GetStringAsync("/Admin/Audit"));
    }

    [Fact]
    public async Task ResetLetsTheSerialRegisterOnceMore()
    {
        var (oldMac, id) = await portal.RegisterMacAsync("C02RESET0001");
        await PortalFactory.CheckInAsync(oldMac, "reset01", null);
        var admin = await portal.SignInAdminAsync();
        var reset = await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Reset", [], formPage: $"/Device/{id}");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);

        var (newMac, newId) = await portal.RegisterMacAsync("C02RESET0001");
        Assert.Equal(id, newId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldMac.GetAsync("/api/device/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newMac.GetAsync("/api/device/v1/settings")).StatusCode);
        // Settings and pinned user carry over.
        var parent = await ParentWithChildrenAsync("parent1", "reset01");
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync($"/Device/{id}")).StatusCode);
    }

    [Fact]
    public async Task AReimagedMacRegistersAgainByItselfAndStartsAfresh()
    {
        const string hardware = "6B29FC40-CA47-1067-B31D-00DD010662DA";
        var (oldMac, id) = await portal.RegisterMacAsync("C02REIMAGE01", hardware);
        await PortalFactory.CheckInAsync(oldMac, "reimage01", null);
        var oldParent = await ParentWithChildrenAsync("parent1", "reimage01");
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(oldParent, $"/Device/{id}/Schedule", Settings(true, "21:00", "06:30", "23:00", "08:00"));
        var before = await PortalFactory.SettingsAsync(oldMac);
        Assert.NotNull(before.SchoolNights);

        // Re-imaged for another student: same serial and hardware UUID (in any case), no token.
        var (newMac, newId) = await portal.RegisterMacAsync("C02REIMAGE01", hardware.ToLowerInvariant());
        Assert.Equal(id, newId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldMac.GetAsync("/api/device/v1/settings")).StatusCode);
        var after = await PortalFactory.SettingsAsync(newMac);
        Assert.Null(after.SchoolNights); // back to following the school
        Assert.Null(after.Weekend);
        Assert.True(after.Revision > before.Revision, "a new revision, or lulld would refuse the change");

        // Not pinned until the new student signs in, and then it's their parent's Mac.
        Assert.Equal(HttpStatusCode.NotFound, (await oldParent.GetAsync($"/Device/{id}")).StatusCode);
        await PortalFactory.CheckInAsync(newMac, "reimage02", null);
        var newParent = await ParentWithChildrenAsync("parent2", "reimage02");
        Assert.Equal(HttpStatusCode.OK, (await newParent.GetAsync($"/Device/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await oldParent.GetAsync($"/Device/{id}")).StatusCode);
        var admin = await portal.SignInAdminAsync();
        Assert.Contains("re_registered_same_hardware", await admin.GetStringAsync("/Admin/Audit?q=C02REIMAGE01"));
        Assert.DoesNotContain("register_refused", await admin.GetStringAsync("/Admin/Audit?q=C02REIMAGE01"));

        // Other hardware with the same serial is still refused.
        var other = await portal.CreateClient().PostAsJsonAsync("/api/device/v1/register",
            new Lull.Portal.Api.RegisterRequest("C02REIMAGE01", Guid.NewGuid().ToString(), PortalFactory.Secret, "0.2.0"));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newMac.GetAsync("/api/device/v1/settings")).StatusCode);
    }

    [Fact]
    public async Task ResettingBeforeAReinstallKeepsSettingsAndUser()
    {
        const string hardware = "0D3C0A2B-5E7F-4A61-9B8C-1F2E3D4C5B6A";
        var (oldMac, id) = await portal.RegisterMacAsync("C02KEEP00001", hardware);
        await PortalFactory.CheckInAsync(oldMac, "keep01", null);
        var parent = await ParentWithChildrenAsync("parent1", "keep01");
        portal.Clock.Now = portal.Clock.Now.AddMinutes(1);
        await PortalFactory.PostFormAsync(parent, $"/Device/{id}/Schedule", Settings(true, "21:00", "06:30", "23:00", "08:00"));
        var before = await PortalFactory.SettingsAsync(oldMac);
        var admin = await portal.SignInAdminAsync();
        await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Reset", [], formPage: $"/Device/{id}");

        var (newMac, _) = await portal.RegisterMacAsync("C02KEEP00001", hardware);
        Assert.Equal(before, await PortalFactory.SettingsAsync(newMac));
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync($"/Device/{id}")).StatusCode);
        Assert.Contains("re_registered", await admin.GetStringAsync("/Admin/Audit?q=C02KEEP00001"));
        Assert.DoesNotContain("re_registered_same_hardware", await admin.GetStringAsync("/Admin/Audit?q=C02KEEP00001"));
    }
}
