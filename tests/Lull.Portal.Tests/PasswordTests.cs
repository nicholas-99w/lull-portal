using Lull.Portal.Auth;

namespace Lull.Portal.Tests;

public class PasswordTests
{
    [Fact]
    public void HashesVerifyAndDiffer()
    {
        var h = new PasswordHasher(1000);
        var a = h.Hash("1234");
        Assert.StartsWith("pbkdf2-sha256$1000$", a);
        Assert.NotEqual(a, h.Hash("1234")); // salted
        Assert.True(PasswordHasher.Verify("1234", a));
        Assert.False(PasswordHasher.Verify("1235", a));
        Assert.False(PasswordHasher.Verify("1234", null));
        Assert.False(PasswordHasher.Verify("1234", "pbkdf2-sha256$x$y$z"));
        Assert.False(PasswordHasher.Verify("1234", "plain-1234"));
    }

    [Theory]
    [InlineData("Parent-Pass-1", null)]
    [InlineData("Ab cdefg", null)] // a space counts as special
    [InlineData("Párénté!", null)] // non-ASCII letters count
    [InlineData("1234", "at least 8 characters, an uppercase letter, a lowercase letter and a special character")]
    [InlineData("Ab!", "at least 8 characters")]
    [InlineData("abcdefg!", "an uppercase letter")]
    [InlineData("ABCDEFG!", "a lowercase letter")]
    [InlineData("Abcdefgh", "a special character")]
    public void ParentPasswordRules(string password, string? missing)
    {
        var problem = PasswordRules.Problem(Roles.Parent, password);
        if (missing is null) Assert.Null(problem);
        else Assert.Equal($"The password needs {missing}.".Replace("a special character.", "a special character (such as ! ? # or a space)."), problem);
    }

    [Fact]
    public void AdminsNeedTwelve()
    {
        Assert.Contains("at least 12 characters", PasswordRules.Problem(Roles.Admin, "Short-Pass!"));
        Assert.Null(PasswordRules.Problem(Roles.Admin, "Long-Admin-Pass"));
        Assert.Contains("at most 128", PasswordRules.Problem(Roles.Parent, "Aa!" + new string('x', 126)));
    }

    [Fact]
    public void SetupCodesAreForgivingAndOneWay()
    {
        var code = SetupCodes.New();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.NotEqual(code, SetupCodes.New());
        var stored = SetupCodes.Hash(code);
        Assert.True(SetupCodes.Matches(code.ToLowerInvariant().Replace("-", " "), stored));
        Assert.False(SetupCodes.Matches("AAAA-AAAA", stored));
        Assert.False(SetupCodes.Matches(code, null));
    }

    [Fact]
    public void DeviceTokensAndEnrolmentSecret()
    {
        Assert.NotEqual(DeviceTokens.New(), DeviceTokens.New());
        Assert.Equal("abc", DeviceTokens.FromHeader("Bearer abc"));
        Assert.Null(DeviceTokens.FromHeader("Basic abc"));
        Assert.False(DeviceTokens.SecretMatches("", ""));
        Assert.True(DeviceTokens.SecretMatches("a-long-enrolment-secret", "a-long-enrolment-secret"));
    }
}
