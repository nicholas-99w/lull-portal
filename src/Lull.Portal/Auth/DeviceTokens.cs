using System.Security.Cryptography;
using System.Text;

namespace Lull.Portal.Auth;

/// <summary>
/// Device bearer tokens: 256 random bits, stored only as a SHA-256 hash, so a
/// database leak doesn't let anyone act as a Mac. Lookup is by hash; with
/// random 256-bit tokens that leaks nothing useful through timing.
/// </summary>
public static class DeviceTokens
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Constant-time comparison of the enrolment secret.</summary>
    public static bool SecretMatches(string expected, string? given) =>
        expected.Length > 0 && CryptographicOperations.FixedTimeEquals(Hash(expected), Hash(given ?? ""));

    /// <summary>The token from <c>Authorization: Bearer ...</c>, or null.</summary>
    public static string? FromHeader(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[prefix.Length..].Trim();
        return token.Length is > 0 and <= 128 ? token : null;
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
