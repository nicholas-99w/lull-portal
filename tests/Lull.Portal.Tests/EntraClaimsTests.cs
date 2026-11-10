using System.Security.Claims;
using Lull.Portal.Auth;

namespace Lull.Portal.Tests;

public class EntraClaimsTests
{
    static readonly Guid Oid = Guid.Parse("0a1b2c3d-0000-0000-0000-000000000001");

    static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    static (EntraIdentity? Who, string? Refused, IReadOnlyList<string> Invalid) Read(EntraOptions? o = null, params (string, string)[] claims) =>
        EntraSignIn.Read(Token([("oid", Oid.ToString()), .. claims]), o ?? new EntraOptions());

    [Fact]
    public void TheAdminRoleWinsOverParent()
    {
        var (who, _, _) = Read(null, ("roles", "Parent"), ("roles", "Admin"), ("email", "it@school.test"));
        Assert.Equal(Roles.Admin, who!.Role);
        Assert.Equal(Roles.Parent, Read(null, ("roles", "Parent"), ("email", "p@school.test")).Who!.Role);
    }

    [Fact]
    public void NoKnownRoleIsRefusedUnlessEveryoneIsAParent()
    {
        Assert.Equal(EntraSignIn.Refused.NoRole, Read(null, ("roles", "Teacher")).Refused);
        Assert.Equal(EntraSignIn.Refused.NoRole, Read(null).Refused);
        Assert.Equal(EntraSignIn.Refused.NoRole, Read(null, ("roles", "parent")).Refused); // app role values are case-sensitive
        Assert.Equal(Roles.Parent, Read(new EntraOptions { ParentRole = "" }).Who!.Role);
        Assert.Equal(Roles.Admin, Read(new EntraOptions { AdminRole = "Portal.Admin" }, ("roles", "Portal.Admin")).Who!.Role);
    }

    [Fact]
    public void StudentsComeAsOneListOrOneClaimEach()
    {
        Assert.Equal(["jsmith26", "asmith29"], Read(null, ("roles", "Parent"), ("students", "JSmith26, asmith29")).Who!.Students);
        Assert.Equal(["jsmith26", "asmith29"], Read(null, ("roles", "Parent"), ("students", "jsmith26"), ("students", "asmith29")).Who!.Students);
        Assert.Equal(["jsmith26"], Read(null, ("roles", "Parent"), ("students", "jsmith26@school.test")).Who!.Students); // a UPN
        var (who, _, invalid) = Read(new EntraOptions { StudentsClaim = "extn.kids" }, ("roles", "Parent"), ("extn.kids", "ok1;bad!"));
        Assert.Equal(["ok1"], who!.Students);
        Assert.Equal(["bad!"], invalid);
        Assert.Empty(Read(null, ("roles", "Parent")).Who!.Students);
    }

    [Fact]
    public void EmailFallsBackToTheUserPrincipalName()
    {
        Assert.Equal("a@school.test", Read(null, ("roles", "Parent"), ("email", "a@school.test"), ("preferred_username", "upn@school.test")).Who!.Email);
        Assert.Equal("upn@school.test", Read(null, ("roles", "Parent"), ("preferred_username", "upn@school.test")).Who!.Email);
    }

    [Fact]
    public void ATokenWithoutAnObjectIdFails()
    {
        var (who, refused, _) = EntraSignIn.Read(Token(("roles", "Parent")), new EntraOptions());
        Assert.Null(who);
        Assert.Equal(EntraSignIn.Refused.Failed, refused);
    }

    [Fact]
    public void EveryRefusalHasAMessageAndUnknownCodesNone()
    {
        foreach (var code in new[] { EntraSignIn.Refused.NoRole, EntraSignIn.Refused.Conflict, EntraSignIn.Refused.NoEmail, EntraSignIn.Refused.Failed })
            Assert.NotNull(EntraSignIn.Refused.Message(code));
        Assert.Null(EntraSignIn.Refused.Message("<script>"));
    }
}
