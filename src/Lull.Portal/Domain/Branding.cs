using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lull.Portal.Domain;

/// <summary>
/// IT's branding for the portal: the school's name, colours, logo and a few
/// words for parents. Every part is optional; unset, the portal looks as Lull does.
/// Colours are "#rrggbb" (lower case), checked by <see cref="Check"/>.
/// </summary>
public sealed partial record Branding
{
    public string? SchoolName { get; init; }

    /// <summary>Buttons and links.</summary>
    public string? Primary { get; init; }
    public string? HeaderBackground { get; init; }
    public string? HeaderText { get; init; }
    /// <summary>Behind the cards, in light mode (dark mode keeps its own).</summary>
    public string? Background { get; init; }
    /// <summary>Downtime and lock-downs: status dots, pills and the advanced schedule's hours.</summary>
    public string? Downtime { get; init; }

    /// <summary>With a logo, show just that: for a logo with the school's name in it. Screen readers and the browser tab still get the name.</summary>
    public bool LogoOnly { get; init; }
    /// <summary>Light for everyone, rather than following each person's device.</summary>
    public bool AlwaysLight { get; init; }

    /// <summary>Shown on the sign-in page, under its heading.</summary>
    public string? LoginMessage { get; init; }
    /// <summary>Who parents contact when they're stuck ("ithelp@example.edu.au or 02 5550 1234").</summary>
    public string? SupportContact { get; init; }

    public static Branding Default { get; } = new();

    public const int MaxSchoolNameLength = 60;
    public const int MaxLoginMessageLength = 300;
    public const int MaxSupportContactLength = 150;
    public const int MaxLogoBytes = 512 * 1024;

    /// <summary>Lull's own colours, which a colour left unset keeps (site.css, light mode).</summary>
    public static class Defaults
    {
        public const string Primary = "#0066cc";
        public const string HeaderBackground = "#ffffff";
        public const string HeaderText = "#1d1d1f";
        public const string Background = "#f5f5f7";
        public const string Downtime = "#4b4fa6";
        /// <summary>Not brandable, but checked against: white cards and buttons' text, grey hints, dark mode's cards.</summary>
        public const string Card = "#ffffff";
        public const string Muted = "#6e6e73";
        public const string DarkCard = "#1c1c1e";
    }

    /// <summary>The school's chosen minimum. Deliberately a little under WCAG AA for normal text (4.5:1).</summary>
    public const double MinContrast = 4;

    /// <summary>"#0066CC", "0066cc" or "#06c" as "#0066cc"; null if it isn't a colour.</summary>
    public static string? NormalizeColor(string? value)
    {
        var v = value?.Trim().TrimStart('#').ToLowerInvariant() ?? "";
        if (v.Length == 3) v = string.Concat(v.Select(c => $"{c}{c}"));
        return HexColor().IsMatch(v) ? "#" + v : null;
    }

    [GeneratedRegex("^[0-9a-f]{6}$")]
    private static partial Regex HexColor();

