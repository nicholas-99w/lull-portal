using Lull.Portal.Api;
using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class DeviceStatusViewTests
{
    static readonly CheckIn Locked = Fixtures.Read<CheckIn>("checkin.json"); // Thu 15 Oct, 11 pm in Sydney
    static readonly DeviceSettings Settings = new(true, new(new(21, 30), new(6, 30)), new(new(23, 0), new(8, 0)), 1_760_000_000_000);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:03:00Z");

    [Fact]
    public void LockedShowsUntilWhenInTheMacsTimeZone()
    {
        var v = DeviceStatusView.From(Settings, Locked, Now.AddMinutes(-3), Now);
        Assert.Equal("In downtime until 6:30 am tomorrow", v.Headline);
        Assert.Equal(SyncState.Applied, v.Sync);
        Assert.Equal("3 minutes ago", v.LastCheckIn);
        Assert.False(v.Stale);
    }

    [Fact]
    public void OfflineAfterTwentyMinutes()
    {
        Assert.False(DeviceStatusView.From(Settings, Locked, Now.AddMinutes(-20), Now).Stale);
        Assert.True(DeviceStatusView.From(Settings, Locked, Now.AddMinutes(-21), Now).Stale);
    }

    [Fact]
    public void ACheckInIsOverdueTenMinutesAfterThePhaseWasDueToChange()
    {
        var unlocks = Locked.NextTransition!.Value; // 6:30 am Fri in Sydney, the end of the window
        var onTime = DeviceStatusView.From(Settings, Locked, Now, unlocks.AddMinutes(10));
        Assert.False(onTime.CheckInOverdue);
        Assert.False(onTime.AwaitingCheckIn);
        Assert.Equal("In downtime", onTime.State);

        var late = DeviceStatusView.From(Settings, Locked, Now, unlocks.AddMinutes(11));
        Assert.True(late.CheckInOverdue);
        Assert.True(late.AwaitingCheckIn);
        Assert.False(late.ChangePending);
        Assert.Equal("Waiting for check-in", late.State);
        Assert.Equal("warn", late.Tone);
        Assert.Equal("Due to unlock at 6:30 am today. Not heard from since.", late.NextChange);

        var warning = Locked with { Phase = "warning", WindowStart = unlocks, NextTransition = unlocks };
        Assert.Equal("Due to lock at 6:30 am today. Not heard from since.", DeviceStatusView.From(Settings, warning, Now, unlocks.AddHours(1)).NextChange);
        var normal = Locked with { Phase = "normal", NextTransition = unlocks };
        Assert.Equal("Due to check in at 6:30 am today. Not heard from since.", DeviceStatusView.From(Settings, normal, Now, unlocks.AddHours(1)).NextChange);
        Assert.False(DeviceStatusView.From(Settings, Locked with { NextTransition = null }, Now, unlocks.AddDays(1)).CheckInOverdue);
    }

    [Fact]
    public void ALockDownSavedHereStillShowsWhileACheckInIsOverdue()
    {
        var at = Locked.NextTransition!.Value.AddHours(1);
        var v = DeviceStatusView.From(Settings with { LockFrom = at, LockUntil = at.AddHours(1) }, Locked, Now, at);
        Assert.Equal("Locked down", v.State);
        Assert.True(v.AwaitingCheckIn);
    }

    [Fact]
    public void NewerSettingsArePendingAndOldCheckInsAreStale()
    {
        var v = DeviceStatusView.From(Settings with { Revision = Settings.Revision + 1 }, Locked, Now.AddHours(-2), Now);
        Assert.Equal(SyncState.Pending, v.Sync);
        Assert.True(v.Stale);
        Assert.Equal("2 hours ago", v.LastCheckIn);
    }

    [Fact]
    public void APendingChangeIsShownInsteadOfWhatTheMacLastReported()
    {
        var pending = Settings with { Revision = Settings.Revision + 1 };
        var v = DeviceStatusView.From(pending, Locked, Now, Now);
        Assert.Equal(("Change pending", "warn", "Applies within a few minutes"), (v.State, v.Tone, v.NextChange));
        var lockedDown = pending with { LockFrom = Now, LockUntil = Now.AddHours(1) };
        Assert.Equal("Locked down", DeviceStatusView.From(lockedDown, Locked, Now, Now).State); // known as soon as it's saved
    }

    [Fact]
    public void OptedOutAndNeverCheckedIn()
    {
        var off = Locked with { Phase = "disabled", Enabled = false };
        Assert.Equal("Opted out in the portal.", DeviceStatusView.From(Settings with { Enabled = false }, off, Now, Now).Detail);
        Assert.Equal(SyncState.WaitingForFirstCheckIn, DeviceStatusView.From(Settings, null, null, Now).Sync);
    }

    [Fact]
    public void RefusedSettingsAreShown()
    {
        var v = DeviceStatusView.From(Settings, Locked with { SettingsError = "schoolNights.start: bad" }, Now, Now);
        Assert.Contains("refused", v.Detail);
    }

    [Fact]
    public void CardShowsStateAndNextChange()
    {
        var locked = DeviceStatusView.From(Settings, Locked, Now, Now);
        Assert.Equal(("In downtime", "rest", "Unlocks 6:30 am tomorrow"), (locked.State, locked.Tone, locked.NextChange));

        var normal = DeviceStatusView.From(Settings, Locked with { Phase = "normal", WindowStart = Now.AddHours(10) }, Now, Now);
        Assert.Equal(("Available", "ok", "Downtime starts 9:03 am tomorrow"), (normal.State, normal.Tone, normal.NextChange));

        var none = DeviceStatusView.From(Settings, Locked with { Phase = "normal", WindowStart = null }, Now, Now);
        Assert.Null(none.NextChange);
    }

    [Fact]
    public void AMacNotHeardFromIsntCalledAvailable()
    {
        var normal = Locked with { Phase = "normal", WindowStart = Now.AddHours(10) };
        var quiet = DeviceStatusView.From(Settings, normal, Now.AddMinutes(-21), Now);
        Assert.Equal(("Not heard from", DeviceStatusView.QuietTone), (quiet.State, quiet.Tone));
        Assert.Equal("Last seen 21 minutes ago. It may be off, asleep or offline.", quiet.NextChange);
        Assert.Equal("Available", DeviceStatusView.From(Settings, normal, Now.AddMinutes(-20), Now).State);

        // Downtime holds whether or not the Mac is online, so that stays what it says.
        Assert.Equal("In downtime", DeviceStatusView.From(Settings, Locked, Now.AddMinutes(-21), Now).State);
    }

    [Fact]
    public void WhatNeedsAttentionRanksFirst()
    {
        var normal = Locked with { Phase = "normal", WindowStart = Now.AddHours(10) };
        Assert.Equal(2, DeviceStatusView.From(Settings, normal, Now, Now).AttentionRank);
        Assert.Equal(1, DeviceStatusView.From(Settings, normal, Now.AddHours(-3), Now).AttentionRank);
        Assert.Equal(1, DeviceStatusView.From(Settings, null, null, Now).AttentionRank);
        Assert.Equal(0, DeviceStatusView.From(Settings, normal with { SettingsError = "no" }, Now, Now).AttentionRank);
    }

    [Fact]
    public void PortalLockDownShowsBeforeTheMacTakesIt()
    {
        var lockedDown = Settings with { LockFrom = Now, LockUntil = Now.AddHours(1) };
        var v = DeviceStatusView.From(lockedDown, Locked with { Phase = "normal", WindowStart = null, WindowEnd = null }, Now, Now);
        Assert.Equal(("Locked down", "Unlocks 12:03 am tomorrow"), (v.State, v.NextChange));
        Assert.Equal("Lock-down pending", DeviceStatusView.From(lockedDown, null, null, Now).State);
    }

    [Fact]
    public void ALockDownIsPendingUntilTheMacHasIt()
    {
        var lockedDown = Settings with { LockFrom = Now, LockUntil = Now.AddHours(1), Revision = Settings.Revision + 1 };
        var normal = Locked with { Phase = "normal", WindowStart = null, WindowEnd = null };
        var pending = DeviceStatusView.From(lockedDown, normal, Now, Now);
        Assert.Equal(("Lock-down pending", "warn", "Locks within a few minutes, until 12:03 am tomorrow"), (pending.State, pending.Tone, pending.NextChange));
        Assert.True(pending.AwaitingCheckIn);

        // The Mac has it: locked down.
        Assert.Equal("Locked down", DeviceStatusView.From(lockedDown, normal with { SettingsRevision = lockedDown.Revision }, Now, Now).State);
        // Another change pending while the Mac already says it's locked: still locked down.
        Assert.Equal("Locked down", DeviceStatusView.From(lockedDown, Locked, Now, Now).State);
    }

    [Fact]
    public void ALockDownThatReachesTheNextWindowLastsUntilItEnds()
    {
        var next = Locked with { Phase = "normal", WindowStart = Now.AddHours(1), WindowEnd = Now.AddHours(10) };
        var reaching = Settings with { LockFrom = Now, LockUntil = Now.AddHours(1) }; // touching windows merge too
        Assert.Equal(Now.AddHours(10), DeviceStatusView.LockDownEnd(reaching, next, Now));
        Assert.True(DeviceStatusView.LockDownJoinsSchedule(reaching, next, Now));
        Assert.Equal("Unlocks 9:03 am tomorrow", DeviceStatusView.From(reaching, next, Now, Now).NextChange);

        var short_ = Settings with { LockFrom = Now, LockUntil = Now.AddMinutes(30) };
        Assert.Equal(Now.AddMinutes(30), DeviceStatusView.LockDownEnd(short_, next, Now));
        Assert.False(DeviceStatusView.LockDownJoinsSchedule(short_, next, Now));

        var released = reaching with { ReleasedAt = Now.AddMinutes(5) };
        Assert.False(released.IsLockedDown(Now.AddMinutes(10)));
        Assert.Null(DeviceStatusView.LockDownEnd(released, next, Now.AddMinutes(10)));
    }

    static readonly SchoolHours School = BoundsPolicy.Default.SchoolHours;

    [Fact]
    public void CardScheduleSaysWhoseTimesApply()
    {
        ScheduleSummary[] profile = [new("Nights", ["Mon", "Tue", "Wed", "Thu", "Fri"], "20:00", "07:00")];
        var mixed = Settings with { Weekend = null, SchoolNightsOff = false };
        Assert.Equal("Sun–Thu 9:30 pm – 6:30 am", SchoolSchedule.Current(WindowKind.SchoolNights, mixed, profile, true, School));
        Assert.Equal("Fri 8:00 pm – 7:00 am", SchoolSchedule.Current(WindowKind.Weekend, mixed, profile, true, School));
        Assert.Equal("no downtime", SchoolSchedule.Current(WindowKind.Weekend, mixed with { WeekendOff = true }, profile, true, School));
        Assert.Equal("times shown once the Mac is online", SchoolSchedule.Current(WindowKind.Weekend, mixed, null, false, School));

        // An advanced schedule: its windows starting on these days, outside school hours.
        var week = WeekGrid.FromCells(Enumerable.Range(0, 5).SelectMany(d => Enumerable.Range(WeekGrid.Cell(d, 15), 3)))!;
        Assert.Equal("Mon–Thu 3:30 pm – 6:00 pm", SchoolSchedule.Current(WindowKind.SchoolNights, mixed with { Week = week }, profile, true, School));
        Assert.Equal("Fri 3:30 pm – 6:00 pm", SchoolSchedule.Current(WindowKind.Weekend, mixed with { Week = week }, null, false, School));
    }

    [Fact]
    public void ActiveScheduleFollowsTheEveningOnTheMacsClock()
    {
        var tz = DeviceStatusView.FindZone("Australia/Sydney");
        DateTimeOffset At(string local) => new(DateTime.Parse(local), TimeSpan.FromHours(11)); // AEDT
        WindowKind Kind(string local, CheckIn? r = null) => SchoolSchedule.ActiveKind(r, At(local), tz);

        Assert.Equal(WindowKind.SchoolNights, Kind("2026-10-15T23:00")); // Thu night
        Assert.Equal(WindowKind.SchoolNights, Kind("2026-10-16T09:00")); // Fri morning: still Thursday's evening
        Assert.Equal(WindowKind.Weekend, Kind("2026-10-16T15:00"));      // Fri afternoon
        Assert.Equal(WindowKind.Weekend, Kind("2026-10-18T09:00"));      // Sun morning
        Assert.Equal(WindowKind.SchoolNights, Kind("2026-10-18T15:00")); // Sun afternoon
        // A window in progress counts by the day it started, even past noon.
        var lateSat = Locked with { Phase = "locked", WindowStart = At("2026-10-17T23:30") };
        Assert.Equal(WindowKind.Weekend, Kind("2026-10-18T13:00", lateSat));
    }
}
