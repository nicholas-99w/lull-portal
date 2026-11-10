using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class WeekGridTests
{
    static readonly SchoolHours School = BoundsPolicy.Default.SchoolHours; // 08:30–15:30

    const int Mon = 0, Tue = 1, Fri = 4, Sat = 5, Sun = 6;

    /// <summary>The cells from <paramref name="day"/> <paramref name="from"/>:00 for <paramref name="hours"/> hours, round the week.</summary>
    static IEnumerable<int> Run(int day, int from, int hours) =>
        Enumerable.Range(0, hours).Select(i => (WeekGrid.Cell(day, from) + i) % WeekGrid.Cells);

    static WeekGrid Grid(params IEnumerable<int>[] runs) => WeekGrid.FromCells(runs.SelectMany(r => r))!;

    static WeekWindow W(DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        new(day, new(startHour, startMinute), new(endHour, endMinute));

    [Fact]
    public void StoredFormRoundTrips()
    {
        var grid = Grid(Run(Mon, 21, 10));
        Assert.Equal(grid, WeekGrid.Parse(grid.Text));
        Assert.Null(WeekGrid.Parse("01"));
        Assert.Null(WeekGrid.Parse(new string('2', WeekGrid.Cells)));
        Assert.Null(WeekGrid.FromCells([WeekGrid.Cells]));
        Assert.Null(WeekGrid.FromCells([-1]));
    }

    [Fact]
    public void ANightIsOneWindowAcrossMidnight()
    {
        Assert.Equal([W(DayOfWeek.Monday, 21, 0, 7, 0)], Grid(Run(Mon, 21, 10)).Windows(School));
    }

    [Fact]
    public void ARunRoundTheEndOfTheWeekIsOneWindow()
    {
        Assert.Equal([W(DayOfWeek.Sunday, 22, 0, 6, 0)], Grid(Run(Sun, 22, 8)).Windows(School));
    }

    [Fact]
    public void HoursPartlyInSchoolAreJustTheRest()
    {
        // 3 pm and 4 pm on Monday, school until 3:30: downtime from 3:30. 8 am Tuesday, school from 8:30: until then.
        var grid = Grid(Run(Mon, 15, 2), Run(Tue, 8, 1));
        Assert.Equal([W(DayOfWeek.Monday, 15, 30, 17, 0), W(DayOfWeek.Tuesday, 8, 0, 8, 30)], grid.Windows(School));
        Assert.Equal(SchoolInHour.FirstPart, WeekGrid.School(WeekGrid.Cell(Mon, 15), School));
        Assert.Equal(SchoolInHour.LastPart, WeekGrid.School(WeekGrid.Cell(Tue, 8), School));
        Assert.Equal(SchoolInHour.Whole, WeekGrid.School(WeekGrid.Cell(Tue, 9), School));
        Assert.Equal(SchoolInHour.None, WeekGrid.School(WeekGrid.Cell(Sat, 9), School));
        Assert.Equal(SchoolInHour.None, WeekGrid.School(WeekGrid.Cell(Tue, 9), null));
        Assert.False(grid.TouchesSchool(School));
    }

    [Fact]
    public void SchoolHoursAreNeverDowntimeWhateverTheGridSays()
    {
        var grid = Grid(Run(Mon, 0, 24));
        Assert.True(grid.TouchesSchool(School));
        Assert.Equal([W(DayOfWeek.Monday, 0, 0, 8, 30), W(DayOfWeek.Monday, 15, 30, 0, 0)], grid.Windows(School));
        Assert.Equal(Grid(Run(Mon, 0, 9), Run(Mon, 15, 9)), grid.WithoutSchool(School));
        Assert.NotEmpty(SettingsRules.CheckWeek(grid, School));
        Assert.Empty(SettingsRules.CheckWeek(grid.WithoutSchool(School), School));
    }

    [Fact]
    public void ARunOfADayOrMoreIsSplitAtMidnight()
    {
        // Friday 10 pm to Sunday 8 am: lulld's windows last at most a day.
        Assert.Equal(
            [W(DayOfWeek.Friday, 22, 0, 0, 0), W(DayOfWeek.Saturday, 0, 0, 0, 0), W(DayOfWeek.Sunday, 0, 0, 8, 0)],
            Grid(Run(Fri, 22, 34)).Windows(School));
        // Exactly a day, from 7 am.
        Assert.Equal([W(DayOfWeek.Saturday, 7, 0, 0, 0), W(DayOfWeek.Sunday, 0, 0, 7, 0)], Grid(Run(Sat, 7, 24)).Windows(School));
        Assert.True(Grid(Run(Sat, 0, 24)).Windows(null).Single().AllDay);
    }

    [Fact]
    public void EveryHourIsEveryDayAllDay()
    {
        var all = Grid(Enumerable.Range(0, WeekGrid.Cells)).Windows(null);
        Assert.Equal(7, all.Count);
        Assert.All(all, w => Assert.True(w.AllDay));
        Assert.Empty(WeekGrid.Empty.Windows(null));
    }

    [Fact]
    public void DescribeGroupsTheSameTimes()
    {
        var grid = Grid(Run(Sun, 21, 10), Run(Mon, 21, 10), Run(Tue, 21, 10), Run(Sat, 0, 24));
        Assert.Equal("Sun–Tue 9:00 pm – 7:00 am; Sat all day", grid.Describe(School));
        Assert.Equal("Mon, Sun 9:00 pm – 7:00 am", grid.Describe(School, [DayOfWeek.Sunday, DayOfWeek.Monday]));
        Assert.Equal("no downtime", WeekGrid.Empty.Describe(School));
    }

    [Fact]
    public void TheSimpleSettingsStartTheGridToTheNearestHour()
    {
        var settings = new DeviceSettings(true, new(new(21, 30), new(6, 30)), null, 1, WeekendOff: true);
        var grid = WeekGrid.Covering(SchoolSchedule.Simple(settings, null));
        // 9:30 pm – 6:30 am covers half of 9 pm and of 6 am: those count.
        Assert.Equal(
            Grid(Run(Sun, 21, 10), Run(Mon, 21, 10), Run(Mon + 2, 21, 10), Run(Mon + 3, 21, 10), Run(Tue, 21, 10)),
            grid);

        // Following the school: its schedule, as the Mac reported it.
        var profile = new[] { new Api.ScheduleSummary("Weekend", ["Fri", "Sat"], "23:00", "08:00") };
        var follow = WeekGrid.Covering(SchoolSchedule.Simple(DeviceSettings.FollowSchool(1) with { SchoolNightsOff = true }, profile));
        Assert.Equal(Grid(Run(Fri, 23, 9), Run(Sat, 23, 9)), follow);
    }
}
