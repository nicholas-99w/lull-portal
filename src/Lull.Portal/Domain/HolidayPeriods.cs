namespace Lull.Portal.Domain;

/// <summary>
/// When a parent's school-holiday setting takes over on the Mac (local date
/// and time on the Mac's clock): from <see cref="StartDay"/> at
/// <see cref="StartTime"/>, the end of school hours on the last school day,
/// until the usual downtime starts on <see cref="LastDay"/>, the night before
/// school goes back (or midnight if there's none). lulld applies it
/// (contract/PORTAL_API.md, <c>holidayPeriods</c>).
/// </summary>
public sealed record HolidayPeriod(DateOnly StartDay, TimeOnly StartTime, DateOnly LastDay);

public static class HolidayPeriods
{
    /// <summary>
    /// One period per run of non-school days (weekends and holiday dates) that
    /// contains a holiday, so the weekends either side of a holiday join it.
    /// </summary>
    public static IReadOnlyList<HolidayPeriod> From(IEnumerable<SchoolHoliday> holidays, SchoolHours hours)
    {
        var sorted = holidays.OrderBy(h => h.First).ToList();
        var dates = sorted.SelectMany(h => Enumerable.Range(0, h.Last.DayNumber - h.First.DayNumber + 1).Select(h.First.AddDays))
            .ToHashSet();
        bool NonSchool(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || dates.Contains(d);

        var periods = new List<HolidayPeriod>();
        foreach (var h in sorted)
        {
            // The whole run of non-school days around this holiday: [first, last].
            var first = h.First;
            while (NonSchool(first.AddDays(-1))) first = first.AddDays(-1);
            var last = h.Last;
            while (NonSchool(last.AddDays(1))) last = last.AddDays(1);
            // Holidays in the same run give the same period.
            if (periods.Count > 0 && periods[^1].LastDay >= first) continue;
            periods.Add(new HolidayPeriod(first.AddDays(-1), hours.End, last));
        }
        return periods;
    }
}
