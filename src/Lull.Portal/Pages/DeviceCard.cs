using Lull.Portal.Data;
using Lull.Portal.Domain;

namespace Lull.Portal.Pages;

/// <summary>One Mac in IT's device list.</summary>
public sealed record DeviceRow(DeviceRecord Device, DeviceStatusView Status)
{
    /// <summary>The status to scan a list for: a problem outranks what the Mac last said it was doing.</summary>
    public string State => Status.Problem is not null ? "Settings refused" : Status.State;
    public string Tone => Status.Problem is not null ? "bad" : Status.Tone;

    /// <summary>What's next, or the reason it was refused. A Mac not heard from has its last-seen in its own column.</summary>
    public string? Next => Device.IsDeregistered ? null
        : Device.LastReport?.SettingsError is { } error ? error
        : Status.Tone == DeviceStatusView.QuietTone ? "May be off, asleep or offline"
        : Status.NextChange;

    public string? Schedule => Device.IsDeregistered ? null
        : Device.Settings.Week is not null ? "Hour by hour"
        : (Device.Settings.ModeFor(WindowKind.SchoolNights), Device.Settings.ModeFor(WindowKind.Weekend)) switch
        {
            (ScheduleMode.School, ScheduleMode.School) => "School's",
            (ScheduleMode.OptOut, ScheduleMode.OptOut) => "No downtime",
            _ => "Custom",
        };
}

/// <summary>
/// One Mac at a glance: a parent's home-page card, and the "Right now" box on the Mac's page. Its
/// buttons follow <see cref="DeviceControls"/> as a parent would see them (school hours apply).
/// </summary>
/// <param name="ActiveKind">The schedule that applies now (school nights or weekend).</param>
/// <param name="SchoolHours">IT's, which an advanced schedule keeps out of.</param>
public sealed record DeviceCard(DeviceRecord Device, DeviceStatusView Status, WindowKind ActiveKind, DeviceControls Controls, SchoolHours SchoolHours)
{
    /// <summary>Unlock now (<see cref="DeviceControls.CanRelease"/>).</summary>
    public bool CanRelease => Controls.CanRelease;

    /// <summary>End lock-down (the store's Reset overrides), for a lock-down only (<see cref="DeviceControls.CanEndLockDown"/>): the card's other button.</summary>
    public bool CanReset => Controls.CanEndLockDown;

    public static DeviceCard For(DeviceRecord d, BoundsPolicy policy, DateTimeOffset now)
    {
        var status = DeviceStatusView.From(d.Settings, d.LastReport, d.LastCheckInAt, now);
        return new(d, status, SchoolSchedule.ActiveKind(d.LastReport, now, DeviceStatusView.ZoneOf(d.LastReport)),
            DeviceControls.For(d.Settings, d.LastReport, status, policy, now, isAdmin: false), policy.SchoolHours);
    }
}
