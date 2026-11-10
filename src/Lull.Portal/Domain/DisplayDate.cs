using System.Globalization;

namespace Lull.Portal.Domain;

/// <summary>
/// How the portal writes a date or a moment that isn't tied to a Mac: "5 Nov 2026" and
/// "5 Nov 2026, 10:02 UTC", always in UTC. Times tied to a Mac are in its own time zone (<see cref="DeviceStatusView.When"/>).
/// </summary>
public static class DisplayDate
{
    static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-AU");

    public static string Date(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy", Culture);

    public static string DateTime(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", Culture);

    /// <summary>Where the order within a minute can matter (the audit log).</summary>
    public static string DateTimeWithSeconds(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy, HH:mm:ss 'UTC'", Culture);

    /// <summary>For a <c>time</c> element's <c>datetime</c>: admin.js shows it in the reader's own time zone.</summary>
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
