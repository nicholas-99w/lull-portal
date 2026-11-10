using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class DeviceSettingsTests
{
    static readonly DeviceSettings Settings = new(true, new(new(21, 30), new(6, 30)), null, 1);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    [Fact]
    public void OnlyTheSchedulesMakeADifferentSchedule()
    {
        Assert.True(Settings.SameSchedule(Settings with { Revision = 2, LockFrom = Now, LockUntil = Now.AddHours(1), ReleasedAt = Now }));
        Assert.False(Settings.SameSchedule(Settings with { SchoolNights = new(new(21, 0), new(6, 30)) }));
        Assert.False(Settings.SameSchedule(Settings with { WeekendOff = true }));
        Assert.False(Settings.SameSchedule(Settings with { Holiday = new(new(23, 0), new(9, 0)) }));
        Assert.False(Settings.SameSchedule(Settings with { Week = WeekGrid.Empty }));
        Assert.False(Settings.SameSchedule(Settings with { HolidayWeek = WeekGrid.Empty }));
        Assert.True((Settings with { Week = WeekGrid.Empty }).SameSchedule(Settings with { Week = WeekGrid.FromCells([]) }));
    }

    [Fact]
    public void WithoutReleaseClearsAReleaseOrASkip()
    {
        var released = Settings with { ReleasedAt = Now.AddMinutes(-5), ReleasedUntil = Now.AddHours(6) };
        Assert.False(released.WithoutRelease().IsReleased(Now));
        var skipping = Settings with { ReleasedAt = Now.AddMinutes(10), ReleasedUntil = Now.AddHours(9) };
        Assert.False(skipping.WithoutRelease().IsSkipPending(Now));
        Assert.Equal(Settings, Settings.WithoutRelease()); // nothing to clear
    }

    /// <summary>
    /// A lock-down that ran into scheduled downtime is left in place by Unlock now (the release cut
    /// both): without the release it would lock the Mac again, so it ends where the release was.
    /// </summary>
    [Fact]
    public void ALockDownTheReleaseCutShortDoesntComeBack()
    {
        var releasedAt = Now.AddMinutes(-10);
        var cut = Settings with { LockFrom = Now.AddMinutes(-40), LockUntil = Now.AddMinutes(20), ReleasedAt = releasedAt, ReleasedUntil = Now.AddHours(6) };
        Assert.False(cut.IsLockedDown(Now));
        var cleared = cut.WithoutRelease();
        Assert.False(cleared.IsLockedDown(Now));
        Assert.Equal(releasedAt, cleared.LockUntil);
        Assert.Null(cleared.ReleasedAt);

        // One that's still running (a skip doesn't end a lock-down) is kept as it is.
        var running = Settings with { LockFrom = Now.AddMinutes(-5), LockUntil = Now.AddMinutes(30), ReleasedAt = Now.AddMinutes(60) };
        Assert.Equal(Now.AddMinutes(30), running.WithoutRelease().LockUntil);
    }
}
