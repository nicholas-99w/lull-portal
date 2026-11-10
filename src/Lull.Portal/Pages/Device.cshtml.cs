using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Lull.Portal.Pages;

/// <summary>One Mac: how it is now and what can be done about it. Its schedule has its own page (<see cref="DeviceScheduleModel"/>).</summary>
public sealed class DeviceModel(PortalStore store, UserStore users, TimeProvider clock) : DevicePageModel(store, users, clock)
{
    /// <summary>Problems with IT's tools on this page: the primary user, de-registering.</summary>
    public List<string> Errors { get; } = [];
    public List<string> ControlErrors { get; } = [];

    // What the page shows, from Controls (see there for what each means), with times as "4:30 pm today".
    public bool AwaitingCheckIn => Controls.AwaitingCheckIn;
    public string? LockedDownUntil => At(Controls.LockedDownUntil);
    public string? ScheduleStartsAt => At(Controls.ScheduleStartsAt);
    public bool InScheduledDowntime => Controls.InScheduledDowntime;
    public bool CanRelease => Controls.CanRelease;
    public string? ReleaseNotAllowed => Controls.ReleaseNotAllowed;
    public (DateTimeOffset Start, DateTimeOffset End)? NextWindow => Controls.NextWindow;
    public string? NextWindowAt => At(Controls.NextWindow?.Start);
    public string? SkipNotAllowed => Controls.SkipNotAllowed;
    public string? SkippingAt => At(Controls.SkippingAt);
    public string? ReleasedUntil => At(Controls.ReleasedUntil);
    public bool CanLock => Controls.CanLock;
    public IReadOnlyList<(string Label, int Minutes)> LockChoices => Controls.LockChoices;
    public string? LockNotAllowed => Controls.LockNotAllowed;

    /// <summary>IT has turned Lock down now off for parents: they aren't offered it or told about it, as if it weren't a feature.</summary>
    bool LockTurnedOff => !IsAdmin && !Policy.LockNow.Enabled;
    public bool OfferLock => CanLock && !LockTurnedOff;
    public bool OfferChangeLockDown => CanChangeLockDown && !LockTurnedOff;
    /// <summary>An unlock or skip stands between this person and Lock down now, which they'd otherwise have.</summary>
    public bool LockAfterCancelling => !AwaitingCheckIn && !LockTurnedOff && Controls.Current is Override.Unlock or Override.Skip;

    /// <summary>The controls card would otherwise be empty: nothing to do, and nothing to say about why.</summary>
    public bool NoControls => !AwaitingCheckIn && ReleasedUntil is null && SkippingAt is null
        && !CanRelease && ReleaseNotAllowed is null && NextWindow is null && SkipNotAllowed is null
        && LockedDownUntil is null && !OfferLock && !LockAfterCancelling
        && ResetNotAllowed is null && Reset is null;
    public bool HasOverride => Controls.HasOverride;
    public bool CanChangeLockDown => Controls.CanChangeLockDown;
    public string? ResetNotAllowed => Controls.ResetNotAllowed;

    /// <summary>
    /// The Reset overrides button, named for what it ends (null when there's nothing to end): parents
    /// think of ending a lock-down, cancelling an unlock or a skip, not of "overrides".
    /// </summary>
    public (string Button, string Hint)? Reset => !Controls.HasOverride ? null : Controls.Current switch
    {
        Override.LockDown => ("End lock-down", "Unlocks the Mac now. The usual schedule carries on."),
        Override.Unlock => ("Cancel unlock", "The downtime in progress applies again"),
        Override.Skip skip => ("Cancel skip", $"The downtime at {At(skip.Start)} goes ahead"),
        _ => null,
    };

    const string FollowingSchool = "Following the school schedule", Custom = "Custom", NoDowntime = "No downtime";