    /// <summary>WCAG contrast ratio of two "#rrggbb" colours, 1 to 21.</summary>
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    /// <summary><paramref name="hex"/> moved <paramref name="amount"/> (0 to 1) of the way to <paramref name="toward"/>.</summary>
    static string Mix(string hex, string toward, double amount)
    {
        var sb = new StringBuilder("#");
        for (int i = 0; i < 3; i++)
        {
            int from = int.Parse(hex.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber);
            int to = int.Parse(toward.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber);
            sb.Append(((int)Math.Round(from + (to - from) * amount)).ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The nearest shade of <paramref name="hex"/>, toward black or white, with at least
    /// <see cref="MinContrast"/> against <paramref name="background"/>: <paramref name="hex"/> itself if it has it.
    /// </summary>
    public static string Readable(string hex, string background, string toward)
    {
        for (int step = 0; step <= 20; step++)
        {
            var shade = Mix(hex, toward, step / 20.0);
            if (Contrast(shade, background) >= MinContrast) return shade;
        }
        return toward;
    }

    /// <summary>What's wrong with this branding, as messages for IT; empty if it's fine.</summary>
    public IReadOnlyList<string> Check()
    {
        var errors = new List<string>();
        if (SchoolName?.Length > MaxSchoolNameLength) errors.Add($"The school's name can be at most {MaxSchoolNameLength} characters.");
        if (LoginMessage?.Length > MaxLoginMessageLength) errors.Add($"The sign-in message can be at most {MaxLoginMessageLength} characters.");
        if (SupportContact?.Length > MaxSupportContactLength) errors.Add($"The support contact can be at most {MaxSupportContactLength} characters.");
        foreach (var (name, value) in new[]
        {
            ("Buttons and links", Primary), ("Header background", HeaderBackground), ("Header text", HeaderText),
            ("Page background", Background), ("Downtime", Downtime),
        })
        {
            if (value is not null && NormalizeColor(value) != value) errors.Add($"{name}: enter a colour as # and six hex digits, such as #1f4e79.");
        }
        if (errors.Count > 0) return errors;

        void Needs(string what, string color, string against, string toward, string why)
        {
            if (Contrast(color, against) >= MinContrast) return;
            errors.Add($"{what} {color} doesn't stand out enough from {why} to read easily "
                + $"({Contrast(color, against):0.0}:1, needs {MinContrast}:1). Try {Readable(color, against, toward)}.");
        }
        if (Primary is { } p) Needs("Buttons and links:", p, Defaults.Card, "#000000", "the white text on buttons");
        if (Downtime is { } d) Needs("Downtime:", d, Defaults.Card, "#000000", "the white cards it's shown on");
        if (HeaderBackground is not null || HeaderText is not null)
        {
            var bg = HeaderBackground ?? Defaults.HeaderBackground;
            var text = HeaderText ?? Defaults.HeaderText;
            var toward = Contrast(bg, "#000000") > Contrast(bg, "#ffffff") ? "#000000" : "#ffffff";
            Needs("Header text:", text, bg, toward, "the header background");
        }
        if (Background is { } b && Contrast(Defaults.Muted, b) < MinContrast)
            errors.Add($"Page background: {b} is too dark for the grey hints on it to read easily "
                + $"({Contrast(Defaults.Muted, b):0.0}:1, needs {MinContrast}:1). Try {LightEnough(b)}.");
        return errors;
    }

    /// <summary>The nearest tint of <paramref name="hex"/> that grey hints are readable on.</summary>
    static string LightEnough(string hex)
    {
        for (int step = 0; step <= 20; step++)
        {
            var tint = Mix(hex, "#ffffff", step / 20.0);
            if (Contrast(Defaults.Muted, tint) >= MinContrast) return tint;
        }
        return "#ffffff";
    }

    public bool HasColors => Primary is not null || HeaderBackground is not null || HeaderText is not null
        || Background is not null || Downtime is not null;

    /// <summary>
    /// The stylesheet that applies these colours over site.css (served as /brand/style.css: the
    /// Content-Security-Policy allows no inline styles). Dark mode gets lighter shades of the
    /// link and downtime colours, readable on its dark cards; the header keeps its colours in both.
    /// Only checked colours go in, so nothing here can break out of a declaration.
    /// </summary>
    public string Css()
    {
        if (!HasColors) return "";
        var light = new StringBuilder();
        var dark = new StringBuilder();
        if (Primary is { } p)
        {
            // Links also sit on the page itself: a darker shade if the page's background needs one. Buttons keep the colour.
            light.Append($"--accent: {Readable(p, Background ?? Defaults.Background, "#000000")}; --accent-fill: {p}; ");
            dark.Append($"--accent: {Readable(p, Defaults.DarkCard, "#ffffff")}; --accent-fill: {p}; ");
        }
        if (Downtime is { } d)
        {
            light.Append($"--rest: {d}; ");
            dark.Append($"--rest: {Readable(d, Defaults.DarkCard, "#ffffff")}; ");
        }
        if (Background is { } b)
        {
            light.Append($"--bg: {b}; ");
            dark.Append("--bg: #000; ");
        }
        // The header keeps its colours in dark mode too: they're the school's, not a theme's.
        if (HeaderBackground is not null || HeaderText is not null)
            light.Append($"--header-bg: {HeaderBackground ?? Defaults.HeaderBackground}; --header-text: {HeaderText ?? Defaults.HeaderText}; ");
        var css = new StringBuilder($":root {{ {light}}}\n");
        if (dark.Length > 0 && !AlwaysLight) css.Append($"@media (prefers-color-scheme: dark) {{ :root {{ {dark}}} }}\n");
        return css.ToString();
    }
}
