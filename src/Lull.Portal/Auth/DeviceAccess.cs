using System.Security.Claims;
using Lull.Portal.Data;
using Lull.Portal.Domain;

namespace Lull.Portal.Auth;

/// <summary>
/// The one place that decides who may see or change a device: IT may see any
/// device; a parent only devices whose pinned primary user is one of their
/// children. Pages call this rather than querying devices directly.
/// </summary>
public static class DeviceAccess
{
    public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    /// <summary>The device, or null if it doesn't exist or this user may not see it.</summary>
    public static async Task<DeviceRecord?> LoadAsync(
        PortalStore store, UserStore users, ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var device = await store.GetDeviceAsync(id, ct);
        if (device is null || IsAdmin(user)) return device;
        if (device.IsDeregistered) return null;
        if (!user.IsInRole(Roles.Parent)) return null;
        var children = await users.GetStudentsAsync(AccountClaims.Id(user), ct);
        return ChildList.Owns(children, device.PrimaryUsername) ? device : null;
    }
}
