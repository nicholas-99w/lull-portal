using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages;

/// <summary>
/// What a Mac's two pages share (its controls, <see cref="DeviceModel"/>, and its schedule,
/// <see cref="DeviceScheduleModel"/>): who may open it, and refusing forms the Mac's settings have moved on from.
/// Parents may change only their children's Macs, never inside school hours; IT may change any Mac and isn't bound.
/// </summary>
public abstract class DevicePageModel(PortalStore store, UserStore users, TimeProvider clock) : PageModel
{
    protected readonly PortalStore Store = store;
    protected readonly TimeProvider Clock = clock;

    public DeviceRecord Device { get; private set; } = null!;
    public DeviceStatusView Status => Card.Status;
    public DeviceCard Card { get; private set; } = null!;
    public BoundsPolicy Policy { get; private set; } = BoundsPolicy.Default;
    public bool IsAdmin => DeviceAccess.IsAdmin(User);
    [TempData] public string? Notice { get; set; }

    /// <summary>What's on offer under the page's controls, and why not (<see cref="DeviceControls"/>).</summary>
    public DeviceControls Controls { get; private set; } = null!;
    /// <summary>When the page loaded the Mac: the "now" its controls and times are worked out for.</summary>
    protected DateTimeOffset LoadedAt { get; private set; }

    /// <summary>"4:30 pm today" in the Mac's time zone, or null.</summary>
    protected string? At(DateTimeOffset? t) => t is { } v ? DeviceStatusView.When(v, LoadedAt, Zone) : null;

    protected TimeZoneInfo Zone => DeviceStatusView.ZoneOf(Device.LastReport);

    /// <summary>The first Lull (lulld) version that applies the holiday setting.</summary>
    public static readonly Version HolidayVersion = new(0, 3, 0);

    /// <summary>The first Lull (lulld) version that applies an advanced schedule.</summary>
    public static readonly Version AdvancedVersion = new(0, 4, 0);

    protected virtual async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        // Not found and not allowed look the same, so parents can't probe for other devices.
        if (await DeviceAccess.LoadAsync(Store, users, User, id, ct) is not { } device) return false;
        Device = device;
        Policy = await Store.GetPolicyAsync(ct);
        var now = LoadedAt = Clock.GetUtcNow();
        Card = DeviceCard.For(device, Policy, now);
        Controls = DeviceControls.For(device.Settings, device.LastReport, Status, Policy, now, IsAdmin);
        return true;
    }

    const string ChangedMeanwhile =
        "This Mac's settings changed after the page loaded (in another tab, or by someone else). Here they are now: try again if you still want to.";

    /// <summary>
    /// Every form carries the settings revision it was shown with. A different one means the
    /// settings changed since (another tab, another parent, IT): the form is refused, and the page
    /// shows them as they are now.
    /// </summary>
    protected bool Stale(long? revision, List<string> errors)
    {
        if (revision == Device.Settings.Revision) return false;
        errors.Add(ChangedMeanwhile);
        return true;
    }

    /// <summary>The settings changed between loading them and saving (the store checks the revision too).</summary>
    protected async Task<IActionResult> ChangedMeanwhileAsync(Guid id, List<string> errors, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        errors.Add(ChangedMeanwhile);
        return Page();
    }
}
