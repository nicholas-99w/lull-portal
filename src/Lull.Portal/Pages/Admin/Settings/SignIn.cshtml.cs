using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Settings;

/// <summary>
/// How people sign in, and how long the audit log is kept: the .env's at first, then the owner's.
/// Other admins can see them but not change them, since they decide who can get in.
/// </summary>
public sealed class SignInModel(PortalSettings settings, PortalStore store, UserStore users) : PageModel
{
    public AccountSettings Settings { get; private set; } = null!;
    /// <summary>Saved here (false: still the .env's).</summary>
    public bool Saved { get; private set; }
    public bool IsOwner { get; private set; }
    public bool EntraEnabled => settings.EntraEnabled;
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }

    async Task LoadAsync(CancellationToken ct)
    {
        (Settings, Saved) = await settings.GetConfiguredAsync(ct);
        IsOwner = await users.GetAsync(AccountClaims.Id(User), ct) is { IsOwner: true };
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(bool localSignIn, bool linkLocalAccounts, string? auditRetentionDays, CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!IsOwner) return Forbid();
        var days = int.TryParse(auditRetentionDays, out var d) ? d : -1;
        // Without Microsoft sign-in the two toggles are disabled, so the form doesn't send them: kept as they were.
        var next = EntraEnabled
            ? new AccountSettings(localSignIn, linkLocalAccounts, days)
            : Settings with { AuditRetentionDays = days };
        Errors.AddRange(next.Check(EntraEnabled));
        if (Errors.Count > 0)
        {
            Settings = next;
            return Page();
        }
        await store.SaveAccountSettingsAsync(next, Settings, DeviceAccess.Actor(User), ct);
        Notice = "Saved. These now apply instead of the values in the server's .env.";
        return RedirectToPage();
    }
}
