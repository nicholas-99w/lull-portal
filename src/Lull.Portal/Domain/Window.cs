using System.Globalization;

namespace Lull.Portal.Domain;

/// <summary>
/// One downtime window as wall-clock times in the Mac's configured time zone.
/// Same rules as lulld (Sources/LullCore/Remote/RemoteSettings.swift in the Mac repository): HH:mm,
/// start != end, and an end at or before the start means "the next day".
/// </summary>
public readonly record struct Window(TimeOnly Start, TimeOnly End)
{
    public static bool TryParseTime(string? s, out TimeOnly time) =>
        // Strict two-digit HH:mm, exactly what lulld accepts.
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"9:30 pm": how times read to people (the status lines use the same format).</summary>
    public static string Friendly(TimeOnly t) => t.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-AU")).ToLowerInvariant();

    /// <summary>"9:30 pm – 6:30 am". <see cref="ToString"/> stays HH:mm, for the audit log and IT's lists.</summary>
    public string Friendly() => $"{Friendly(Start)} – {Friendly(End)}";

    public int DurationMinutes => ((End.Hour * 60 + End.Minute) - (Start.Hour * 60 + Start.Minute) + 1440) % 1440;

    public bool CrossesMidnight => End <= Start;

    public override string ToString() => $"{Format(Start)}–{Format(End)}";
}

/// <summary>What a parent chose for one kind of window (school nights or weekend).</summary>
public enum ScheduleMode
{
    /// <summary>No downtime scheduled on these days.</summary>
    OptOut,
    /// <summary>
    /// The school's schedule (the Jamf profile's, which IT may set per year or device group).
    /// For school holidays: the usual school-night and weekend settings.
    /// </summary>
    School,
    /// <summary>The parent's own times.</summary>
    Custom,
    /// <summary>The parent's hour-by-hour grid (<see cref="WeekGrid"/>). School holidays only: for school weeks see <see cref="DeviceSettings.Week"/>.</summary>
    Advanced,
}

