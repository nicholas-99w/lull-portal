namespace Lull.Portal.Domain;

/// <summary>
/// School hours, Monday to Friday, in each Mac's own time zone. Parents can
/// never put downtime, a lock-down or a release inside them; IT can.
/// </summary>
public sealed record SchoolHours(TimeOnly Start, TimeOnly End)
{
    public override string ToString() => $"Mon–Fri {Window.Friendly(Start)} – {Window.Friendly(End)}";
}

/// <summary>Whether parents may "Lock down now", and for how long at most. IT is only held to the Mac's 24 h cap.</summary>
public sealed record LockNowPolicy(bool Enabled, int MaxMinutes);

/// <summary>
/// A school holiday, first to last day inclusive, in each Mac's own time zone.
/// Portal-side only: school hours don't hold parents back on these days. What
/// the Mac itself does on holidays is its Jamf profile's <c>DateExceptions</c>.
/// </summary>
public sealed record SchoolHoliday(DateOnly First, DateOnly Last, string? Name = null)
{
    public bool Contains(DateOnly day) => First <= day && day <= Last;

    /// <summary>"Mon 6 Apr – Fri 17 Apr 2026", or one day.</summary>
    public string Dates => First == Last
        ? Day(First, year: true)
        : $"{Day(First, year: First.Year != Last.Year)} – {Day(Last, year: true)}";

    static string Day(DateOnly d, bool year) =>
        d.ToString(year ? "ddd d MMM yyyy" : "ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Name is { Length: > 0 } ? $"{Name} ({Dates})" : Dates;
}

/// <summary>
/// IT's portal policy. The schedules themselves are IT's Jamf profile (per
/// year or device group); the portal only holds what parents may change.
/// </summary>
public sealed record BoundsPolicy(SchoolHours SchoolHours, LockNowPolicy LockNow, int Version = 0)
{
    /// <summary>In date order. Absent from policies stored before there were holidays.</summary>
    public IReadOnlyList<SchoolHoliday> Holidays { get; init; } = [];

    public bool IsHoliday(DateOnly day) => Holidays.Any(h => h.Contains(day));

    /// <summary>Stored policies with another version (an older shape) are replaced by <see cref="Default"/>.</summary>
    public const int CurrentVersion = 4;

    public static BoundsPolicy Default { get; } = new(
        SchoolHours: new(new(8, 30), new(15, 30)),
        LockNow: new(true, MaxMinutes: 12 * 60),
        Version: CurrentVersion);
}
