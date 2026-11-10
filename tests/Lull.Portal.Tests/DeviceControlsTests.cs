using Lull.Portal.Api;
using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

/// <summary>Which device controls are offered, from a Mac's settings and last check-in (Sydney, Thursday 15 Oct).</summary>
public class DeviceControlsTests
{
    /// <summary>The fixture: in tonight's scheduled downtime (9:30 pm to 6:30 am), having applied <see cref="Settings"/>.</summary>
    static readonly CheckIn Locked = Fixtures.Read<CheckIn>("checkin.json");
    static readonly DeviceSettings Settings = new(true, null, null, Locked.SettingsRevision!.Value);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:03:00Z"); // 11:03 pm
    static readonly DateTimeOffset WindowStart = Locked.WindowStart!.Value, WindowEnd = Locked.WindowEnd!.Value;

    /// <summary>The next change the Mac reports, as lulld does: the window's end while locked, else its start.</summary>
    static CheckIn Report(string phase, DateTimeOffset? start, DateTimeOffset? end) =>
        Locked with { Phase = phase, WindowStart = start, WindowEnd = end, NextTransition = phase == Phase.Locked ? end : start };

    static DeviceControls Controls(DeviceSettings s, CheckIn? report, DateTimeOffset? now = null, bool isAdmin = false)
    {
        var at = now ?? Now;
        var status = DeviceStatusView.From(s, report, report is null ? null : at.AddMinutes(-1), at);
        return DeviceControls.For(s, report, status, BoundsPolicy.Default, at, isAdmin);
    }

    static DeviceSettings Pending(DeviceSettings s) => s with { Revision = s.Revision + 1 };

    [Fact]
    public void ScheduledDowntimeCanBeUnlocked()
    {
        var c = Controls(Settings, Locked);
        Assert.True(c.InScheduledDowntime);
        Assert.True(c.CanRelease);
        Assert.False(c.CanLock); // already locked
        Assert.False(c.HasOverride);
        Assert.Equal(WindowEnd, c.ReleaseWindowEnd(Now));
    }

    /// <summary>The bug that started the audit: after Reset overrides, the Mac still says "locked" (the lock-down).</summary>
    [Fact]
    public void AfterResettingALockDownNothingIsOfferedUntilTheMacChecksIn()
    {
        var lockDown = Report(Phase.Locked, Now.AddMinutes(-30), Now.AddMinutes(30));
        var c = Controls(Pending(Settings), lockDown);
        Assert.True(c.AwaitingCheckIn);
        Assert.False(c.CanRelease);
        Assert.False(c.CanLock);
        Assert.False(c.HasOverride);
        Assert.Null(c.NextWindow);
    }

    [Fact]
    public void APendingLockDownCanOnlyBeReset()
    {
        var s = Pending(Settings) with { LockFrom = Now, LockUntil = Now.AddHours(1) };
        var c = Controls(s, Report(Phase.Normal, WindowStart.AddDays(1), WindowEnd.AddDays(1)));
        Assert.True(c.AwaitingCheckIn);
        Assert.Equal(Now.AddHours(1), c.LockedDownUntil);
        Assert.True(c.HasOverride);
        Assert.True(c.CanEndLockDown);
        Assert.False(c.CanLock);
        Assert.False(c.CanRelease);
    }

    [Fact]
    public void SettingsTheMacRefusedDontHoldTheControlsBack()
    {
        var c = Controls(Pending(Settings), Locked with { SettingsError = "bad time" });
        Assert.False(c.AwaitingCheckIn);
        Assert.True(c.CanRelease);
    }

    [Fact]
    public void AnOverdueCheckInHoldsTheControlsBack()
    {
        var c = Controls(Settings, Locked, now: WindowEnd.AddMinutes(11)); // should have unlocked by now
        Assert.True(c.AwaitingCheckIn);
        Assert.False(c.CanRelease);
        Assert.False(c.CanLock);
    }

    [Fact]
    public void ALockDownThatRunsIntoScheduledDowntimeIsHandledAsThatDowntime()
    {
        var at = WindowStart.AddMinutes(-30); // 9:00 pm, downtime at 9:30 pm
        var s = Settings with { LockFrom = at, LockUntil = at.AddHours(1), LockJoinsAt = WindowStart };
        var c = Controls(s, Report(Phase.Locked, at, WindowEnd), now: at.AddMinutes(10));
        Assert.True(c.LockDownJoinsSchedule);
        Assert.Equal(WindowEnd, c.LockedDownUntil); // merged with the scheduled downtime
        Assert.Equal(WindowStart, c.ScheduleStartsAt); // Unlock now ends just the lock-down until then
        Assert.True(c.CanRelease);
        Assert.False(c.HasOverride);
        Assert.False(c.CanEndLockDown);
    }

    [Fact]
    public void ALockDownOnItsOwnEndsWithResetOverrides()
    {
        var s = Settings with { LockFrom = Now, LockUntil = Now.AddMinutes(30) };
        var c = Controls(s, Report(Phase.Locked, Now, Now.AddMinutes(30)), now: Now.AddMinutes(1));
        Assert.False(c.LockDownJoinsSchedule);
        Assert.False(c.CanRelease);
        Assert.True(c.HasOverride);
        Assert.True(c.CanEndLockDown);
        Assert.Null(c.ResetNotAllowed);
    }

