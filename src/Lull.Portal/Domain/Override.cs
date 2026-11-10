namespace Lull.Portal.Domain;

/// <summary>
/// What a parent or IT has put in place on top of a Mac's schedule right now: nothing, a
/// lock-down, an unlock, or a skip. <see cref="DeviceSettings.OverrideAt"/> is the one place that
/// reads this from the stored fields, which encode it indirectly (a release is
/// <c>releasedAt</c> in the past, a skip <c>releasedAt</c> in the future, and a lock-down counts
/// as over once a release came after it). Ending any of them is the same action ("Reset overrides"
/// in the store, labelled for what it ends).
/// </summary>
public abstract record Override
{
    Override() { }

    /// <summary>The schedule applies as set.</summary>
    public sealed record None : Override;

    /// <summary>"Lock down now", from <paramref name="From"/> until <paramref name="Until"/>.</summary>
    /// <param name="JoinsAt">
    /// When the next scheduled downtime starts, as the Mac reported it when the lock-down was saved, if
    /// known: the lock-down runs into it if <paramref name="Until"/> is at or after it.
    /// </param>
    public sealed record LockDown(DateTimeOffset From, DateTimeOffset Until, DateTimeOffset? JoinsAt) : Override;

    /// <summary>"Unlock now" at <paramref name="At"/>: the downtime then in progress ends, until it would have ended (<paramref name="Until"/>).</summary>
    public sealed record Unlock(DateTimeOffset At, DateTimeOffset Until) : Override;

    /// <summary>"Skip next downtime": the window starting at <paramref name="Start"/> (until <paramref name="End"/>) won't lock.</summary>
    public sealed record Skip(DateTimeOffset Start, DateTimeOffset End) : Override;
}
