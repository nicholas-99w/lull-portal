namespace Lull.Portal.Domain;

/// <summary>
/// The rules for what parents may do, built around school hours: no
/// downtime, lock-down or release may touch Mon–Fri school hours. A lock-down,
/// release or skip may on IT's school holidays; weekly downtime never, as it
/// repeats into term. Pure (wall-clock arithmetic, no I/O), so it's
/// unit-tested directly.
/// </summary>
public static class SettingsRules
{
    public static string Label(WindowKind kind) => kind == WindowKind.SchoolNights ? "School nights" : "Weekend";

    static string T(TimeOnly t) => Window.Friendly(t);

    static int Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    public static string Hours(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60} h" : minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";

    // MARK: Week arithmetic (wall-clock minutes from Monday 00:00)

    const int Week = 7 * 1440;

    /// <summary>Monday = 0 ... Sunday = 6.</summary>
    static int DayIndex(DayOfWeek d) => ((int)d + 6) % 7;

    /// <summary>Days a window of this kind starts on (lulld: RemoteSettings.schoolNightDays / weekendDays).</summary>
    public static DayOfWeek[] StartDays(WindowKind kind) => kind == WindowKind.SchoolNights
        ? [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday]
        : [DayOfWeek.Friday, DayOfWeek.Saturday];

    static IEnumerable<(int Start, int End)> SchoolBlocks(SchoolHours h) =>
        Enumerable.Range(0, 5).Select(day => (day * 1440 + Minutes(h.Start), day * 1440 + Minutes(h.End)));

    /// <summary>Does [start, start + length) overlap any school block, wrapping round the week?</summary>
    static bool TouchesSchool(int start, int length, SchoolHours h) =>
        length > 0 && SchoolBlocks(h).Any(b =>
            new[] { -Week, 0, Week }.Any(shift => start + shift < b.End && b.Start < start + shift + length));

    public static bool OverlapsSchoolHours(WindowKind kind, Window w, SchoolHours h) =>
        StartDays(kind).Any(d => TouchesSchool(DayIndex(d) * 1440 + Minutes(w.Start), w.DurationMinutes, h));

    // MARK: Windows

    /// <summary>
    /// What lulld itself would refuse. Applies to everyone, including IT:
    /// start == end would be a 24-hour lock every day.
    /// </summary>
    public static IReadOnlyList<string> CheckShape(WindowKind kind, Window w) => CheckShape(Label(kind), w);

    static IReadOnlyList<string> CheckShape(string label, Window w) =>
        w.Start == w.End ? [$"{label}: start and end can't be the same time."] : [];

    public const string HolidayLabel = "School holidays";

    public const string AdvancedLabel = "Advanced schedule";

    /// <summary>
    /// An advanced school-week schedule: no hour wholly in school hours, for IT too (their
    /// windows leave out school hours anyway, so ticking one would do nothing). Hours partly in
    /// them are fine: downtime there is just the rest of the hour.
    /// </summary>
    public static IReadOnlyList<string> CheckWeek(WeekGrid grid, SchoolHours school) =>
        grid.TouchesSchool(school) ? [$"{AdvancedLabel}: downtime can't be in school hours ({school})."] : [];

    /// <summary>All a holiday window needs, for parents too: no school hours apply on holiday days.</summary>
    public static IReadOnlyList<string> CheckHolidayShape(Window w) => CheckShape(HolidayLabel, w);

    /// <summary>Shape plus school hours: what a parent may save.</summary>
    public static IReadOnlyList<string> CheckForParent(WindowKind kind, Window w, BoundsPolicy policy)
    {
        var errors = new List<string>(CheckShape(kind, w));
        if (errors.Count == 0 && OverlapsSchoolHours(kind, w, policy.SchoolHours))
            errors.Add(OverlapMessage(kind, policy.SchoolHours));
        return errors;
    }

