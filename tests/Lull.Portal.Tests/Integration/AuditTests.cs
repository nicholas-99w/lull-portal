using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lull.Portal.Api;
using Lull.Portal.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Lull.Portal.Tests.Integration;

/// <summary>The audit log: refused registrations audited once, searched and paged, old entries deleted.</summary>
public class AuditTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static string Decode(string html) => WebUtility.HtmlDecode(html);

    static int Count(string html, string text) => Regex.Matches(html, Regex.Escape(text)).Count;

    Task<HttpResponseMessage> RegisterAgainAsync(string serial) =>
        portal.CreateClient().PostAsJsonAsync("/api/device/v1/register",
            new RegisterRequest(serial, Guid.NewGuid().ToString(), PortalFactory.Secret, "0.2.0"));

    [Fact]
    public async Task ARefusedSerialIsAuditedOnceUntilItRegistersAgain()
    {
        var (_, id) = await portal.RegisterMacAsync("C02REFUSE001");
        // lulld retries a 409 at every poll: every attempt is refused, only the first is audited.
        for (int i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Conflict, (await RegisterAgainAsync("C02REFUSE001")).StatusCode);
        var admin = await portal.SignInAdminAsync();
        Assert.Equal(1, Count(await admin.GetStringAsync("/Admin/Audit?q=C02REFUSE001"), "register_refused"));
        Assert.Contains("Trying to register", await admin.GetStringAsync($"/Device/{id}"));

        // IT resets it and it registers: the hint goes, and a later refusal is audited afresh.
        await PortalFactory.PostFormAsync(admin, $"/Device/{id}?handler=Reset", [], formPage: $"/Device/{id}");
        await portal.RegisterMacAsync("C02REFUSE001");
        Assert.DoesNotContain("Trying to register", await admin.GetStringAsync($"/Device/{id}"));
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAgainAsync("C02REFUSE001")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAgainAsync("C02REFUSE001")).StatusCode);
        Assert.Equal(2, Count(await admin.GetStringAsync("/Admin/Audit?q=C02REFUSE001"), "register_refused"));
    }

    [Fact]
    public async Task TheLogIsPagedAndSearchedByWhoWhatSerialOrDetails()
    {
        for (int i = 1; i <= 30; i++)
        {
            var (mac, _) = await portal.RegisterMacAsync($"C02AUDIT{i:D3}");
            await PortalFactory.CheckInAsync(mac, $"auditkid{i:D3}", null); // registered + primary_user_pinned
        }
        var admin = await portal.SignInAdminAsync();
        var first = Decode(await admin.GetStringAsync("/Admin/Audit?q=C02AUDIT"));
        Assert.Contains("1–50 of 60 entries.", first);
        Assert.Contains("C02AUDIT030", first); // newest first
        Assert.DoesNotContain("C02AUDIT001<", first);
        var second = Decode(await admin.GetStringAsync("/Admin/Audit?q=C02AUDIT&p=2"));
        Assert.Contains("51–60 of 60 entries.", second);
        Assert.Contains("C02AUDIT001<", second);

        Assert.Contains("1–2 of 2 entries.", Decode(await admin.GetStringAsync("/Admin/Audit?q=c02audit007"))); // any case
        Assert.Contains("1–1 of 1 entries.", Decode(await admin.GetStringAsync("/Admin/Audit?q=AUDITKID007"))); // in the details
        Assert.Contains("primary_user_pinned", await admin.GetStringAsync("/Admin/Audit?q=pinned"));
        Assert.Contains("No entries match", Decode(await admin.GetStringAsync("/Admin/Audit?q=nothing-like-this")));
        Assert.Contains("kept for 365 days", await admin.GetStringAsync("/Admin/Audit"));
    }

    [Fact]
    public async Task EntriesOlderThanTheCutOffAreDeletedAndThatIsAudited()
    {
        var store = portal.Services.GetRequiredService<PortalStore>();
        var now = portal.Clock.Now;
        portal.Clock.Now = now.AddDays(-400);
        try
        {
            await portal.RegisterMacAsync("C02OLDAUDIT1");
        }
        finally
        {
            portal.Clock.Now = now;
        }
        await portal.RegisterMacAsync("C02NEWAUDIT1");

        Assert.Equal(1, await store.PruneAuditAsync(now.AddDays(-365), default));
        Assert.Equal(0, await store.PruneAuditAsync(now.AddDays(-365), default));
        var admin = await portal.SignInAdminAsync();
        Assert.Contains("No entries match", Decode(await admin.GetStringAsync("/Admin/Audit?q=C02OLDAUDIT1")));
        Assert.Contains("registered", await admin.GetStringAsync("/Admin/Audit?q=C02NEWAUDIT1"));
        var pruned = await admin.GetStringAsync("/Admin/Audit?q=audit_pruned");
        Assert.Contains("1–1 of 1 entries.", Decode(pruned));
        Assert.Contains("&quot;deleted&quot;: 1", pruned);
    }
}
