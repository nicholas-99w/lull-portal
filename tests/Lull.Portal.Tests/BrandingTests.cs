using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class BrandingTests
{
    [Theory]
    [InlineData("#1F4E79", "#1f4e79")]
    [InlineData("1f4e79", "#1f4e79")]
    [InlineData(" #06c ", "#0066cc")]
    [InlineData("#1f4e7", null)]
    [InlineData("navy", null)]
    [InlineData("#1f4e79; } body { display: none", null)]
    [InlineData("", null)]
    public void ColoursAreSixHexDigits(string typed, string? expected) => Assert.Equal(expected, Branding.NormalizeColor(typed));

    [Fact]
    public void ContrastIsWcags()
    {
        Assert.Equal(21, Branding.Contrast("#000000", "#ffffff"), 1);
        Assert.Equal(1, Branding.Contrast("#0066cc", "#0066cc"), 1);
        Assert.Equal(5.6, Branding.Contrast("#0066cc", "#ffffff"), 1);
    }

    [Fact]
    public void LullsOwnColoursPass()
    {
        Assert.Empty(new Branding
        {
            Primary = Branding.Defaults.Primary, HeaderBackground = Branding.Defaults.HeaderBackground,
            HeaderText = Branding.Defaults.HeaderText, Background = Branding.Defaults.Background, Downtime = Branding.Defaults.Downtime,
        }.Check());
        Assert.Empty(Branding.Default.Check());
        Assert.Equal("", Branding.Default.Css());
    }

    [Fact]
    public void HardToReadColoursAreRefusedWithAShadeThatWorks()
    {
        var errors = new Branding { Primary = "#f5c400" }.Check();
        var error = Assert.Single(errors);
        Assert.StartsWith("Buttons and links: #f5c400 doesn't stand out enough", error);
        var suggested = System.Text.RegularExpressions.Regex.Match(error, "Try (#[0-9a-f]{6})").Groups[1].Value;
        Assert.True(Branding.Contrast(suggested, "#ffffff") >= Branding.MinContrast);
        Assert.Empty(new Branding { Primary = suggested }.Check());
    }

    [Fact]
    public void TheMinimumContrastIsFourToOne()
    {
        Assert.Empty(new Branding { Primary = "#7c7c7c" }.Check()); // 4.2:1
        Assert.Contains("(3.9:1, needs 4:1)", Assert.Single(new Branding { Primary = "#808080" }.Check()));
    }

    [Fact]
    public void HeaderTextIsCheckedAgainstTheHeader()
    {
        // A dark header with Lull's dark text: refused, with a light text suggested.
        var error = Assert.Single(new Branding { HeaderBackground = "#1f4e79" }.Check());
        Assert.StartsWith("Header text: #1d1d1f", error);
        Assert.Empty(new Branding { HeaderBackground = "#1f4e79", HeaderText = "#ffffff" }.Check());
        Assert.Single(new Branding { Background = "#888888" }.Check());
    }

    [Fact]
    public void BadInputIsReportedNotCheckedForContrast()
    {
        var errors = new Branding { Primary = "blue", SchoolName = new string('x', Branding.MaxSchoolNameLength + 1) }.Check();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Buttons and links: enter a colour"));
    }

    [Fact]
    public void TheStylesheetSetsOnlyWhatIsBranded()
    {
        var css = new Branding { Primary = "#1f4e79", HeaderBackground = "#1f4e79", HeaderText = "#ffffff" }.Css();
        Assert.Contains(":root { --accent: #1f4e79; --accent-fill: #1f4e79; --header-bg: #1f4e79; --header-text: #ffffff; }", css);
        // Dark mode: links in a shade readable on its dark cards, buttons as they are.
        var dark = System.Text.RegularExpressions.Regex.Match(css, "--accent: (#[0-9a-f]{6}); --accent-fill: #1f4e79; } }").Groups[1].Value;
        Assert.True(Branding.Contrast(dark, Branding.Defaults.DarkCard) >= Branding.MinContrast, css);
        Assert.DoesNotContain("--rest", css);
        Assert.DoesNotContain("--bg", css);
    }

    [Fact]
    public void LinksStayReadableOnThePageWhenButtonsKeepTheColour()
    {
        // At the floor against the white text on buttons, so a little under it against a tinted page.
        const string primary = "#7a7a7a";
        var branding = new Branding { Primary = primary, Background = "#eeeeee" };
        Assert.Empty(branding.Check());
        Assert.True(Branding.Contrast(primary, "#eeeeee") < Branding.MinContrast);

        var link = System.Text.RegularExpressions.Regex.Match(branding.Css(), ":root \\{ --accent: (#[0-9a-f]{6});").Groups[1].Value;
        Assert.True(Branding.Contrast(link, "#eeeeee") >= Branding.MinContrast, link);
        Assert.Contains($"--accent-fill: {primary};", branding.Css());
    }

    [Fact]
    public void AlwaysLightDropsTheDarkShades()
    {
        Assert.Contains("prefers-color-scheme: dark", new Branding { Primary = "#1f4e79" }.Css());
        Assert.DoesNotContain("prefers-color-scheme: dark", new Branding { Primary = "#1f4e79", AlwaysLight = true }.Css());
    }

    [Fact]
    public void LogosAreRecognisedByTheirBytes()
    {
        Assert.Equal("image/png", Logo.TypeOf([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/jpeg", Logo.TypeOf([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/webp", Logo.TypeOf("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(Logo.TypeOf("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8));
        Assert.Null(Logo.TypeOf([]));
    }
}
