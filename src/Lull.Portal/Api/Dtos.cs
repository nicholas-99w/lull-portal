using System.Text.Json;
using System.Text.Json.Serialization;
using Lull.Portal.Domain;

namespace Lull.Portal.Api;

// Wire format shared with lulld: mirrors Sources/LullCore/Remote/PortalAPI.swift
// in the Mac repository, and contract/PORTAL_API.md. Tests decode
// contract/fixtures/*.json with these types so the two sides can't drift.
// Unknown fields are ignored (the System.Text.Json default), so either side
// can add fields.

public static class Wire
{
    /// <summary>camelCase, nulls omitted (Swift's encoder omits nil optionals too).</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Contains the enrolment secret: never log it.</summary>
public sealed record RegisterRequest(string SerialNumber, string HardwareUUID, string EnrolmentSecret, string? Version);

/// <summary>The token is a bearer secret; the portal keeps only its hash.</summary>
public sealed record RegisterResponse(string DeviceId, string DeviceToken);

public sealed record WindowPayload(string Start, string End)
{
    /// <summary>Null (omitted on the wire) = follow the school's schedule.</summary>
    public static WindowPayload? From(Window? w) => w is { } v ? new(Window.Format(v.Start), Window.Format(v.End)) : null;
}

public sealed record SettingsPayload(
    long Revision, bool Enabled, WindowPayload? SchoolNights, WindowPayload? Weekend,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? LockFrom = null,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? LockUntil = null,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? ReleasedAt = null,
    bool? SchoolNightsOff = null,
    bool? WeekendOff = null,
    WindowPayload? Holiday = null,
    bool? HolidayOff = null,
    HolidayPeriodPayload[]? HolidayPeriods = null,
    WeekWindowPayload[]? Week = null,
    WeekWindowPayload[]? HolidayWeek = null)
{
    /// <summary>
    /// An opted-out kind sends its flag and no window; the flags are omitted when false.
    /// <paramref name="holidayPeriods"/> go only with a holiday setting (without one they change
    /// nothing), and never filtered by today's date: the same revision must mean the same content.
    /// An advanced schedule goes as its windows, outside <paramref name="schoolHours"/> (required
    /// with one), next to the simple choices it replaces: older Macs apply those instead.
    /// </summary>
    public static SettingsPayload From(DeviceSettings s, IReadOnlyList<HolidayPeriod>? holidayPeriods = null, SchoolHours? schoolHours = null)
    {
        var holiday = s.HolidayMode != ScheduleMode.School && holidayPeriods is { Count: > 0 };
        var week = s.Week?.Windows(schoolHours ?? throw new ArgumentNullException(nameof(schoolHours), "an advanced schedule keeps out of school hours"));
        return new(s.Revision, s.Enabled,
            s.SchoolNightsOff ? null : WindowPayload.From(s.SchoolNights), s.WeekendOff ? null : WindowPayload.From(s.Weekend),
            s.LockFrom, s.LockUntil, s.ReleasedAt,
            s.SchoolNightsOff ? true : null, s.WeekendOff ? true : null,
            s.HolidayOff ? null : WindowPayload.From(s.Holiday), s.HolidayOff ? true : null,
            holiday ? holidayPeriods!.Select(HolidayPeriodPayload.From).ToArray() : null,
            week?.Select(WeekWindowPayload.From).ToArray(),
            holiday && s.HolidayWeek is { } h ? h.Windows(null).Select(WeekWindowPayload.From).ToArray() : null);
    }
}

/// <summary>
/// One window of an advanced schedule (<see cref="WeekWindow"/>): <c>day</c> as the profile
/// spells it ("Mon"), the day it starts; times as for <see cref="WindowPayload"/>, except that
/// the same start and end is a whole 24 hours.
/// </summary>
public sealed record WeekWindowPayload(string Day, string Start, string End)
{
    public static WeekWindowPayload From(WeekWindow w) =>
        new(WeekGrid.DayNames[WeekGrid.DayIndex(w.Day)], Window.Format(w.Start), Window.Format(w.End));
}

/// <summary>
/// On the Mac's clock: <c>start</c> as <c>yyyy-MM-ddTHH:mm</c> and <c>lastDay</c> as
/// <c>yyyy-MM-dd</c> (<see cref="HolidayPeriod"/>).
/// </summary>
public sealed record HolidayPeriodPayload(string Start, string LastDay)
{
    public static HolidayPeriodPayload From(HolidayPeriod p) =>
        new($"{Date(p.StartDay)}T{Window.Format(p.StartTime)}", Date(p.LastDay));

    static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// "2026-10-17T02:00:00Z": UTC, whole seconds. Swift's ISO 8601 decoder (lulld)
/// rejects fractional seconds, so never use the default round-trip format.
/// </summary>
public sealed class UtcSecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteStringValue(v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}

public sealed record ScheduleSummary(string Name, string[] Days, string Start, string End);

/// <summary><c>fullName</c>: the account's display name on the Mac ("Jane Smith"; absent from older lulld).</summary>
public sealed record UserSummary(string Username, bool Exempt, bool AgentConnected, string? FullName = null);

/// <summary>The values of <see cref="CheckIn.Phase"/>: what lulld is doing (its DaemonPhase).</summary>
public static class Phase
{
    /// <summary>No downtime now; <see cref="CheckIn.WindowStart"/> is the next window, if any.</summary>
    public const string Normal = "normal";
    /// <summary>Downtime starts soon (the warning period before <see cref="CheckIn.WindowStart"/>).</summary>
    public const string Warning = "warning";
    /// <summary>In downtime (scheduled, a lock-down, or both merged) until <see cref="CheckIn.WindowEnd"/>.</summary>
    public const string Locked = "locked";
    /// <summary>Unlocked on the Mac by an admin override until the next window.</summary>
    public const string Overridden = "overridden";
    /// <summary>Downtime is off (the profile, or the portal's older all-or-nothing opt-out).</summary>
    public const string Disabled = "disabled";
}

/// <summary>What the Mac is enforcing. Times are lulld's trusted times.</summary>
public sealed record CheckIn(
    string Version,
    bool DebugBuild,
    DateTimeOffset ReportedAt,
    string Phase,
    string? PhaseReason,
    string ScheduleName,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    DateTimeOffset? NextTransition,
    string? TimeZone,
    bool? Enabled,
    ScheduleSummary[] Schedules,
    /// <summary>The school's baseline from the Jamf profile (absent from older lulld).</summary>
    ScheduleSummary[]? ProfileSchedules,
    long? SettingsRevision,
    string? SettingsError,
    bool ConfigValid,
    string[] ConfigErrors,
    string TimeStatus,
    UserSummary[] Users)
{
    /// <summary>
    /// The user the portal pins as the device's primary user: the first GUI
    /// user who isn't exempt (admins and IT accounts are exempt).
    /// </summary>
    public string? PrimaryUserCandidate =>
        Users.FirstOrDefault(u => !u.Exempt && !string.IsNullOrWhiteSpace(u.Username)) is { } u
            ? ChildList.Normalize(u.Username)
            : null;

    /// <summary>
    /// The full name the Mac reports for <paramref name="username"/>, trimmed; null when it isn't
    /// listed or has none (or only its username, which says nothing more).
    /// </summary>
    public string? FullNameOf(string username)
    {
        var name = Users.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.Username) && ChildList.Normalize(u.Username) == username)
            ?.FullName?.Trim();
        if (string.IsNullOrEmpty(name) || ChildList.Normalize(name) == username) return null;
        return name.Length <= MaxFullNameLength ? name : name[..MaxFullNameLength];
    }

    public const int MaxFullNameLength = 100;
}
