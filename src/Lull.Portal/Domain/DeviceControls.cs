using Lull.Portal.Api;

namespace Lull.Portal.Domain;

/// <summary>
/// The device controls on offer for one Mac right now (Unlock now, Skip next downtime, Lock down
/// now, Reset overrides), and why one isn't when there's a reason to show. From its settings, its
/// last check-in and the school's rules; pure (takes "now"), so it's unit-tested. A Mac's page and
/// its home-page card show these, and every control's handler refuses what isn't offered.
/// </summary>
public sealed class DeviceControls
{
    /// <summary>The lock-down lengths offered as buttons, before the rules leave out those that wouldn't be accepted.</summary>
    public static readonly (string Label, int Minutes)[] LockLengths = [("30 minutes", 30), ("1 hour", 60), ("2 hours", 120)];

    DeviceSettings settings = null!;
    CheckIn? report;
    DeviceStatusView status = null!;

    /// <summary>The lock-down, unlock or skip in effect, which Reset overrides ends (and is labelled for).</summary>
    public Override Current { get; private init; } = new Override.None();

    /// <summary>
    /// The Mac hasn't applied the last change, or hasn't checked in since its phase was due to change,
    /// so what it last reported may no longer be true: only Reset overrides is offered until it checks in.
    /// </summary>
    public bool AwaitingCheckIn { get; private init; }

    /// <summary>When the lock-down in progress ends (with any scheduled downtime it runs into), or null.</summary>
    public DateTimeOffset? LockedDownUntil { get; private init; }
    /// <summary>The lock-down runs into scheduled downtime, so it's handled as that downtime: Unlock now, not Reset overrides.</summary>
    public bool LockDownJoinsSchedule { get; private init; }
    /// <summary>When that scheduled downtime starts, while it hasn't: Unlock now then ends just the lock-down.</summary>
    public DateTimeOffset? ScheduleStartsAt { get; private init; }

    /// <summary>
    /// The Mac reports scheduled downtime that no lock-down or release has taken over, or a lock-down
    /// runs into scheduled downtime (lulld merges them). Lock down now isn't offered then.
    /// </summary>
    public bool InScheduledDowntime { get; private init; }
    /// <summary>Unlock now: scheduled downtime is in progress and may be released. A lock-down on its own ends with Reset overrides.</summary>
    public bool CanRelease { get; private init; }
    /// <summary>Why scheduled downtime in progress can't be released now (school hours), or null.</summary>
    public string? ReleaseNotAllowed { get; private init; }

    /// <summary>Skip next downtime: the window the Mac reports as starting soon.</summary>
    public (DateTimeOffset Start, DateTimeOffset End)? NextWindow { get; private init; }
    /// <summary>Why the downtime starting soon can't be skipped (it starts in school hours), or null.</summary>
    public string? SkipNotAllowed { get; private init; }
    /// <summary>The start of the downtime a skip will cut, while it's still to come.</summary>
    public DateTimeOffset? SkippingAt { get; private init; }
    /// <summary>Until when a release (or a skip under way) holds.</summary>
    public DateTimeOffset? ReleasedUntil { get; private init; }

    /// <summary>
    /// Lock down now: not awaiting a check-in, no lock-down or scheduled downtime in progress, and no
    /// release or skip (a lock-down would cancel it and bring that downtime back).
    /// </summary>
    public bool CanLock { get; private init; }
    /// <summary>
    /// "Change to…" for the lock-down in progress: a new length from now, with the same choices and
    /// rules as Lock down now. Only for a lock-down on its own (one that runs into scheduled downtime
    /// is handled as that downtime), not awaiting a check-in, and that may be ended now.
    /// </summary>
    public bool CanChangeLockDown { get; private init; }
    /// <summary>The lengths of <see cref="LockLengths"/> the rules would accept now.</summary>
    public IReadOnlyList<(string Label, int Minutes)> LockChoices { get; private init; } = [];
    /// <summary>Why none of them would (school hours, turned off), or null.</summary>
    public string? LockNotAllowed { get; private init; }

    /// <summary>Reset overrides: a lock-down, release or skip to clear (one that runs into scheduled downtime only while awaiting a check-in).</summary>
    public bool HasOverride { get; private init; }
    /// <summary>Why a lock-down can't be ended now: ending one is a release, so it follows the release rule. Null if it can.</summary>
    public string? ResetNotAllowed { get; private init; }
    /// <summary>The home-page card's Reset overrides: for a lock-down, and only when it may be ended now.</summary>
    public bool CanEndLockDown { get; private init; }

