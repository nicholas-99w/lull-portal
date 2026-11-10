using System.Security.Cryptography;
using System.Text;

namespace Lull.Portal.Auth;

/// <summary>
/// Time-based one-time codes (RFC 6238, as authenticator apps use them): HMAC-SHA1,
/// 6 digits, 30-second steps. A code from the step before or after is accepted too,
/// for a phone's clock being a little off. Pure, so it's unit-tested directly.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    const int SecretBytes = 20; // 160 bits, as RFC 4226 recommends

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset t) => t.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The 6-digit code for <paramref name="step"/>.</summary>
    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        int offset = hash[^1] & 0x0f;
        int value = (hash[offset] & 0x7f) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step <paramref name="code"/> belongs to (this one, or one either side), or null if it
    /// matches none. Spaces are ignored. Callers refuse a step at or before the last one used, so a
    /// code works once.
    /// </summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now)
    {
        var given = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (given.Length != Digits || !given.All(char.IsAsciiDigit)) return null;
        var bytes = Encoding.ASCII.GetBytes(given);
        long? matched = null;
        var current = StepAt(now);
        // Every step is checked, so the time taken doesn't say which one matched.
        for (var step = current - 1; step <= current + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), bytes))
                matched ??= step;
        return matched;
    }

    /// <summary>RFC 4648 base32 without padding: how authenticator apps take a key typed in.</summary>
    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[buffer >> (bits - 5) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[buffer << (5 - bits) & 31]);
        return sb.ToString();
    }

    /// <summary>
    /// The key as an <c>otpauth://</c> link, which opens an authenticator app on a phone or Mac.
    /// </summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
}
