using System.Text;
using Lull.Portal.Auth;

namespace Lull.Portal.Tests;

public class TotpTests
{
    /// <summary>RFC 6238's SHA-1 test key.</summary>
    static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>RFC 6238 appendix B (SHA-1), as 6 digits: the last six of its 8-digit codes.</summary>
    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void CodesMatchTheRfcTestVectors(long unixSeconds, string code) =>
        Assert.Equal(code, Totp.Code(Rfc, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));

    [Fact]
    public void ACodeFromTheStepEitherSideIsAcceptedButNoFurther()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        foreach (var offset in new[] { -1, 0, 1 })
            Assert.Equal(step + offset, Totp.Match(Rfc, Totp.Code(Rfc, step + offset), now));
        Assert.Null(Totp.Match(Rfc, Totp.Code(Rfc, step + 2), now));
        Assert.Null(Totp.Match(Rfc, Totp.Code(Rfc, step - 2), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void MalformedCodesNeverMatch(string? code) =>
        Assert.Null(Totp.Match(Rfc, code, DateTimeOffset.FromUnixTimeSeconds(59)));

    [Fact]
    public void SpacesInACodeAreIgnored() =>
        Assert.NotNull(Totp.Match(Rfc, "287 082", DateTimeOffset.FromUnixTimeSeconds(59)));

    /// <summary>RFC 4648's base32 test vectors, without padding.</summary>
    [Theory]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void KeysAreShownInBase32(string data, string base32) =>
        Assert.Equal(base32, Totp.Base32(Encoding.ASCII.GetBytes(data)));

    [Fact]
    public void TheLinkNamesTheAccountAndCarriesTheKey()
    {
        var uri = Totp.Uri("Lull (portal.school.test)", "it@school.test", Encoding.ASCII.GetBytes("foobar"));
        Assert.Equal("otpauth://totp/Lull%20%28portal.school.test%29:it%40school.test?secret=MZXW6YTBOI&issuer=Lull%20%28portal.school.test%29&digits=6&period=30", uri);
    }

    [Fact]
    public void NewKeysAre160BitsAndDiffer()
    {
        var a = Totp.NewSecret();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, Totp.NewSecret());
    }
}
