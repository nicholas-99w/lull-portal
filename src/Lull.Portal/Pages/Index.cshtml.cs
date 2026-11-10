using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages;

/// <summary>A parent's view: the Macs whose primary user is one of their children.</summary>
public sealed class IndexModel(PortalStore store, UserStore users, TimeProvider clock) : PageModel
{
    public IReadOnlyList<string> Children { get; private set; } = [];
    public List<DeviceCard> Devices { get; } = [];
    public List<string> Missing { get; } = [];
    public DateTimeOffset Now { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (DeviceAccess.IsAdmin(User)) return Redirect("/Admin/Devices");
        if (!User.IsInRole(Roles.Parent)) return Redirect("/Denied");
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>The cards, for live.js to swap in while the page is open.</summary>
    public async Task<IActionResult> OnGetLiveAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Parent) || DeviceAccess.IsAdmin(User)) return new NoContentResult();
        await LoadAsync(ct);
        return Partial("_HomeCards", this);
    }

    async Task LoadAsync(CancellationToken ct)
    {
        Children = await users.GetStudentsAsync(AccountClaims.Id(User), ct);
        var now = Now = clock.GetUtcNow();
        var policy = await store.GetPolicyAsync(ct);
        // What needs a parent's attention first, otherwise in the store's order (by name).
        Devices.AddRange((await store.ListDevicesForChildrenAsync(Children, ct))
            .Select(d => DeviceCard.For(d, policy, now))
            .OrderBy(c => c.Status.AttentionRank));
        Missing.AddRange(Children.Where(c => Devices.All(d => d.Device.PrimaryUsername != c)));
    }
}
