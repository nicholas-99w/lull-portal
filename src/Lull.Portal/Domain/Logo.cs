namespace Lull.Portal.Domain;

/// <summary>
/// The school's logo: a PNG, JPEG or WebP image, recognised by its first bytes rather than
/// its name or the browser's word for it. No SVG: it can carry script.
/// </summary>
public static class Logo
{
    public const string Accept = "image/png,image/jpeg,image/webp";

    /// <summary>The image's media type, or null if it's none of the three.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