    /// <summary>The school weeks at a glance: the school's own schedule, none at all (opted out, or an empty grid), or anything else.</summary>
    public string WeeksSummary
    {
        get
        {
            var s = Device.Settings;
            if (s.Week is { } week) return week.Windows(Policy.SchoolHours).Count == 0 ? NoDowntime : Custom;
            return (s.ModeFor(WindowKind.SchoolNights), s.ModeFor(WindowKind.Weekend)) switch
            {
                (ScheduleMode.School, ScheduleMode.School) => FollowingSchool,
                (ScheduleMode.OptOut, ScheduleMode.OptOut) => NoDowntime,
                _ => Custom,
            };
        }
    }

    /// <summary>Likewise for the school holidays, where "Same as during term" is whatever the school weeks are.</summary>
    public string HolidaySummary => Device.Settings.HolidayMode switch
    {
        ScheduleMode.School => WeeksSummary,
        ScheduleMode.OptOut => NoDowntime,
        ScheduleMode.Advanced when Device.Settings.HolidayWeek?.Windows(null).Count == 0 => NoDowntime,
        _ => Custom,
    };

    /// <summary>
    /// Refuses a control the page doesn't offer now: awaiting a check-in, or the page was out of
    /// date (another tab, or the home page) and the Mac's state has moved on.
    /// </summary>
    bool Refused(bool offered)
    {
        if (offered) return false;
        ControlErrors.Add(AwaitingCheckIn
            ? "Wait for the Mac to check in, then try again."
            : "That's no longer available. The controls below are up to date.");
        return true;
    }

