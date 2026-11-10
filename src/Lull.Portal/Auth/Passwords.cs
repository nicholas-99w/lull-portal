using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lull.Portal.Auth;

public static class Roles
{
    public const string Parent = "Parent";
    public const string Admin = "Admin";

    public static bool IsValid(string? role) => role is Parent or Admin;
}

/// <summary>
/// Password rules (owner decision, 2026-10-18): at least 8 characters
/// (admins 12, since they control every Mac and user), with an uppercase
/// letter, a lowercase letter and a special character (anything that isn't
/// a letter or digit, e.g. ! ? # or a space). Checked whenever a password is
/// set; an older password that doesn't meet them must be changed at the next
/// sign-in (<see cref="Lull.Portal.Auth.AccountClaims.MustChangePassword"/>).
/// </summary>
public static class PasswordRules
{
    public const int ParentMinimum = 8;
    public const int AdminMinimum = 12;
    public const int Maximum = 128;

    public static int MinimumFor(string role) => role == Roles.Admin ? AdminMinimum : ParentMinimum;

    /// <summary>"At least 8 characters, with an uppercase letter, a lowercase letter and a special character."</summary>
    public static string Describe(string role) =>
        $"At least {MinimumFor(role)} characters, with an uppercase letter, a lowercase letter and a special character (such as ! ? # or a space).";

    /// <summary>What's missing, as one sentence, or null if the password is acceptable.</summary>
    public static string? Problem(string role, string? password)
    {
        password ??= "";
        if (password.Length > Maximum) return $"Use at most {Maximum} characters.";
        var missing = new List<string>();
        if (password.Length < MinimumFor(role)) missing.Add($"at least {MinimumFor(role)} characters");
        if (!password.Any(char.IsUpper)) missing.Add("an uppercase letter");
        if (!password.Any(char.IsLower)) missing.Add("a lowercase letter");
        if (password.All(char.IsLetterOrDigit)) missing.Add("a special character (such as ! ? # or a space)");
        return missing.Count == 0 ? null : $"The password needs {Join(missing)}.";
    }

    static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
}

/// <summary>
/// PBKDF2-SHA256, stored as <c>pbkdf2-sha256$iterations$salt$hash</c> (the
/// same shape as lulld's override hash). Online guessing is further limited
/// by per-account lockout and per-IP rate limits.
/// </summary>
public sealed class PasswordHasher(int iterations)
{
    public const int DefaultIterations = 600_000;
    const int SaltBytes = 16, HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time check. A malformed stored value never matches.</summary>
    public static bool Verify(string password, string? encoded)
    {
        var parts = encoded?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2-sha256"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iter) || iter < 1)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iter), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Same work as a real check, for unknown emails, so timing doesn't reveal which accounts exist.</summary>
    public void Waste(string password) => Derive(password, new byte[SaltBytes], iterations);

    static byte[] Derive(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, HashBytes);
}

/// <summary>
/// One-time setup codes: how a new user proves who they are before setting a
/// password (IT hands them out, e.g. by mail merge). 8 characters from an alphabet
/// without look-alikes (~40 bits), shown once, stored as SHA-256.
/// </summary>
public static class SetupCodes
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<char> c = stackalloc char[8];
        for (int i = 0; i < c.Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{c[..4]}-{c[4..]}";
    }

    /// <summary>Forgiving input: case, spaces and dashes don't matter.</summary>
    public static string Normalize(string? input) =>
        new((input ?? "").ToUpperInvariant().Where(ch => ch is not ('-' or ' ')).ToArray());

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)));

    public static bool Matches(string? given, byte[]? stored) =>
        stored is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Hash(given ?? ""), stored);
}
