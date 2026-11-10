using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class HolidayPeriodsTests
{
    static readonly SchoolHours Hours = new(new(8, 30), new(15, 30));
    static SchoolHoliday H(string first, string last) => new(DateOnly.Parse(first), DateOnly.Parse(last));
    static HolidayPeriod P(string startDay, string lastDay) => new(DateOnly.Parse(startDay), new(15, 30), DateOnly.Parse(lastDay));

    [Fact]
    public void FromSchoolEndOnTheLastSchoolDayToTheNightBeforeSchool()
    {
        // Term ends Fri 16 Oct, holidays Mon 19 – Fri 23, school back Mon 26: the weekends join in.
        Assert.Equal([P("2026-10-16", "2026-10-25")], HolidayPeriods.From([H("2026-10-19", "2026-10-23")], Hours));
        // Entered with the weekends, the same.
        Assert.Equal([P("2026-10-16", "2026-10-25")], HolidayPeriods.From([H("2026-10-17", "2026-10-25")], Hours));
        // The start follows school hours.
        Assert.Equal(new TimeOnly(15, 0), HolidayPeriods.From([H("2026-10-19", "2026-10-23")], new(new(8, 0), new(15, 0)))[0].StartTime);
    }

    [Fact]
    public void APupilFreeDay()
    {
        // Wednesday off: from Tuesday after school to Wednesday night (a school night again).
        Assert.Equal([P("2026-10-20", "2026-10-21")], HolidayPeriods.From([H("2026-10-21", "2026-10-21")], Hours));
        // A Friday off makes a long weekend: Thursday after school to Sunday.
        Assert.Equal([P("2026-10-22", "2026-10-25")], HolidayPeriods.From([H("2026-10-23", "2026-10-23")], Hours));
    }

    [Fact]
    public void HolidaysJoinedByAWeekendAreOne()
    {
        Assert.Equal([P("2026-10-09", "2026-10-25")],
            HolidayPeriods.From([H("2026-10-19", "2026-10-23"), H("2026-10-12", "2026-10-16")], Hours));
        // One school day apart (Wed 21): two periods.
        Assert.Equal([P("2026-10-16", "2026-10-20"), P("2026-10-21", "2026-10-22")],
            HolidayPeriods.From([H("2026-10-19", "2026-10-20"), H("2026-10-22", "2026-10-22")], Hours));
    }

    [Fact]
    public void NoHolidaysNoPeriods() => Assert.Empty(HolidayPeriods.From([], Hours));
}
