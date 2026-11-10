using System.Net;
using System.Net.Http.Json;
using Lull.Portal.Api;
using Lull.Portal.Auth;
using Lull.Portal.Data;

namespace Lull.Portal.Tests.Integration;

/// <summary>Signing in with Entra ID, alongside local accounts.</summary>
public class EntraTests(EntraPortalFactory portal) : IClassFixture<EntraPortalFactory>
{
    static (string, string)[] Token(Guid oid, string email, string? role, string? students = null, string? name = null) =>
        EntraPortalFactory.Token(oid, email, role, students, name);

    [Fact]
    public async Task TheLoginPageOffersMicrosoftAndPasswordsAndSendsYouToEntra()
    {
        var page = await portal.Browser().GetStringAsync("/Login");
        Assert.Contains("Sign in with Microsoft", page);
        Assert.Contains("class=\"ms-signin\"", page);
        Assert.Contains("name=\"password\"", page);
        // The password form is folded away under Microsoft, and open again after a wrong password.
        Assert.Contains("<details class=\"password-signin\">", page);
        var wrong = await PortalFactory.PostFormAsync(portal.Browser(), "/Login", new() { ["email"] = "nobody@parents.test", ["password"] = "wrong" });
        Assert.Contains("<details class=\"password-signin\" open=\"open\">", await wrong.Content.ReadAsStringAsync());

        var res = await portal.Browser().GetAsync("/Login?handler=Entra&returnUrl=%2FAdmin%2FUsers");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith(EntraPortalFactory.AuthorizeEndpoint, location);
        Assert.Contains($"client_id={EntraPortalFactory.ClientId}", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("code_challenge=", location); // PKCE
        Assert.Contains(Uri.EscapeDataString("http://localhost/signin-oidc"), location);
        Assert.Contains("form-action 'self' https://login.microsoftonline.com", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task TheLoginPageSendsYouHomeWhenYouAreAlreadySignedIn()
    {
        var parent = await portal.EntraBrowserAsync(Token(Guid.NewGuid(), "eparent9@school.test", "Parent", "ekid90"));
        var res = await parent.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);

        var admin = await portal.SignInAdminAsync();
        Assert.Equal("/", (await admin.GetAsync("/Login?local=true")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AParentSignsInForTheFirstTimeAndSeesTheirChildsMac()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02ENTRA0001");
        await PortalFactory.CheckInAsync(mac, "ekid01", null);

        var oid = Guid.NewGuid();
        // The claim as an extension attribute holds it; a bad username is left out.
        var parent = await portal.EntraBrowserAsync(Token(oid, "EParent1@School.test", "Parent", "ekid01, EKid02;bad!name", "E Parent"));
        Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync($"/Device/{id}")).StatusCode);
        Assert.Contains("ekid01", await parent.GetStringAsync("/"));

        var user = (await portal.Users.FindByEmailAsync("eparent1@school.test", default))!;
        Assert.Equal(oid, user.EntraObjectId);
        Assert.Equal(("E Parent", Roles.Parent, false), (user.DisplayName, user.Role, user.HasPassword));
        Assert.Equal(["ekid01", "ekid02"], user.Students);
        var admin = await portal.SignInAdminAsync();
        var audit = await admin.GetStringAsync("/Admin/Audit?q=eparent1");
        Assert.Contains("user_created", audit);
        Assert.Contains("entra", audit);
    }

    [Fact]
    public async Task EachSignInBringsTheAccountUpToDate()
    {
        var oid = Guid.NewGuid();
        var first = await portal.EntraBrowserAsync(Token(oid, "eparent2@school.test", "Parent", "ekid10"));
        var id = (await portal.Users.FindByEmailAsync("eparent2@school.test", default))!.Id;

        // New students and name: the same account, and the first session carries on.
        await portal.EntraBrowserAsync(Token(oid, "eparent2@school.test", "Parent", "ekid11,ekid12", "Renamed"));
        var user = (await portal.Users.GetAsync(id, default))!;
        Assert.Equal(("Renamed", 2), (user.DisplayName, user.Students.Count));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/")).StatusCode);

        // A new email (e.g. a changed name in Entra) and the admin role: other sessions end.
        var asAdmin = await portal.EntraBrowserAsync(Token(oid, "eparent2.new@school.test", "Admin", "ekid11"));
        user = (await portal.Users.GetAsync(id, default))!;
        Assert.Equal(("eparent2.new@school.test", Roles.Admin), (user.Email, user.Role));
        Assert.Empty(user.Students); // admins have none
        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/")).StatusCode); // to /Login
        Assert.Equal(HttpStatusCode.OK, (await asAdmin.GetAsync("/Admin/Devices")).StatusCode);
    }

    [Fact]
    public async Task SomeoneWithoutAPortalRoleIsRefused()
    {
        var browser = portal.Browser();
        var res = await EntraPortalFactory.EntraSignInAsync(browser, Token(Guid.NewGuid(), "norole@school.test", role: null));
        Assert.Equal("/Login?entra=no-role", res.Headers.Location?.OriginalString);
        Assert.Null(await portal.Users.FindByEmailAsync("norole@school.test", default));
        Assert.Contains("isn't set up to use the portal", WebUtility.HtmlDecode(await browser.GetStringAsync("/Login?entra=no-role")));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);

        var other = await EntraPortalFactory.EntraSignInAsync(portal.Browser(), Token(Guid.NewGuid(), "otherrole@school.test", "Teacher"));
        Assert.Equal("/Login?entra=no-role", other.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ATokenWithoutAnEmailIsRefused()
    {
        var res = await EntraPortalFactory.EntraSignInAsync(portal.Browser(), ("oid", Guid.NewGuid().ToString()), ("roles", "Parent"));
        Assert.Equal("/Login?entra=no-email", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ALocalAccountWithTheSameEmailIsNotTakenOverUnlessLinkingIsOn()
    {
        await portal.ParentAsync("elocal1", "ekid20"); // elocal1@parents.test, with a password
        var oid = Guid.NewGuid();
        var refused = await EntraPortalFactory.EntraSignInAsync(portal.Browser(), Token(oid, "elocal1@parents.test", "Parent", "ekid21"));
        Assert.Equal("/Login?entra=conflict", refused.Headers.Location?.OriginalString);
        var local = (await portal.Users.FindByEmailAsync("elocal1@parents.test", default))!;
        Assert.False(local.IsEntra);
        await portal.SignInAsync("elocal1@parents.test", PortalFactory.ParentPassword); // still works

        var passwordSession = await portal.SignInAsync("elocal1@parents.test", PortalFactory.ParentPassword);
        portal.Entra.LinkLocalAccountsByEmail = true;
        try
        {
            await portal.EntraBrowserAsync(Token(oid, "elocal1@parents.test", "Parent", "ekid21"));
        }
        finally
        {
            portal.Entra.LinkLocalAccountsByEmail = false;
        }
        var linked = (await portal.Users.GetAsync(local.Id, default))!;
        Assert.Equal((oid, false), (linked.EntraObjectId, linked.HasPassword)); // the same account, now Entra's
        Assert.Equal(["ekid21"], linked.Students);
        Assert.Equal(HttpStatusCode.Redirect, (await passwordSession.GetAsync("/")).StatusCode); // its password sessions ended
        var password = await PortalFactory.PostFormAsync(portal.Browser(), "/Login",
            new() { ["email"] = "elocal1@parents.test", ["password"] = PortalFactory.ParentPassword });
        Assert.Equal(HttpStatusCode.OK, password.StatusCode); // the form again: the password no longer works
        Assert.Contains("user_linked_to_entra", await (await portal.SignInAdminAsync()).GetStringAsync("/Admin/Audit?q=elocal1"));
    }

    [Fact]
    public async Task TheOwnersAccountIsNeverTakenOver()
    {
        portal.Entra.LinkLocalAccountsByEmail = true;
        try
        {
            var res = await EntraPortalFactory.EntraSignInAsync(portal.Browser(), Token(Guid.NewGuid(), PortalFactory.AdminEmail, "Admin"));
            Assert.Equal("/Login?entra=conflict", res.Headers.Location?.OriginalString);
        }
        finally
        {
            portal.Entra.LinkLocalAccountsByEmail = false;
        }
        var owner = (await portal.Users.FindByEmailAsync(PortalFactory.AdminEmail, default))!;
        Assert.False(owner.IsEntra);
        await portal.SignInAdminAsync();
    }

    [Fact]
    public async Task AReusedEmailDoesntGetSomeoneElsesAccount()
    {
        await portal.EntraBrowserAsync(Token(Guid.NewGuid(), "eparent3@school.test", "Parent", "ekid30"));
        // A different person in Entra (new object ID) given the same address.
        var res = await EntraPortalFactory.EntraSignInAsync(portal.Browser(), Token(Guid.NewGuid(), "eparent3@school.test", "Parent", "ekid31"));
        Assert.Equal("/Login?entra=conflict", res.Headers.Location?.OriginalString);
        Assert.Equal(["ekid30"], (await portal.Users.FindByEmailAsync("eparent3@school.test", default))!.Students);
    }

    [Fact]
    public async Task ITCanViewAndDeleteAnEntraAccountButNotEditIt()
    {
        var oid = Guid.NewGuid();
        var parent = await portal.EntraBrowserAsync(Token(oid, "eparent4@school.test", "Parent", "ekid40"));
        var id = (await portal.Users.FindByEmailAsync("eparent4@school.test", default))!.Id;
        var admin = await portal.SignInAdminAsync();

        Assert.Contains("Entra ID", await admin.GetStringAsync("/Admin/Users?q=eparent4"));
        var page = await admin.GetStringAsync($"/Admin/Users/Edit/{id}");
        Assert.Contains("change them in Entra", page);
        Assert.DoesNotContain("New setup code", page);

        var edit = await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{id}",
            new() { ["name"] = "X", ["email"] = "eparent4@school.test", ["role"] = "Parent", ["students"] = "someoneelse" });
        Assert.Contains("signs in with Entra ID", await edit.Content.ReadAsStringAsync());
        var code = await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{id}?handler=Code", [], formPage: $"/Admin/Users/Edit/{id}");
        Assert.Contains("signs in with Entra ID", await code.Content.ReadAsStringAsync());
        Assert.Equal(["ekid40"], (await portal.Users.GetAsync(id, default))!.Students);

        // An import naming them changes nothing.
        var csv = "parentName,parentMail,studentUsernames\nX,eparent4@school.test,\"someoneelse\"\n";
        await PortalFactory.PostFormAsync(admin, "/Admin/Users/Import?handler=Apply", new() { ["csv"] = csv }, formPage: "/Admin/Users/Import");
        Assert.Equal(["ekid40"], (await portal.Users.GetAsync(id, default))!.Students);

        var delete = await PortalFactory.PostFormAsync(admin, $"/Admin/Users/Edit/{id}?handler=Delete", new() { ["confirm"] = "true" },
            formPage: $"/Admin/Users/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/")).StatusCode); // signed out at once
        // Still assigned in Entra: the next sign-in makes a new account.
        await portal.EntraBrowserAsync(Token(oid, "eparent4@school.test", "Parent", "ekid40"));
        Assert.NotEqual(id, (await portal.Users.FindByEmailAsync("eparent4@school.test", default))!.Id);
    }

    [Fact]
    public async Task AnEntraAccountHasNoPasswordAndSignsOutOfMicrosoftToo()
    {
        var browser = await portal.EntraBrowserAsync(Token(Guid.NewGuid(), "eparent5@school.test", "Parent", "ekid50"));
        var account = await browser.GetStringAsync("/Account");
        Assert.Contains("no portal password to change", account);
        Assert.DoesNotContain("name=\"current\"", account);
        var change = await PortalFactory.PostFormAsync(browser, "/Account",
            new() { ["current"] = "x", ["password"] = "New-Password-1", ["confirm"] = "New-Password-1" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        var signOut = await PortalFactory.PostFormAsync(browser, "/Logout", [], formPage: "/Account");
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.StartsWith(EntraPortalFactory.EndSessionEndpoint, signOut.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);

        // A local account signs out of the portal only.
        var local = await portal.SignInAdminAsync();
        var localOut = await PortalFactory.PostFormAsync(local, "/Logout", [], formPage: "/Account");
        Assert.Equal("/Login", localOut.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LocalAccountsKeepWorkingAlongsideEntra()
    {
        var parent = await portal.ParentAsync("elocal2", "ekid60");
        Assert.Contains("ekid60", await parent.GetStringAsync("/"));
        var admin = await portal.SignInAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/Users/Import")).StatusCode);
        Assert.Contains("New user", await admin.GetStringAsync("/Admin/Users"));
    }
}

/// <summary>Entra only: passwords are for the owner, the emergency admin.</summary>
public class EntraOnlyTests(EntraOnlyPortalFactory portal) : IClassFixture<EntraOnlyPortalFactory>
{
    [Fact]
    public async Task OnlyTheOwnerCanUseAPassword()
    {
        var page = await portal.Browser().GetStringAsync("/Login");
        Assert.Contains("Sign in with Microsoft", page);
        Assert.DoesNotContain("name=\"password\"", page);
        Assert.Contains("/Login?local=true", page);
        Assert.Contains("name=\"password\"", await portal.Browser().GetStringAsync("/Login?local=true"));

        // A local parent made before local sign-in was turned off can't sign in, even with the right password.
        var (_, code) = await portal.Users.CreateAsync("old@parents.test", "Old", Roles.Parent, ["okid1"], "test", default);
        var setup = await PortalFactory.PostFormAsync(portal.Browser(), "/Setup",
            new() { ["email"] = "old@parents.test", ["code"] = code, ["password"] = PortalFactory.ParentPassword, ["confirm"] = PortalFactory.ParentPassword });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode); // refused: the form again
        Assert.False((await portal.Users.FindByEmailAsync("old@parents.test", default))!.HasPassword);

        // Nor one that set its password while local sign-in was on.
        var (_, code2) = await portal.Users.CreateAsync("older@parents.test", "Older", Roles.Parent, ["okid1"], "test", default);
        Assert.Equal(SignInOutcome.Ok, (await portal.Users.CompleteSetupAsync("older@parents.test", code2, PortalFactory.ParentPassword, default)).Outcome);
        var signIn = await PortalFactory.PostFormAsync(portal.Browser(), "/Login",
            new() { ["email"] = "older@parents.test", ["password"] = PortalFactory.ParentPassword }, formPage: "/Login?local=true");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode); // the form again

        await portal.SignInAdminAsync(); // the owner can
    }

    [Fact]
    public async Task NoNewLocalAccounts()
    {
        var admin = await portal.SignInAdminAsync();
        var users = await admin.GetStringAsync("/Admin/Users");
        Assert.DoesNotContain("Import CSV", users);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/Admin/Users/Import")).StatusCode);
        Assert.Contains("Local accounts are turned off", await admin.GetStringAsync("/Admin/Users/Edit"));
        var create = await PortalFactory.PostFormAsync(admin, "/Admin/Users/Edit",
            new() { ["name"] = "New", ["email"] = "new@parents.test", ["role"] = "Parent", ["students"] = "" });
        Assert.Contains("Local accounts are turned off", await create.Content.ReadAsStringAsync());
        Assert.Null(await portal.Users.FindByEmailAsync("new@parents.test", default));

        // Entra sign-in works as usual.
        await portal.EntraBrowserAsync(EntraPortalFactory.Token(Guid.NewGuid(), "eonly@school.test", "Parent", "okid2"));
    }
}