    /// <summary>
    /// The weekend's times apply to Friday and Saturday alike, so only Friday can clash, and they can't
    /// be changed for Friday alone here: what's left is the advanced schedule, which goes hour by hour.
    /// </summary>
    static string OverlapMessage(WindowKind kind, SchoolHours h) => kind == WindowKind.SchoolNights
        ? $"{Label(kind)}: downtime can't overlap school hours ({h}). School-night windows must start after {T(h.End)} and end by {T(h.Start)}."
        : $"{Label(kind)}: unable to set this downtime because it would interfere with Friday's school hours ({T(h.Start)} – {T(h.End)}). "
            + "Use the Advanced schedule for more flexibility.";

    /// <summary>Problems with a policy IT is about to save.</summary>
    public static IReadOnlyList<string> CheckPolicy(BoundsPolicy p)
    {
        var errors = new List<string>();
        if (p.SchoolHours.End <= p.SchoolHours.Start) errors.Add("School hours must end after they start, on the same day.");
        if (p.LockNow.MaxMinutes < 5 || p.LockNow.MaxMinutes > MaxLockMinutes)
            errors.Add($"Lock down now: the longest lock-down must be between 5 minutes and {Hours(MaxLockMinutes)}.");
        if (p.Holidays.Any(h => h.Last < h.First)) errors.Add("School holidays must end on or after their first day.");
        return errors;
    }

    public const int MaxHolidayNameLength = 100;

    /// <summary>
    /// Problems with a holiday IT is about to add: dates in order, not over by
    /// <paramref name="today"/> (the portal's; Macs' own dates may be a day
    /// either side) and not overlapping one already there.
    /// </summary>
    public static IReadOnlyList<string> CheckHoliday(SchoolHoliday holiday, IEnumerable<SchoolHoliday> existing, DateOnly today)
    {
        var errors = new List<string>();
        if (holiday.Last < holiday.First) errors.Add("The last day of a holiday can't be before its first day.");
        else if (holiday.Last < today.AddDays(-1)) errors.Add("That holiday is already over.");
        else if (existing.FirstOrDefault(h => h.First <= holiday.Last && holiday.First <= h.Last) is { } clash)
            errors.Add($"Those dates overlap {clash}.");
        if (holiday.Name?.Length > MaxHolidayNameLength) errors.Add($"Keep the name to {MaxHolidayNameLength} characters.");
        return errors;
    }

    /// <summary>
    /// Fits a parent's window around changed school hours: end it at school
    /// start, or start it at school end (whichever keeps it valid and no longer
    /// than it was), else back to following the school (null). "Follow the
    /// school" (null) is returned unchanged.
    /// </summary>
    public static Window? Clamp(WindowKind kind, Window? window, BoundsPolicy policy)
    {
        if (window is not { } w || CheckForParent(kind, w, policy).Count == 0) return window;
        var h = policy.SchoolHours;
        Window[] candidates = [new(w.Start, h.Start), new(h.End, w.End), new(h.End, h.Start)];
        foreach (var c in candidates)
            if (c.Start != c.End && c.DurationMinutes <= w.DurationMinutes && CheckForParent(kind, c, policy).Count == 0)
                return c;
        return null;
    }

    /// <summary>
    /// Next settings revision. Based on the portal's clock (unix ms) and never
    /// below previous + 1, so revisions keep increasing even after a database
    /// restore; lulld refuses anything lower than what it has cached.
    /// </summary>
    public static long NextRevision(long previous, DateTimeOffset now) =>
        Math.Max(previous + 1, now.ToUnixTimeMilliseconds());

    // MARK: Lock down now and release

    /// <summary>lulld refuses longer lock-downs (LockNowWindow.maxDuration).</summary>
    public const int MaxLockMinutes = 24 * 60;