    [Fact]
    public void LockDownNowIsntOfferedDuringAnUnlockOrSkip()
    {
        var released = Settings with { ReleasedAt = Now.AddMinutes(-5), ReleasedUntil = WindowEnd };
        var c = Controls(released, Report(Phase.Normal, WindowStart.AddDays(1), WindowEnd.AddDays(1)));
        Assert.Equal(WindowEnd, c.ReleasedUntil);
        Assert.False(c.CanLock); // it would cancel the unlock and bring tonight's downtime back
        Assert.True(c.HasOverride);

        var soon = Now.AddMinutes(10);
        var skipping = Settings with { ReleasedAt = soon, ReleasedUntil = soon.AddHours(8) };
        var s = Controls(skipping, Report(Phase.Normal, soon.AddDays(1), soon.AddDays(1).AddHours(8)));
        Assert.Equal(soon, s.SkippingAt);
        Assert.Null(s.NextWindow);
        Assert.False(s.CanLock);
    }

    [Fact]
    public void DowntimeStartingSoonCanBeSkipped()
    {
        var at = WindowStart.AddMinutes(-10);
        var c = Controls(Settings, Report(Phase.Warning, WindowStart, WindowEnd), now: at);
        Assert.Equal((WindowStart, WindowEnd), c.NextWindow);
        Assert.Null(c.SkipNotAllowed);
        // Not from settings the Mac hasn't applied: the window it reports may be gone.
        Assert.Null(Controls(Pending(Settings), Report(Phase.Warning, WindowStart, WindowEnd), now: at).NextWindow);
    }

    [Fact]
    public void ParentsCantUnlockDuringSchoolHoursButItCan()
    {
        var tuesday1pm = DateTimeOffset.Parse("2026-10-20T02:00:00Z");
        var inDowntime = Report(Phase.Locked, tuesday1pm.AddHours(-1), tuesday1pm.AddHours(1));
        var parent = Controls(Settings, inDowntime, now: tuesday1pm);
        Assert.False(parent.CanRelease);
        Assert.Contains("school hours", parent.ReleaseNotAllowed);
        Assert.True(Controls(Settings, inDowntime, now: tuesday1pm, isAdmin: true).CanRelease);
    }

    [Fact]
    public void ALockDownRemembersWhenTheNextDowntimeStartsOnlyFromAppliedSettings()
    {
        var at = WindowStart.AddMinutes(-30);
        var normal = Report(Phase.Normal, WindowStart, WindowEnd);
        Assert.Equal(WindowStart, Controls(Settings, normal, now: at).NextDowntimeStart(at));
        Assert.Null(Controls(Pending(Settings), normal, now: at).NextDowntimeStart(at));
        // A lock-down in progress keeps its own: the Mac reports the lock-down now, not the downtime after it.
        var locked = Settings with { LockFrom = at, LockUntil = at.AddMinutes(20), LockJoinsAt = WindowStart };
        Assert.Equal(WindowStart, Controls(locked, Report(Phase.Locked, at, at.AddMinutes(20)), now: at.AddMinutes(5)).NextDowntimeStart(at.AddMinutes(5)));
    }

    [Fact]
    public void ALockDownRunsIntoTheNextDowntimeOnlyIfItLastsUntilThen()
    {
        var at = WindowStart.AddMinutes(-60);
        var shortOne = Settings with { LockFrom = at, LockUntil = at.AddMinutes(30), LockJoinsAt = WindowStart };
        Assert.False(shortOne.LockJoinsLater(at.AddMinutes(1)));
        Assert.True((shortOne with { LockUntil = WindowStart.AddMinutes(30) }).LockJoinsLater(at.AddMinutes(1)));
    }

    [Fact]
    public void ALockDownOnItsOwnCanBeChangedButNotOneThatRunsIntoDowntime()
    {
        var alone = Settings with { LockFrom = Now, LockUntil = Now.AddMinutes(30) };
        var reportAlone = Report(Phase.Locked, Now, Now.AddMinutes(30));
        Assert.True(Controls(alone, reportAlone, now: Now.AddMinutes(1)).CanChangeLockDown);
        Assert.False(Controls(Pending(alone), reportAlone, now: Now.AddMinutes(1)).CanChangeLockDown); // awaiting the Mac

        var at = WindowStart.AddMinutes(-30);
        var joined = Settings with { LockFrom = at, LockUntil = at.AddHours(1), LockJoinsAt = WindowStart };
        Assert.False(Controls(joined, Report(Phase.Locked, at, WindowEnd), now: at.AddMinutes(10)).CanChangeLockDown);
    }

    [Fact]
    public void WhatsInEffectIsReadOnce()
    {
        Assert.IsType<Override.None>(Controls(Settings, Locked).Current);
        Assert.IsType<Override.LockDown>(Controls(Settings with { LockFrom = Now, LockUntil = Now.AddHours(1) }, Locked).Current);
        Assert.IsType<Override.Unlock>(Controls(Settings with { ReleasedAt = Now.AddMinutes(-1), ReleasedUntil = WindowEnd }, Locked).Current);
        Assert.IsType<Override.Skip>(Controls(Settings with { ReleasedAt = Now.AddMinutes(5), ReleasedUntil = WindowEnd }, Locked).Current);
        // A lock-down an unlock came after is over: it's the unlock that's in effect.
        var cut = Settings with { LockFrom = Now.AddMinutes(-30), LockUntil = Now.AddMinutes(30), ReleasedAt = Now.AddMinutes(-5), ReleasedUntil = WindowEnd };
        Assert.IsType<Override.Unlock>(Controls(cut, Locked).Current);
    }
}
