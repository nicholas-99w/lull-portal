using System.Net;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lull.Portal.Tests.Integration;

/// <summary>Settings, Sign-in: the .env's values at first, then the owner's.</summary>
public class AccountSettingsTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    PortalSettings Settings => portal.Services.GetRequiredService<PortalSettings>();

    [Fact]
    public async Task TheOwnerSetsHowLongTheAuditLogIsKept()
    {
        var owner = await portal.SignInAdminAsync();
        Assert.Contains("server&#x27;s .env values for now", await owner.GetStringAsync("/Admin/Settings/SignIn"));
        Assert.Equal(365, (await Settings.GetAsync(default)).AuditRetentionDays);

        var refused = await PortalFactory.PostFormAsync(owner, "/Admin/Settings/SignIn", new() { ["auditRetentionDays"] = "-1" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Keep audit log entries for 0 (forever)", await refused.Content.ReadAsStringAsync());

        // Without Microsoft sign-in, local sign-in stays on whatever's posted.
        var saved = await PortalFactory.PostFormAsync(owner, "/Admin/Settings/SignIn", new() { ["auditRetentionDays"] = "30", ["localSignIn"] = "false" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(new AccountSettings(true, false, 30), await Settings.GetAsync(default));
        Assert.Contains("Entries are kept for 30 days.", await owner.GetStringAsync("/Admin/Audit"));
        Assert.Contains("account_settings_changed", await owner.GetStringAsync("/Admin/Audit"));
        Assert.Contains("Set here", await owner.GetStringAsync("/Admin/Settings/SignIn"));
        await PortalFactory.PostFormAsync(owner, "/Admin/Settings/SignIn", new() { ["auditRetentionDays"] = "365" });
    }

    [Fact]
    public async Task OtherAdminsCanLookButNotChange()
    {
        const string email = "settings-admin@school.test", password = "A-Long-Admin-Password";
        var (admin, code) = await portal.Users.CreateAsync(email, "Second", Roles.Admin, [], "test", default);
        await portal.Users.CompleteSetupAsync(email, code, password, default);
        await portal.EnableTwoFactorAsync(admin.Id);
        var other = await portal.SignInAsync(email, password);

        var page = await other.GetStringAsync("/Admin/Settings/SignIn");
        Assert.Contains("Only the portal owner can change these", page);
        Assert.Contains("<fieldset disabled=\"disabled\">", page);
        var res = await PortalFactory.PostFormAsync(other, "/Admin/Settings/SignIn", new() { ["auditRetentionDays"] = "1" });
        Assert.NotEqual("/Admin/Settings/SignIn", res.Headers.Location?.OriginalString); // refused, not saved
        Assert.NotEqual(1, (await Settings.GetAsync(default)).AuditRetentionDays);
    }
}

/// <summary>With Microsoft sign-in, the owner can turn local sign-in off and on.</summary>
public class EntraAccountSettingsTests(EntraPortalFactory portal) : IClassFixture<EntraPortalFactory>
{
    [Fact]
    public async Task TheOwnerCanTurnLocalSignInOff()
    {
        var owner = await portal.SignInAdminAsync();
        try
        {
            var saved = await PortalFactory.PostFormAsync(owner, "/Admin/Settings/SignIn",
                new() { ["auditRetentionDays"] = "365", ["linkLocalAccounts"] = "true" }); // local sign-in unticked
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            var login = await portal.Browser().GetStringAsync("/Login");
            Assert.DoesNotContain("name=\"password\"", login);
            Assert.Contains("/Login?local=true", login);
            Assert.Equal(new AccountSettings(false, true, 365), await portal.Services.GetRequiredService<PortalSettings>().GetAsync(default));
            // No new local accounts meanwhile.
            Assert.Contains("Local accounts are turned off (Settings, Sign-in)",
                System.Net.WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Users/Edit")));
        }
        finally
        {
            await PortalFactory.PostFormAsync(owner, "/Admin/Settings/SignIn", new() { ["auditRetentionDays"] = "365", ["localSignIn"] = "true" });
        }
        Assert.Contains("name=\"password\"", await portal.Browser().GetStringAsync("/Login"));
    }
}