    /// <summary>
    /// Do <paramref name="minutes"/> from <paramref name="from"/> (whole minutes on
    /// the Mac's clock) touch school hours on a school day: Mon–Fri, not a holiday?
    /// </summary>
    static bool TouchesSchoolDay(DateTimeOffset from, int minutes, TimeZoneInfo zone, BoundsPolicy p)
    {
        if (minutes <= 0) return false;
        var local = TimeZoneInfo.ConvertTime(from, zone).DateTime;
        var start = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMinute));
        var end = start.AddMinutes(minutes);
        for (var day = DateOnly.FromDateTime(start); day.ToDateTime(TimeOnly.MinValue) < end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || p.IsHoliday(day)) continue;
            if (start < day.ToDateTime(p.SchoolHours.End) && day.ToDateTime(p.SchoolHours.Start) < end) return true;
        }
        return false;
    }

    /// <summary>In school hours on the Mac's clock, on a school day (not a holiday).</summary>
    public static bool InSchoolHours(DateTimeOffset t, TimeZoneInfo zone, BoundsPolicy p) =>
        TouchesSchoolDay(t, 1, zone, p);

    /// <summary>
    /// Whether this lock-down may be saved. Parents: lock-downs on, no longer
    /// than IT's maximum, and nowhere near school hours on a school day (judged
    /// in the Mac's time zone). IT: any time, up to the Mac's 24 h cap.
    /// </summary>
    public static IReadOnlyList<string> CheckLockNow(
        BoundsPolicy p, DateTimeOffset now, DateTimeOffset until, TimeZoneInfo zone, bool isAdmin)
    {
        var errors = new List<string>();
        var minutes = (until - now).TotalMinutes;
        if (minutes < 1) errors.Add("Choose a time in the future.");
        int max = isAdmin ? MaxLockMinutes : Math.Min(p.LockNow.MaxMinutes, MaxLockMinutes);
        if (minutes > max + 0.5) errors.Add($"A lock-down can last at most {Hours(max)}.");
        if (isAdmin || errors.Count > 0) return errors;
        if (!p.LockNow.Enabled) errors.Add("Lock down now is turned off by your school.");
        else if (InSchoolHours(now, zone, p))
            errors.Add($"Lock down now isn't available during school hours ({p.SchoolHours}).");
        else if (TouchesSchoolDay(now, (int)Math.Ceiling(minutes), zone, p))
            errors.Add($"A lock-down can't run into school hours: on school days it must end by {T(p.SchoolHours.Start)}.");
        return errors;
    }

    /// <summary>Parents can't release during school hours (except on holidays); IT always can.</summary>
    public static IReadOnlyList<string> CheckRelease(BoundsPolicy p, DateTimeOffset now, TimeZoneInfo zone, bool isAdmin) =>
        !isAdmin && InSchoolHours(now, zone, p)
            ? [$"The Mac can't be unlocked during school hours ({p.SchoolHours})."]
            : [];

    /// <summary>Parents can't skip downtime that starts during school hours (as for a release then); IT always can.</summary>
    public static IReadOnlyList<string> CheckSkip(BoundsPolicy p, DateTimeOffset windowStart, TimeZoneInfo zone, bool isAdmin) =>
        !isAdmin && InSchoolHours(windowStart, zone, p)
            ? [$"Downtime that starts during school hours ({p.SchoolHours}) can't be skipped."]
            : [];

    /// <summary>
    /// The next time the Mac's clock shows <paramref name="time"/>, at least a
    /// minute from now (for "lock until 4:30 pm"). A time skipped by a DST
    /// change resolves to the hour after.
    /// </summary>
    public static DateTimeOffset NextOccurrence(TimeOnly time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = localNow.Date + time.ToTimeSpan();
        if (candidate <= localNow.DateTime.AddMinutes(1)) candidate = candidate.AddDays(1);
        if (zone.IsInvalidTime(candidate)) candidate = candidate.AddHours(1);
        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
    }

    /// <summary>Whole seconds: lulld's ISO 8601 parser doesn't take fractions.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());
}