/// <summary>
/// Per-device settings, as edited in the portal and served to lulld. Per kind:
/// opted out (<see cref="SchoolNightsOff"/>), customised (a window), or
/// following the school (no window). School holidays likewise, where no window
/// means the usual settings; they apply only on IT's holiday days
/// (<see cref="HolidayPeriods"/>). <see cref="Enabled"/> is the older
/// all-or-nothing opt-out; the portal now always keeps it true.
/// An advanced schedule (<see cref="Week"/>, <see cref="HolidayWeek"/>) replaces
/// the simple choices, which are kept for when the parent goes back to them and
/// for Macs too old for it.
/// </summary>
public sealed record DeviceSettings(
    bool Enabled, Window? SchoolNights, Window? Weekend, long Revision, DateTimeOffset? LockFrom = null, DateTimeOffset? LockUntil = null,
    DateTimeOffset? ReleasedAt = null, bool SchoolNightsOff = false, bool WeekendOff = false, DateTimeOffset? ReleasedUntil = null,
    DateTimeOffset? LockJoinsAt = null, Window? Holiday = null, bool HolidayOff = false, WeekGrid? Week = null,
    WeekGrid? HolidayWeek = null)
{
    public ScheduleMode HolidayMode =>
        HolidayWeek is not null ? ScheduleMode.Advanced
        : HolidayOff ? ScheduleMode.OptOut : Holiday is null ? ScheduleMode.School : ScheduleMode.Custom;

    /// <summary>"opted out", "same as usual", "23:00–09:00" or "advanced: Sun–Thu 10:00 pm – 8:00 am; …".</summary>
    public string DescribeHoliday() => HolidayMode switch
    {
        ScheduleMode.OptOut => "opted out",
        ScheduleMode.School => "same as usual",
        ScheduleMode.Advanced => $"advanced: {HolidayWeek!.Describe(null)}",
        _ => Holiday!.Value.ToString(),
    };

    /// <summary>"simple" (the per-kind choices apply) or "advanced: Sun–Thu 9:00 pm – 7:00 am; …".</summary>
    public string DescribeWeek(SchoolHours school) => Week is { } w ? $"advanced: {w.Describe(school)}" : "simple";

    public ScheduleMode ModeFor(WindowKind kind) =>
        (kind == WindowKind.SchoolNights ? SchoolNightsOff : WeekendOff) ? ScheduleMode.OptOut
        : For(kind) is null ? ScheduleMode.School : ScheduleMode.Custom;

    /// <summary>"opted out", "school's schedule" or "21:30–06:30".</summary>
    public string Describe(WindowKind kind) => ModeFor(kind) switch
    {
        ScheduleMode.OptOut => "opted out",
        ScheduleMode.School => "school's schedule",
        _ => For(kind)!.Value.ToString(),
    };

    /// <summary>
    /// A "Lock down now" that hasn't ended yet, nor been released: a new lock-down clears
    /// <see cref="ReleasedAt"/>, so a past release alongside one came after it and cut it short.
    /// </summary>
    public bool IsLockedDown(DateTimeOffset now) => LockUntil is { } u && u > now && !(ReleasedAt <= now);

    /// <summary>
    /// A release (or a skipped window that has started) still holding: the downtime
    /// it ended early would otherwise run until <see cref="ReleasedUntil"/>.
    /// Portal-side only; lulld gets just <see cref="ReleasedAt"/>.
    /// </summary>
    public bool IsReleased(DateTimeOffset now) => ReleasedAt <= now && ReleasedUntil > now;

    /// <summary>
    /// Scheduled downtime the lock-down runs into hasn't started yet, so Unlock now can end just the
    /// lock-down. <see cref="LockJoinsAt"/> (portal-side only) is when the next scheduled downtime
    /// starts, as the Mac reported it when the lock-down was saved: the lock-down runs into it if it
    /// lasts until then. (Before lock-downs could be changed, it was kept only when they did.)
    /// </summary>
    public bool LockJoinsLater(DateTimeOffset now) => IsLockedDown(now) && LockJoinsAt > now && LockJoinsAt <= LockUntil;

    /// <summary>"Skip next downtime" before that window starts: <see cref="ReleasedAt"/> is its start.</summary>
    public bool IsSkipPending(DateTimeOffset now) => ReleasedAt > now;

    /// <summary>A lock-down, release or skip in effect, which "Reset overrides" clears.</summary>
    public bool HasOverride(DateTimeOffset now) => OverrideAt(now) is not Override.None;

    /// <summary>
    /// The lock-down, unlock or skip in effect at <paramref name="now"/>, if any. They can't overlap:
    /// a lock-down clears any release or skip, a skip is offered only when nothing else is in
    /// effect, and an unlock that comes after a lock-down ends it.
    /// </summary>
    public Override OverrideAt(DateTimeOffset now) =>
        IsLockedDown(now) ? new Override.LockDown(LockFrom ?? LockUntil!.Value, LockUntil!.Value, LockJoinsAt)
        : IsSkipPending(now) ? new Override.Skip(ReleasedAt!.Value, ReleasedUntil ?? ReleasedAt.Value)
        : IsReleased(now) ? new Override.Unlock(ReleasedAt!.Value, ReleasedUntil!.Value)
        : new Override.None();

    /// <summary>The same downtime schedule (each kind's choice and times, the holiday choice, and any advanced schedules).</summary>
    public bool SameSchedule(DeviceSettings other) =>
        (Enabled, SchoolNights, SchoolNightsOff, Weekend, WeekendOff, Holiday, HolidayOff, Week, HolidayWeek)
        == (other.Enabled, other.SchoolNights, other.SchoolNightsOff, other.Weekend, other.WeekendOff, other.Holiday, other.HolidayOff,
            other.Week, other.HolidayWeek);

    /// <summary>
    /// Without any release or skip. A lock-down the release cut short (one that ran into scheduled
    /// downtime is left in place by a release, which ended both) now ends at the release, so it
    /// doesn't come back.
    /// </summary>
    public DeviceSettings WithoutRelease() => this with
    {
        ReleasedAt = null,
        ReleasedUntil = null,
        LockUntil = ReleasedAt is { } at && LockFrom <= at && LockUntil > at ? at : LockUntil,
    };

    public Window? For(WindowKind kind) => kind == WindowKind.SchoolNights ? SchoolNights : Weekend;

    /// <summary>A new Mac: follows the school's schedule, downtime on.</summary>
    public static DeviceSettings FollowSchool(long revision) => new(true, null, null, revision);
}

public enum WindowKind
{
    SchoolNights,
    Weekend,
}