    public static DeviceControls For(
        DeviceSettings s, CheckIn? report, DeviceStatusView status, BoundsPolicy policy, DateTimeOffset now, bool isAdmin)
    {
        var zone = DeviceStatusView.ZoneOf(report);
        var current = s.OverrideAt(now);
        var awaiting = status.AwaitingCheckIn;
        var joins = DeviceStatusView.LockDownJoinsSchedule(s, report, now);
        // Scheduled downtime the Mac reports, which no lock-down or unlock has taken over, or a
        // lock-down that runs into scheduled downtime (lulld merges them).
        var scheduled = joins || report?.Phase == Phase.Locked && current is not (Override.LockDown or Override.Unlock);
        var releaseRule = SettingsRules.CheckRelease(policy, now, zone, isAdmin).FirstOrDefault();

        // Offer only the lengths that would be accepted (e.g. none that run into school hours).
        var choices = LockLengths.Where(c => SettingsRules.CheckLockNow(policy, now, now.AddMinutes(c.Minutes), zone, isAdmin).Count == 0).ToArray();
        var lockNotAllowed = choices.Length == 0
            ? SettingsRules.CheckLockNow(policy, now, now.AddMinutes(LockLengths[0].Minutes), zone, isAdmin).FirstOrDefault()
            : null;

        // Skipping sets releasedAt to the window's start, so only while the Mac reports the window as
        // starting soon, from the settings shown here, and with nothing else in effect that it would replace.
        (DateTimeOffset, DateTimeOffset)? next = null;
        string? skipNotAllowed = null;
        if (current is Override.None && status.Sync == SyncState.Applied
            && report is { Phase: Phase.Warning, WindowStart: { } start, WindowEnd: { } end } && start > now)
        {
            skipNotAllowed = SettingsRules.CheckSkip(policy, start, zone, isAdmin).FirstOrDefault();
            if (skipNotAllowed is null) next = (start, end);
        }

        var lockedDownUntil = DeviceStatusView.LockDownEnd(s, report, now);
        // A lock-down that runs into scheduled downtime ends with Unlock now, like that downtime,
        // except while awaiting a check-in: Reset overrides is then the only control.
        var resettable = awaiting || !joins;
        // Ending a lock-down is a release, so it follows the release rule.
        var resetNotAllowed = current is Override.LockDown && resettable ? releaseRule : null;
        return new()
        {
            settings = s,
            report = report,
            status = status,
            Current = current,
            AwaitingCheckIn = awaiting,
            LockedDownUntil = lockedDownUntil,
            LockDownJoinsSchedule = joins,
            ScheduleStartsAt = joins && s.LockJoinsLater(now) ? s.LockJoinsAt : null,
            InScheduledDowntime = scheduled,
            CanRelease = scheduled && releaseRule is null && !awaiting,
            ReleaseNotAllowed = scheduled && !awaiting ? releaseRule : null,
            NextWindow = next,
            SkipNotAllowed = skipNotAllowed,
            SkippingAt = current is Override.Skip skip ? skip.Start : null,
            ReleasedUntil = current is Override.Unlock unlock ? unlock.Until : null,
            CanLock = !awaiting && current is Override.None && !scheduled,
            CanChangeLockDown = !awaiting && current is Override.LockDown && !joins && resetNotAllowed is null,
            LockChoices = choices,
            LockNotAllowed = lockNotAllowed,
            HasOverride = current is not Override.None && resettable,
            ResetNotAllowed = resetNotAllowed,
            CanEndLockDown = current is Override.LockDown && resettable && resetNotAllowed is null,
        };
    }

    /// <summary>
    /// When the next scheduled downtime starts, for a lock-down to keep (<see cref="DeviceSettings.LockJoinsAt"/>):
    /// once the Mac applies a lock-down that runs into it, it reports the two as one window, and Unlock
    /// now before this time ends just the lock-down. A new lock-down takes it from the Mac's report (only
    /// from settings it has applied); a changed one keeps its own, since the Mac then reports the lock-down.
    /// </summary>
    public DateTimeOffset? NextDowntimeStart(DateTimeOffset now) =>
        Current is Override.LockDown lockDown ? lockDown.JoinsAt
        : status.Sync == SyncState.Applied && report is { Phase: Phase.Normal or Phase.Warning, WindowStart: { } start } && start > now
            ? start
            : null;

    /// <summary>When the downtime Unlock now ends would otherwise have ended (to know when the release stops holding).</summary>
    public DateTimeOffset? ReleaseWindowEnd(DateTimeOffset now) => LockDownJoinsSchedule
        ? DeviceStatusView.LockDownEnd(settings, report, now)
        : report is { Phase: Phase.Locked } r ? r.WindowEnd : null;

}