    public async Task<IActionResult> OnPostLockAsync(Guid id, long? revision, int? minutes, string? until, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Stale(revision, ControlErrors)) return Page();
        var now = Clock.GetUtcNow();
        DateTimeOffset end;
        if (minutes is > 0) end = now.AddMinutes(minutes.Value);
        else if (Window.TryParseTime(until, out var t)) end = SettingsRules.NextOccurrence(t, now, Zone);
        else
        {
            ControlErrors.Add("Choose how long to lock down for.");
            return Page();
        }
        ControlErrors.AddRange(SettingsRules.CheckLockNow(Policy, now, end, Zone, IsAdmin));
        if (ControlErrors.Count > 0 || Refused(CanLock || CanChangeLockDown)) return Page();
        var changing = CanChangeLockDown;
        if (await Store.LockNowAsync(id, Device.Settings.Revision, end, Controls.NextDowntimeStart(now), DeviceAccess.Actor(User), ct) is null)
            return await ChangedMeanwhileAsync(id, ControlErrors, ct);
        Notice = changing
            ? $"Lock-down changed: now until {DeviceStatusView.When(end, now, Zone)}."
            : $"Locking down until {DeviceStatusView.When(end, now, Zone)}. The Mac locks within a few minutes.";
        return RedirectToPage(new { id });
    }

    /// <summary>Ends the downtime in progress (scheduled or lock-down). Also posted from the parent's home page.</summary>
    public async Task<IActionResult> OnPostReleaseAsync(Guid id, long? revision, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Stale(revision, ControlErrors)) return Page();
        ControlErrors.AddRange(SettingsRules.CheckRelease(Policy, Clock.GetUtcNow(), Zone, IsAdmin));
        if (ControlErrors.Count > 0 || Refused(CanRelease)) return Page();
        if (ScheduleStartsAt is { } startsAt)
        {
            if (await Store.EndLockDownAsync(id, Device.Settings.Revision, DeviceAccess.Actor(User), ct) is null)
                return await ChangedMeanwhileAsync(id, ControlErrors, ct);
            Notice = $"Lock-down ended. Downtime still starts at {startsAt}.";
            return RedirectToPage(new { id });
        }
        if (await Store.ReleaseAsync(id, Device.Settings.Revision, Controls.ReleaseWindowEnd(Clock.GetUtcNow()), DeviceAccess.Actor(User), ct) is null)
            return await ChangedMeanwhileAsync(id, ControlErrors, ct);
        Notice = "Unlocking. The Mac unlocks within a few minutes.";
        return RedirectToPage(new { id });
    }

    /// <param name="start">The window the page offered to skip (Unix seconds), so a stale page can't skip a different one.</param>
    public async Task<IActionResult> OnPostSkipAsync(Guid id, long? revision, long start, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Stale(revision, ControlErrors)) return Page();
        if (SkipNotAllowed is { } why) ControlErrors.Add(why);
        else if (AwaitingCheckIn) Refused(false);
        else if (NextWindow is not { } w || w.Start.ToUnixTimeSeconds() != start)
            ControlErrors.Add("There's no downtime starting soon to skip.");
        if (ControlErrors.Count > 0) return Page();
        var (from, to) = NextWindow!.Value;
        if (await Store.SkipAsync(id, Device.Settings.Revision, from, to, DeviceAccess.Actor(User), ct) is null)
            return await ChangedMeanwhileAsync(id, ControlErrors, ct);
        Notice = $"Skipping the downtime at {DeviceStatusView.When(from, Clock.GetUtcNow(), Zone)}.";
        return RedirectToPage(new { id });
    }

    /// <summary>Clears any lock-down, release or skip. Ending a lock-down is a release, so it follows the release rule.</summary>
    public async Task<IActionResult> OnPostResetOverridesAsync(Guid id, long? revision, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Stale(revision, ControlErrors)) return Page();
        if (ResetNotAllowed is { } why)
        {
            ControlErrors.Add(why);
            return Page();
        }
        if (Refused(HasOverride)) return Page();
        if (await Store.ResetOverridesAsync(id, Device.Settings.Revision, DeviceAccess.Actor(User), ct) is null)
            return await ChangedMeanwhileAsync(id, ControlErrors, ct);
        Notice = Controls.Current switch
        {
            Override.LockDown => "Lock-down ended. The Mac unlocks within a few minutes, and the usual schedule carries on.",
            Override.Unlock => "Unlock cancelled. The usual schedule applies again within a few minutes.",
            Override.Skip skip => $"Skip cancelled. The downtime at {At(skip.Start)} goes ahead.",
            _ => "The usual schedule applies within a few minutes.",
        };
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) ? Page() : NotFound();

    /// <summary>
    /// The parts of this page that change by themselves (status and controls), for live.js to swap in
    /// while it's open. Nothing for a de-registered Mac, which stops it.
    /// </summary>
    public async Task<IActionResult> OnGetLiveAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        return Device.IsDeregistered ? new NoContentResult() : Partial("_DeviceLive", this);
    }

    public async Task<IActionResult> OnPostDeregisterAsync(Guid id, bool confirm, CancellationToken ct)
    {
        if (!IsAdmin || !await LoadAsync(id, ct)) return NotFound();
        if (Device.IsDeregistered) return RedirectToPage(new { id });
        if (!confirm)
        {
            Errors.Add("Tick the box to confirm you want to de-register this Mac.");
            return Page();
        }
        await Store.DeregisterAsync(id, DeviceAccess.Actor(User), ct);
        Notice = $"Mac {Device.SerialNumber} de-registered. It falls back to its local configuration at its next check-in.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResetAsync(Guid id, CancellationToken ct)
    {
        if (!IsAdmin || !await LoadAsync(id, ct)) return NotFound();
        await Store.ResetDeviceAsync(id, DeviceAccess.Actor(User), ct);
        Notice = "Device reset. It can register again once.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPrimaryUserAsync(Guid id, string? username, CancellationToken ct)
    {
        if (!IsAdmin || !await LoadAsync(id, ct)) return NotFound();
        var (names, invalid) = ChildList.Parse(username);
        if (invalid.Count > 0 || names.Count > 1)
        {
            Errors.Add("Enter one valid username, or leave it empty.");
            return Page();
        }
        var name = names.FirstOrDefault();
        await Store.SetPrimaryUserAsync(id, name, DeviceAccess.Actor(User), ct);
        Notice = name is null ? "Primary user cleared; it is pinned again at the next check-in." : $"Primary user is now {name}.";
        return RedirectToPage(new { id });
    }

}
