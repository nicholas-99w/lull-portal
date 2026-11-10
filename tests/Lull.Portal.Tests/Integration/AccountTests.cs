using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Lull.Portal.Auth;
using Lull.Portal.Data;

namespace Lull.Portal.Tests.Integration;

/// <summary>Parent and admin accounts: first sign-in, passwords, lockout, sessions, user management and CSV import.</summary>
public class AccountTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static string Decode(string html) => WebUtility.HtmlDecode(html);

    static string CodeOnPage(string html) =>
        Regex.Match(Decode(html), "<code class=\"big\">([A-Z2-9]{4}-[A-Z2-9]{4})</code>").Groups[1].Value;

    Task<HttpResponseMessage> Setup(HttpClient b, string email, string code, string password, string? confirm = null) =>
        PortalFactory.PostFormAsync(b, "/Setup", new() { ["email"] = email, ["code"] = code, ["password"] = password, ["confirm"] = confirm ?? password });

    Task<HttpResponseMessage> Login(HttpClient b, string email, string password) =>
        PortalFactory.PostFormAsync(b, "/Login", new() { ["email"] = email, ["password"] = password });

    [Fact]
    public async Task AdminCreatesAParentWhoSetsAPasswordWithTheSetupCode()
    {
        var admin = await portal.SignInAdminAsync();
        var created = await PortalFactory.PostFormAsync(admin, "/Admin/Users/Edit",
            new() { ["name"] = "Jane Smith", ["email"] = "Jane.Smith@Example.com", ["role"] = "Parent", ["students"] = "jsmith26, ASmith29" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var page = await created.Content.ReadAsStringAsync();
        var code = CodeOnPage(page);
        Assert.NotEmpty(code);
        Assert.Contains("jane.smith@example.com", Decode(await admin.GetStringAsync("/Admin/Users")));
        Assert.Contains("Waiting for first sign-in", await admin.GetStringAsync("/Admin/Users"));

        // Before setup: no password works, and the login page doesn't say the account exists.
        var early = await Login(portal.Browser(), "jane.smith@example.com", "Wrong-Pass-0");
        Assert.Contains("don&#x27;t match", await early.Content.ReadAsStringAsync());

        var parent = portal.Browser();
        var wrong = await Setup(parent, "jane.smith@example.com", "AAAA-AAAA", "Jane-Pass-4321");
        Assert.Contains("don&#x27;t match", await wrong.Content.ReadAsStringAsync());
        var weak = await Setup(parent, "jane.smith@example.com", code, "1234");
        Assert.Contains("needs at least 8 characters, an uppercase letter", Decode(await weak.Content.ReadAsStringAsync()));
        var noSpecial = await Setup(parent, "jane.smith@example.com", code, "JaneSmith2026");
        Assert.Contains("needs a special character", Decode(await noSpecial.Content.ReadAsStringAsync()));
        var mismatch = await Setup(parent, "jane.smith@example.com", code, "Jane-Pass-4321", "Jane-Pass-4322");
        Assert.Contains("don&#x27;t match", await mismatch.Content.ReadAsStringAsync());
        var ok = await Setup(parent, "jane.smith@example.com", code.ToLowerInvariant(), "Jane-Pass-4321");
        Assert.Equal("/", ok.Headers.Location?.OriginalString);
        Assert.Contains("No Macs found yet for asmith29, jsmith26", Decode(await parent.GetStringAsync("/")));

        // The code is single-use; the password works from now on.
        var reuse = await Setup(portal.Browser(), "jane.smith@example.com", code, "Wrong-Pass-9");
        Assert.Contains("already used", Decode(await reuse.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await Login(portal.Browser(), "jane.smith@example.com", "Jane-Pass-4321")).StatusCode);
        Assert.Contains("password_set", await admin.GetStringAsync("/Admin/Audit"));
        Assert.DoesNotContain(code, await admin.GetStringAsync("/Admin/Audit")); // codes are never logged
    }

    [Fact]
    public async Task UnknownEmailAndWrongPasswordLookTheSameAndLockoutAfterFive()
    {
        await portal.ParentAsync("lock01", "kid01");
        var unknown = Decode(await (await Login(portal.Browser(), "nobody@parents.test", "Nobody-Pass-1")).Content.ReadAsStringAsync());
        var wrong = Decode(await (await Login(portal.Browser(), "lock01@parents.test", "Wrong-Pass-9")).Content.ReadAsStringAsync());
        Assert.Contains("That email and password don't match", unknown);
        Assert.Contains("That email and password don't match", wrong);

        for (int i = 0; i < 3; i++) await Login(portal.Browser(), "lock01@parents.test", "Wrong-Pass-9");
        // Locked now, but a wrong password still gets the same answer as an unknown email, and so does the right one.
        var fifth = Decode(await (await Login(portal.Browser(), "lock01@parents.test", "Wrong-Pass-9")).Content.ReadAsStringAsync());
        Assert.Contains("That email and password don't match", fifth);
        for (int i = 0; i < 5; i++) await Login(portal.Browser(), "nobody@parents.test", "Nobody-Pass-1");
        Assert.Contains("That email and password don't match",
            Decode(await (await Login(portal.Browser(), "nobody@parents.test", "Nobody-Pass-1")).Content.ReadAsStringAsync()));
        var right = Decode(await (await Login(portal.Browser(), "lock01@parents.test", PortalFactory.ParentPassword)).Content.ReadAsStringAsync());
        // The right password is refused, and looks like a wrong one: a different answer would let guessing carry on.
        Assert.Contains("That email and password don't match", right);
        Assert.DoesNotContain("Too many attempts", right);

        var admin = await portal.SignInAdminAsync();
        var user = (await portal.Users.FindByEmailAsync("lock01@parents.test", default))!;
        Assert.Contains("Locked", await admin.GetStringAsync("/Admin/Users"));
        await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{user.Id}?handler=Unlock", [], formPage: $"/Admin/Users/Edit/{user.Id}");
        Assert.Equal(HttpStatusCode.Redirect, (await Login(portal.Browser(), "lock01@parents.test", PortalFactory.ParentPassword)).StatusCode);
    }

    [Fact]
    public async Task ALockedAccountAnswersTheRightSetupCodeLikeAWrongOne()
    {
        var (user, code) = await portal.Users.CreateAsync("lock02@parents.test", "Lock Two", Roles.Parent, ["kid02"], "test", default);
        for (int i = 0; i < UserStore.MaxFailedAttempts; i++)
            await Setup(portal.Browser(), user.Email, "AAAA-AAAA", PortalFactory.ParentPassword);

        var right = await Setup(portal.Browser(), user.Email, code, PortalFactory.ParentPassword);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode); // not signed in
        var page = Decode(await right.Content.ReadAsStringAsync());
        Assert.Contains("That email and setup code don't match", page);
        Assert.DoesNotContain("Too many attempts", page);
        Assert.False((await portal.Users.GetAsync(user.Id, default))!.HasPassword);
    }

    [Fact]
    public async Task FailedSignInsAreLoggedWithoutWhatWasTyped()
    {
        await portal.ParentAsync("logged01", "kid01");
        var user = (await portal.Users.FindByEmailAsync("logged01@parents.test", default))!;

        await Login(portal.Browser(), "ghost@parents.test", "Typed-Ghost-Pass1");
        await Login(portal.Browser(), user.Email, "Typed-Wrong-Pass1");
        await Setup(portal.Browser(), user.Email, "BBBB-BBBB", "Typed-Setup-Pass1");
        await Login(portal.Browser(), user.Email, PortalFactory.ParentPassword); // signed in: nothing to log

        var lines = portal.SignInLogs.Lines.ToList();
        Assert.Contains(lines, l => l.Contains("password: UnknownAccount, account none"));
        var mine = lines.Where(l => l.Contains(user.Id.ToString())).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, l => l.Contains("password: WrongPassword"));
        Assert.Contains(mine, l => l.Contains("setup: WrongCode"));
        Assert.DoesNotContain(lines, l => l.Contains("ghost@") || l.Contains(user.Email) || l.Contains("Typed-") || l.Contains("BBBB"));
    }

    [Fact]
    public async Task ResetPasswordOrDeleteEndsSessionsAtOnce()
    {
        var parent = await portal.ParentAsync("session01", "kid01");
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync("/")).StatusCode);
        var admin = await portal.SignInAdminAsync();
        var user = (await portal.Users.FindByEmailAsync("session01@parents.test", default))!;

        var reset = await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{user.Id}?handler=Code", [], formPage: $"/Admin/Users/Edit/{user.Id}");
        var code = CodeOnPage(await reset.Content.ReadAsStringAsync());
        Assert.NotEmpty(code);
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/")).StatusCode); // signed out
        Assert.NotEqual(HttpStatusCode.Redirect, (await Login(portal.Browser(), "session01@parents.test", PortalFactory.ParentPassword)).StatusCode);
        var again = portal.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await Setup(again, "session01@parents.test", code, "Again-Pass-5678")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/")).StatusCode);

        var del = await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{user.Id}?handler=Delete", new() { ["confirm"] = "true" }, formPage: $"/Admin/Users/Edit/{user.Id}");
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await again.GetAsync("/")).StatusCode);
        Assert.Null(await portal.Users.FindByEmailAsync("session01@parents.test", default));
        Assert.Contains("user_deleted", await admin.GetStringAsync("/Admin/Audit"));
    }

    [Fact]
    public async Task TheOwnerIsAlwaysAnAdminAndNeverDeleted()
    {
        var owner = await portal.SignInAdminAsync();
        var me = (await portal.Users.FindByEmailAsync(PortalFactory.AdminEmail, default))!;
        Assert.True(me.IsOwner);
        Assert.Contains("Owner", await owner.GetStringAsync("/Admin/Users"));
        var page = await owner.GetStringAsync($"/Admin/Users/Edit/{me.Id}");
        Assert.DoesNotContain("Delete user", page);
        Assert.DoesNotContain("name=\"role\"", page); // always an admin
        await Assert.ThrowsAsync<UserChangeException>(() =>
            portal.Users.UpdateAsync(me.Id, me.Email, me.DisplayName, Roles.Parent, [], me.Id, "test", default));

        // A second admin (12+ characters) can't delete, change or take over the owner.
        var (other, code) = await portal.Users.CreateAsync("second-admin@school.test", "Second", Roles.Admin, [], "test", default);
        var b = portal.Browser();
        // The admin rule shows only with the right code, so the page doesn't say which emails are admins.
        Assert.Contains("don't match", Decode(await (await Setup(b, other.Email, "AAAA-AAAA", "Short-Pass!")).Content.ReadAsStringAsync()));
        Assert.Contains("School IT accounts", Decode(await (await Setup(b, other.Email, code, "Short-Pass!")).Content.ReadAsStringAsync()));
        Assert.Equal("/TwoFactor", (await Setup(b, other.Email, code, "A-Long-Admin-Password")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.SetUpTwoFactorAsync(b, other.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Users")).StatusCode);
        var asOther = await b.GetStringAsync($"/Admin/Users/Edit/{me.Id}");
        Assert.Contains("<fieldset disabled", asOther);
        Assert.DoesNotContain("Reset password", asOther);
        var del = await Assert.ThrowsAsync<UserChangeException>(() => portal.Users.DeleteAsync(me.Id, other.Id, "test", default));
        Assert.Contains("can't be deleted", del.Message);
        await Assert.ThrowsAsync<UserChangeException>(() => portal.Users.NewSetupCodeAsync(me.Id, other.Id, "test", default));
        await Assert.ThrowsAsync<UserChangeException>(() =>
            portal.Users.UpdateAsync(me.Id, "hijack@school.test", me.DisplayName, Roles.Admin, [], other.Id, "test", default));

        // Nobody deletes themselves; the owner can remove the other admin.
        var self = await PortalFactory.PostFormAsync(owner, $"/Admin/Users/Edit/{me.Id}?handler=Delete", new() { ["confirm"] = "true" }, formPage: $"/Admin/Users/Edit/{other.Id}");
        Assert.NotEqual(HttpStatusCode.Redirect, self.StatusCode);
        await portal.Users.DeleteAsync(other.Id, me.Id, "test", default);
        Assert.NotNull(await portal.Users.FindByEmailAsync(PortalFactory.AdminEmail, default));
    }

    [Fact]
    public async Task ServerResetGivesTheOwnerANewSetupCode()
    {
        var reset = await portal.Users.ResetOwnerAsync(default);
        Assert.Equal(PortalFactory.AdminEmail, reset!.Value.Email);
        Assert.NotEqual(HttpStatusCode.Redirect, (await Login(portal.Browser(), PortalFactory.AdminEmail, PortalFactory.AdminPassword)).StatusCode);
        var b = portal.Browser();
        // The reset clears two-step sign-in too (the way back from a lost phone): set up again before anything else.
        Assert.Equal("/TwoFactor", (await Setup(b, PortalFactory.AdminEmail, reset.Value.Code, PortalFactory.AdminPassword)).Headers.Location?.OriginalString);
        Assert.Equal("/TwoFactor", (await b.GetAsync("/Admin/Users")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await portal.SetUpTwoFactorAsync(b, PortalFactory.AdminEmail)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/Admin/Users")).StatusCode);
    }

    [Fact]
    public async Task AnOldWeakPasswordMustBeChangedBeforeAnythingElse()
    {
        // A parent whose 4-digit PIN predates the current rules.
        var (user, code) = await portal.Users.CreateAsync("legacy@parents.test", "Legacy", Roles.Parent, ["kid01"], "test", default);
        await portal.Users.CompleteSetupAsync(user.Email, code, "1234", default, checkRules: false); // as before the rules

        var b = portal.Browser();
        var signIn = await Login(b, user.Email, "1234");
        Assert.Equal("/Account", signIn.Headers.Location?.OriginalString);
        Assert.Equal("/Account", (await b.GetAsync("/")).Headers.Location?.OriginalString);
        Assert.Equal("/Account", (await b.GetAsync("/Device/" + Guid.NewGuid())).Headers.Location?.OriginalString);
        Assert.Contains("choose a new password", Decode(await b.GetStringAsync("/Account")));

        var weak = await PortalFactory.PostFormAsync(b, "/Account", new() { ["current"] = "1234", ["password"] = "12345678", ["confirm"] = "12345678" });
        Assert.Contains("needs an uppercase letter", Decode(await weak.Content.ReadAsStringAsync()));
        var ok = await PortalFactory.PostFormAsync(b, "/Account", new() { ["current"] = "1234", ["password"] = "Legacy-Pass-1", ["confirm"] = "Legacy-Pass-1" });
        Assert.Equal("/", ok.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/")).StatusCode); // free to use the portal now
        Assert.NotEqual(HttpStatusCode.Redirect, (await Login(portal.Browser(), user.Email, "1234")).StatusCode);
        Assert.Equal("/", (await Login(portal.Browser(), user.Email, "Legacy-Pass-1")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ParentsCantManageUsers()
    {
        var parent = await portal.ParentAsync("nosy01", "kid01");
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/Admin/Users")).StatusCode); // to /Denied
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/Admin/Users/Import")).StatusCode);
    }

    [Fact]
    public async Task ChangeOwnPassword()
    {
        var parent = await portal.ParentAsync("change01", "kid01");
        var bad = await PortalFactory.PostFormAsync(parent, "/Account", new() { ["current"] = "Wrong-Pass-0", ["password"] = "Changed-Pass-8", ["confirm"] = "Changed-Pass-8" });
        Assert.Contains("current password isn", Decode(await bad.Content.ReadAsStringAsync()));
        var elsewhere = await portal.SignInAsync("change01@parents.test", PortalFactory.ParentPassword); // say, a stolen session
        var ok = await PortalFactory.PostFormAsync(parent, "/Account", new() { ["current"] = PortalFactory.ParentPassword, ["password"] = "Changed-Pass-8", ["confirm"] = "Changed-Pass-8" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync("/")).StatusCode); // this session carries on
        Assert.StartsWith("http://localhost/Login", (await elsewhere.GetAsync("/")).Headers.Location?.ToString()); // the other one is signed out
        Assert.Equal(HttpStatusCode.Redirect, (await Login(portal.Browser(), "change01@parents.test", "Changed-Pass-8")).StatusCode);
    }

    [Fact]
    public async Task GuessingTheCurrentPasswordLocksTheAccount()
    {
        var parent = await portal.ParentAsync("guess01", "kid01");
        string? last = null;
        for (int i = 0; i < UserStore.MaxFailedAttempts + 1; i++)
            last = Decode(await (await PortalFactory.PostFormAsync(parent, "/Account",
                new() { ["current"] = $"Wrong-Pass-{i}", ["password"] = "Changed-Pass-8", ["confirm"] = "Changed-Pass-8" })).Content.ReadAsStringAsync());
        Assert.Contains("Too many attempts", last);
        var right = await PortalFactory.PostFormAsync(parent, "/Account",
            new() { ["current"] = PortalFactory.ParentPassword, ["password"] = "Changed-Pass-8", ["confirm"] = "Changed-Pass-8" });
        Assert.Contains("Too many attempts", Decode(await right.Content.ReadAsStringAsync())); // not while locked
        Assert.Equal(HttpStatusCode.OK, (await Login(portal.Browser(), "guess01@parents.test", "Changed-Pass-8")).StatusCode); // unchanged
    }

    [Fact]
    public async Task SetupCodesExpire()
    {
        var (user, code) = await portal.Users.CreateAsync("late@parents.test", "Late", Roles.Parent, [], "test", default);
        portal.Clock.Now = portal.Clock.Now.Add(UserStore.SetupCodeLifetime).AddMinutes(1);
        try
        {
            var res = await Setup(portal.Browser(), user.Email, code, "Late-Pass-1234");
            Assert.Contains("expired", Decode(await res.Content.ReadAsStringAsync()));
        }
        finally
        {
            portal.Clock.Now = portal.Clock.Now.Subtract(UserStore.SetupCodeLifetime);
        }
    }

    [Fact]
    public async Task CsvImportPreviewsThenCreatesAndReplaces()
    {
        await portal.ParentAsync("csvold", "old1", "old2"); // existing: students get replaced
        var admin = await portal.SignInAdminAsync();
        var csv =
            "parentName,parentMail,studentUsernames\n" +
            "New Parent,new.parent@parents.test,\"np1,np2\"\n" +
            "csvold,csvold@parents.test,\"old2,new3\"\n" +
            $"IT,{PortalFactory.AdminEmail},\"x1\"\n";

        async Task<HttpResponseMessage> Upload(string text)
        {
            var page = await admin.GetStringAsync("/Admin/Users/Import");
            var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
            var form = new MultipartFormDataContent
            {
                { new StringContent(token), "__RequestVerificationToken" },
                { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", "parents.csv" },
            };
            return await admin.PostAsync("/Admin/Users/Import", form);
        }

        var preview = Decode(await (await Upload(csv)).Content.ReadAsStringAsync());
        Assert.Contains("1 to create, 1 to update, 0 unchanged, 1 skipped", preview);
        Assert.Contains("students old1, old2 → old2, new3", preview);
        Assert.Contains("is an admin account", preview);
        Assert.Null(await portal.Users.FindByEmailAsync("new.parent@parents.test", default)); // nothing changed yet

        var csvField = Regex.Match(preview, "<textarea name=\"csv\" hidden>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value;
        var applied = Decode(await (await PortalFactory.PostFormAsync(admin, "/Admin/Users/Import?handler=Apply", new() { ["csv"] = csvField }, formPage: "/Admin/Users/Import")).Content.ReadAsStringAsync());
        Assert.Contains("1 created, 1 updated", applied);
        Assert.Equal(["new3", "old2"], (await portal.Users.FindByEmailAsync("csvold@parents.test", default))!.Students);

        // The downloadable codes work for the new parent.
        var b64 = Regex.Match(applied, "base64,([A-Za-z0-9+/=]+)").Groups[1].Value;
        var codes = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var line = codes.Split('\n')[1];
        Assert.StartsWith("\"New Parent\",new.parent@parents.test,", line);
        var code = line.Split(',')[2];
        Assert.Equal(HttpStatusCode.Redirect, (await Setup(portal.Browser(), "new.parent@parents.test", code, "Import-Pass-2468")).StatusCode);

        // A file with problems can't be imported.
        var broken = Decode(await (await Upload("parentName,parentMail,studentUsernames\nX,not-an-email,a1\n")).Content.ReadAsStringAsync());
        Assert.Contains("isn't an email address", broken);
        Assert.Contains("users_imported", await admin.GetStringAsync("/Admin/Audit"));
    }
}
