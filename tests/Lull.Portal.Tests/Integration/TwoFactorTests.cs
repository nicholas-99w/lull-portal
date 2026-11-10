using System.Net;
using Lull.Portal.Auth;
using Lull.Portal.Data;

namespace Lull.Portal.Tests.Integration;

/// <summary>Two-step sign-in (an authenticator app's codes): required for local admins, not for parents.</summary>
public class TwoFactorTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static string Decode(string html) => WebUtility.HtmlDecode(html);

    Task<HttpResponseMessage> Password(HttpClient b, string email, string password) =>
        PortalFactory.PostFormAsync(b, "/Login", new() { ["email"] = email, ["password"] = password });

    Task<HttpResponseMessage> Code(HttpClient b, string ticket, string code) =>
        PortalFactory.PostFormAsync(b, "/Login?handler=Code", new() { ["ticket"] = ticket, ["code"] = code }, formPage: "/Login");

    /// <summary>A local admin who has set a password but not two-step sign-in, signed in (and so held at /TwoFactor).</summary>
    async Task<(UserRecord User, HttpClient Browser)> NewAdminAsync(string email)
    {
        var (user, code) = await portal.Users.CreateAsync(email, "Admin", Roles.Admin, [], "test", default);
        Assert.Equal(SignInOutcome.Ok, (await portal.Users.CompleteSetupAsync(email, code, PortalFactory.AdminPassword, default)).Outcome);
        var b = portal.Browser();
        Assert.Equal("/TwoFactor", (await Password(b, email, PortalFactory.AdminPassword)).Headers.Location?.OriginalString);
        return (user, b);
    }

    [Fact]
    public async Task TheRightPasswordOnlyLeadsToTheCodeStep()
    {
        var b = portal.Browser();
        var page = await Password(b, PortalFactory.AdminEmail, PortalFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("code from your authenticator app", html);
        Assert.False(page.Headers.Contains("Set-Cookie") && page.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("lull.portal=")));
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/Admin/Devices")).StatusCode); // not signed in yet
        var ticket = PortalFactory.TicketOnPage(html);

        var wrong = Decode(await (await Code(b, ticket, "000000")).Content.ReadAsStringAsync());
        Assert.Contains("That code isn't right", wrong);
        Assert.Equal(ticket, PortalFactory.TicketOnPage(wrong)); // try again without the password

        var code = await portal.TwoFactorCodeAsync(PortalFactory.AdminEmail);
        var ok = await Code(b, ticket, code);
        Assert.Equal("/Admin/Devices", ok.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Devices")).StatusCode);

        // Each code works once, even with a fresh password step.
        var again = portal.Browser();
        var ticket2 = PortalFactory.TicketOnPage(await (await Password(again, PortalFactory.AdminEmail, PortalFactory.AdminPassword)).Content.ReadAsStringAsync());
        Assert.Contains("That code isn't right", Decode(await (await Code(again, ticket2, code)).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ATicketExpiresCantBeForgedAndDiesWithAPasswordReset()
    {
        var b = portal.Browser();
        var ticket = PortalFactory.TicketOnPage(await (await Password(b, PortalFactory.AdminEmail, PortalFactory.AdminPassword)).Content.ReadAsStringAsync());
        Assert.Contains("took too long", Decode(await (await Code(b, ticket + "x", await portal.TwoFactorCodeAsync(PortalFactory.AdminEmail))).Content.ReadAsStringAsync()));

        portal.Clock.Now = portal.Clock.Now.Add(Lull.Portal.Pages.LoginModel.TicketLifetime).AddSeconds(1);
        Assert.Contains("took too long", Decode(await (await Code(b, ticket, await portal.TwoFactorCodeAsync(PortalFactory.AdminEmail))).Content.ReadAsStringAsync()));

        // A second admin's password is reset (by the owner) between the two steps: the ticket is no good.
        var (other, held) = await NewAdminAsync("ticket-admin@school.test");
        Assert.Equal(HttpStatusCode.Redirect, (await portal.SetUpTwoFactorAsync(held, other.Email)).StatusCode);
        var b2 = portal.Browser();
        var ticket2 = PortalFactory.TicketOnPage(await (await Password(b2, other.Email, PortalFactory.AdminPassword)).Content.ReadAsStringAsync());
        var code2 = await portal.TwoFactorCodeAsync(other.Email);
        var owner = (await portal.Users.FindByEmailAsync(PortalFactory.AdminEmail, default))!;
        await portal.Users.NewSetupCodeAsync(other.Id, owner.Id, "test", default);
        Assert.Contains("That code isn't right", Decode(await (await Code(b2, ticket2, code2)).Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task WrongCodesLockTheAccountAndTheRightPasswordDoesntResetTheCount()
    {
        var (admin, held) = await NewAdminAsync("guess-admin@school.test");
        await portal.SetUpTwoFactorAsync(held, admin.Email);
        string? last = null;
        for (int i = 0; i < UserStore.MaxFailedAttempts; i++)
        {
            var b = portal.Browser();
            var ticket = PortalFactory.TicketOnPage(await (await Password(b, admin.Email, PortalFactory.AdminPassword)).Content.ReadAsStringAsync());
            last = Decode(await (await Code(b, ticket, "000000")).Content.ReadAsStringAsync());
        }
        Assert.Contains("Too many attempts", last);
        Assert.True((await portal.Users.GetAsync(admin.Id, default))!.IsLocked(portal.Clock.Now));
    }

    [Fact]
    public async Task ALocalAdminWithoutItSetsItUpBeforeAnythingElse()
    {
        var (admin, b) = await NewAdminAsync("new-admin@school.test");
        var other = portal.Browser(); // signed in elsewhere too, also held
        Assert.Equal("/TwoFactor", (await Password(other, admin.Email, PortalFactory.AdminPassword)).Headers.Location?.OriginalString);
        foreach (var path in new[] { "/Admin/Devices", "/Admin/Users", "/Account", "/" })
            Assert.Equal("/TwoFactor", (await b.GetAsync(path)).Headers.Location?.OriginalString);

        var page = Decode(await b.GetStringAsync("/TwoFactor"));
        Assert.Contains("otpauth://totp/", page);
        var wrong = await PortalFactory.PostFormAsync(b, "/TwoFactor",
            new() { ["pending"] = System.Text.RegularExpressions.Regex.Match(page, "name=\"pending\" value=\"([^\"]+)\"").Groups[1].Value, ["code"] = "000000" });
        Assert.Contains("That code isn't right", Decode(await wrong.Content.ReadAsStringAsync()));
        Assert.False((await portal.Users.GetAsync(admin.Id, default))!.HasTwoFactor);

        var ok = await portal.SetUpTwoFactorAsync(b, admin.Email);
        Assert.Equal("/Admin/Devices", ok.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Users")).StatusCode); // this session carries on
        Assert.StartsWith("http://localhost/Login", (await other.GetAsync("/Admin/Users")).Headers.Location?.ToString()); // the other one is signed out
        Assert.Contains("On since", Decode(await b.GetStringAsync("/TwoFactor")));
        Assert.Contains("two_factor_enabled", await b.GetStringAsync("/Admin/Audit"));

        // From now on, the code after the password; and a new setup code clears it (a lost phone).
        Assert.Equal(HttpStatusCode.Redirect, (await portal.PasswordAndCodeAsync(portal.Browser(), admin.Email, PortalFactory.AdminPassword)).StatusCode);
        var owner = (await portal.Users.FindByEmailAsync(PortalFactory.AdminEmail, default))!;
        await portal.Users.NewSetupCodeAsync(admin.Id, owner.Id, "test", default);
        Assert.False((await portal.Users.GetAsync(admin.Id, default))!.HasTwoFactor);
    }

    [Fact]
    public async Task AnOldWeakAdminPasswordIsChangedFirstThenTwoStepIsSetUp()
    {
        var (user, code) = await portal.Users.CreateAsync("legacy-admin@school.test", "Legacy", Roles.Admin, [], "test", default);
        await portal.Users.CompleteSetupAsync(user.Email, code, "Short-Pass!", default, checkRules: false); // as before the rules
        var b = portal.Browser();
        Assert.Equal("/Account", (await Password(b, user.Email, "Short-Pass!")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Account")).StatusCode); // not bounced on to /TwoFactor
        Assert.Equal("/Account", (await b.GetAsync("/TwoFactor")).Headers.Location?.OriginalString);
        var changed = await PortalFactory.PostFormAsync(b, "/Account",
            new() { ["current"] = "Short-Pass!", ["password"] = PortalFactory.AdminPassword, ["confirm"] = PortalFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal("/TwoFactor", (await b.GetAsync("/Admin/Devices")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.SetUpTwoFactorAsync(b, user.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Devices")).StatusCode);
    }

    [Fact]
    public async Task TheKeyIsShownAsAQrCodeALinkAndText()
    {
        var (_, b) = await NewAdminAsync("qr-admin@school.test");
        var page = Decode(await b.GetStringAsync("/TwoFactor"));
        var svg = System.Text.RegularExpressions.Regex.Match(page, "<div class=\"qr\"[^>]*>(<svg.*?</svg>)</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(svg);
        // Only shapes: nothing the CSP would block, nothing that could run.
        var elements = System.Text.RegularExpressions.Regex.Matches(svg, "<([a-z]+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Subset(new HashSet<string> { "svg", "rect", "path" }, elements);
        Assert.DoesNotContain("style", svg);
        Assert.Contains("viewBox=", svg);

        // The link (and so the QR code, made from it) carries the key typed in by hand.
        var keyText = System.Text.RegularExpressions.Regex.Match(page, "<code class=\"big\">([A-Z2-7 ]+)</code>").Groups[1].Value.Replace(" ", "");
        var link = System.Text.RegularExpressions.Regex.Match(page, "href=\"(otpauth://totp/[^\"]+)\"").Groups[1].Value;
        Assert.Contains($"secret={keyText}&", link);
        Assert.Contains("qr-admin%40school.test", link);
    }

    [Fact]
    public async Task AKeyFromAnotherAccountsPageIsRefused()
    {
        var (a, ba) = await NewAdminAsync("key-a@school.test");
        var (_, bb) = await NewAdminAsync("key-b@school.test");
        var page = Decode(await ba.GetStringAsync("/TwoFactor"));
        var pending = System.Text.RegularExpressions.Regex.Match(page, "name=\"pending\" value=\"([^\"]+)\"").Groups[1].Value;
        var res = await PortalFactory.PostFormAsync(bb, "/TwoFactor", new() { ["pending"] = pending, ["code"] = "123456" });
        Assert.Contains("That took too long", Decode(await res.Content.ReadAsStringAsync()));
        Assert.False((await portal.Users.GetAsync(a.Id, default))!.HasTwoFactor);
    }

    [Fact]
    public async Task ParentsSignInWithoutIt()
    {
        var parent = await portal.ParentAsync("nocode01", "kid01");
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync("/")).StatusCode);
        var page = Decode(await parent.GetStringAsync("/TwoFactor"));
        Assert.Contains("for admin accounts", page);
        Assert.DoesNotContain("otpauth://", page);
        Assert.DoesNotContain("Two-step sign-in</a>", await parent.GetStringAsync("/Account"));
    }
}
