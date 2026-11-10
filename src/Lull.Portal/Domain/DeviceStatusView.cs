using System.Globalization;
using Lull.Portal.Api;

namespace Lull.Portal.Domain;

public enum SyncState
{
    /// <summary>Registered but never checked in.</summary>
    WaitingForFirstCheckIn,
    /// <summary>The Mac reports the current revision.</summary>
    Applied,
    /// <summary>Saved here; the Mac hasn't reported it yet.</summary>
    Pending,
}

/// <summary>
/// Human-readable status of one Mac, from its last check-in. Pure (takes
/// "now"), so it is unit-tested. Times are shown in the Mac's configured
/// time zone, which is what its schedule uses.
/// </summary>
/// <param name="State">Short current status for the parent's card ("In downtime").</param>
/// <param name="Tone">Colour for <paramref name="State"/>: "ok" (usable), "rest" (locked, as intended), "warn" or "bad" (a problem), or <see cref="QuietTone"/>.</param>
/// <param name="NextChange">When the status next changes ("Unlocks 6:30 am tomorrow"), or null if nothing is due.</param>
/// <param name="Problem">The Mac refused the last settings, with its reason; else null.</param>
/// <param name="CheckInOverdue">The Mac's phase was due to change (<see cref="CheckInGrace"/> ago or more) and it hasn't checked in since.</param>
public sealed record DeviceStatusView(string Headline, string? Detail, SyncState Sync, string LastCheckIn, bool Stale, string TimeZone,
    string State, string Tone, string? NextChange, string? Problem, bool CheckInOverdue = false)
{
    /// <summary>No check-in for this long: probably off, asleep or offline.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(20);

    /// <summary>
    /// lulld checks in as soon as its phase changes, so a check-in this long after the change it
    /// reported as next is overdue (with room for the Mac's clock being a little off the portal's).
    /// </summary>
    public static readonly TimeSpan CheckInGrace = TimeSpan.FromMinutes(10);

    public const string FallbackTimeZone = "Australia/Sydney";

    /// <summary>The <paramref name="Tone"/> of a Mac not heard from lately, whose state can't be known: it may just be asleep, so not a warning.</summary>
    public const string QuietTone = "quiet";

    /// <summary>For a list of Macs, what needs a look first: lower comes first.</summary>
    public int AttentionRank => Problem is not null ? 0 : CheckInOverdue || Stale || Sync == SyncState.WaitingForFirstCheckIn ? 1 : 2;

    /// <summary>
    /// A change saved here that the Mac hasn't applied yet, so what it last reported may no longer
    /// be true. Settings the Mac refused never apply, so they don't count: it would never end.
    /// </summary>
    public bool ChangePending => Sync == SyncState.Pending && Problem is null;

    /// <summary>
    /// What the Mac last reported may no longer be true (a change pending, or a check-in overdue),
    /// so only Reset overrides is offered until it checks in.
    /// </summary>
    public bool AwaitingCheckIn => ChangePending || CheckInOverdue;

    public static DeviceStatusView From(DeviceSettings settings, CheckIn? report, DateTimeOffset? lastCheckInAt, DateTimeOffset now)
    {
        var tzId = report?.TimeZone ?? FallbackTimeZone;
        var tz = FindZone(tzId);
        string When(DateTimeOffset t) => DeviceStatusView.When(t, now, tz);
        // A portal lock-down the Mac hasn't confirmed yet (it takes it at its next check-in) is "pending",
        // not "Locked down": until it checks in, the Mac may well still be usable.
        string? lockedUntil = LockDownEnd(settings, report, now) is { } lockEnd ? When(lockEnd) : null;
        const string LockPending = "Lock-down pending";
        string LocksSoon() => $"Locks within a few minutes, until {lockedUntil}";

        if (report is null || lastCheckInAt is null)
            return new("Waiting for the Mac to check in", "It appears here a few minutes after Lull starts on it.",
                SyncState.WaitingForFirstCheckIn, "never", Stale: false, tzId,
                lockedUntil is null ? "Waiting for first check-in" : LockPending, "warn",
                lockedUntil is null ? null : LocksSoon(), Problem: null);

        (string headline, string? detail) = report.Phase switch
        {
            Phase.Locked => ($"In downtime until {Opt(report.WindowEnd, When)}", report.ScheduleName),
            Phase.Overridden => ("Unlocked by school IT", $"Downtime resumes at the next window ({report.ScheduleName})."),
            Phase.Warning => ($"Downtime starts at {Opt(report.WindowStart, When)}", report.ScheduleName),
            Phase.Normal => report.WindowStart is { } next
                ? ($"Not in downtime. Next: {When(next)}", report.ScheduleName)
                : ("Not in downtime", "No downtime scheduled."),
            Phase.Disabled when report.Enabled == false && !settings.Enabled => ("Downtime is off", "Opted out in the portal."),
            Phase.Disabled => ("Downtime is off", report.PhaseReason),
            _ => (report.Phase, report.PhaseReason),
        };
        string? problem = report.SettingsError is { } err ? $"The Mac refused the last settings: {err}" : null;
        if (problem is not null) detail = problem;

        var sync = report.SettingsRevision == settings.Revision ? SyncState.Applied : SyncState.Pending;
        var overdue = report.NextTransition is { } due && now > due + CheckInGrace;
        var age = now - lastCheckInAt.Value;
        var stale = age > StaleAfter;
        // Until the Mac applies the latest change, or checks in after its phase was due to change,
        // what it last reported may no longer be true.
        // Confirmed once the Mac has applied these settings, or says it's locked anyway (another change may be pending).
        var lockConfirmed = (sync == SyncState.Applied || problem is not null) || report.Phase == Phase.Locked;
        (string state, string tone, string? nextChange) = lockedUntil is not null
            ? lockConfirmed ? ("Locked down", "rest", $"Unlocks {lockedUntil}") : (LockPending, "warn", LocksSoon())
            : overdue
            ? ("Waiting for check-in", "warn", report.Phase switch
            {
                Phase.Locked => $"Due to unlock at {When(report.WindowEnd ?? report.NextTransition!.Value)}. Not heard from since.",
                Phase.Warning => $"Due to lock at {When(report.WindowStart ?? report.NextTransition!.Value)}. Not heard from since.",
                _ => $"Due to check in at {When(report.NextTransition!.Value)}. Not heard from since.",
            })
            : sync == SyncState.Pending
            ? ("Change pending", "warn", "Applies within a few minutes")
            // Downtime holds offline, and off is off, but nothing says a Mac is usable now if it hasn't been heard from.
            : stale && report.Phase is Phase.Normal or Phase.Warning or Phase.Overridden
            ? ("Not heard from", QuietTone, $"Last seen {Ago(age)}. It may be off, asleep or offline.")
            : report.Phase switch
            {
                Phase.Locked => ("In downtime", "rest", $"Unlocks {Opt(report.WindowEnd ?? report.NextTransition, When)}"),
                Phase.Warning => ("Downtime starting soon", "warn", $"Locks {Opt(report.WindowStart ?? report.NextTransition, When)}"),
                Phase.Overridden => ("Unlocked by IT", "warn", report.WindowStart is { } s ? $"Locks {When(s)}" : "Locks at the next downtime"),
                Phase.Normal => ("Available", "ok", report.WindowStart is { } s ? $"Downtime starts {When(s)}" : null),
                Phase.Disabled => ("Downtime is off", "warn", null),
                _ => (report.Phase, "warn", null),
            };

        return new(headline, detail, sync, Ago(age), stale, tzId, state, tone, nextChange, problem, overdue);
    }

    /// <summary>
    /// When the lock-down in progress ends, or null if there isn't one. lulld merges a lock-down
    /// with scheduled downtime it runs into or touches, so one that reaches the window the Mac
    /// last reported (in progress or next) lasts until that window ends.
    /// </summary>
    public static DateTimeOffset? LockDownEnd(DeviceSettings settings, CheckIn? report, DateTimeOffset now)
    {
        if (!settings.IsLockedDown(now)) return null;
        var until = settings.LockUntil!.Value;
        return report is { Phase: Phase.Locked or Phase.Warning or Phase.Normal, WindowStart: { } start, WindowEnd: { } end }
            && start <= until && end > until
            ? end
            : until;
    }

    /// <summary>A lock-down that runs into scheduled downtime: it behaves as that downtime (Release now ends both).</summary>
    public static bool LockDownJoinsSchedule(DeviceSettings settings, CheckIn? report, DateTimeOffset now) =>
        LockDownEnd(settings, report, now) > settings.LockUntil;

    /// <summary>"4:30 pm today", "6:30 am tomorrow" or "9:30 pm Sat 17 Oct", in <paramref name="tz"/>.</summary>
    public static string When(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(t, tz);
        var nowLocal = TimeZoneInfo.ConvertTime(now, tz);
        var time = local.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-AU")).ToLowerInvariant();
        if (local.Date == nowLocal.Date) return $"{time} today";
        if (local.Date == nowLocal.Date.AddDays(1)) return $"{time} tomorrow";
        return $"{time} {local.ToString("ddd d MMM", CultureInfo.GetCultureInfo("en-AU"))}";
    }

    static string Opt(DateTimeOffset? t, Func<DateTimeOffset, string> f) => t is { } v ? f(v) : "the end of the window";

    public static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "just now",
        < 2 => "1 minute ago",
        < 60 => $"{(int)age.TotalMinutes} minutes ago",
        < 120 => "1 hour ago",
        < 48 * 60 => $"{(int)age.TotalHours} hours ago",
        _ => $"{(int)age.TotalDays} days ago",
    };

    /// <summary>The Mac's configured time zone (from its last check-in), or the fallback.</summary>
    public static TimeZoneInfo ZoneOf(CheckIn? report) => FindZone(report?.TimeZone ?? FallbackTimeZone);

    public static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(FallbackTimeZone);
        }
    }
}
