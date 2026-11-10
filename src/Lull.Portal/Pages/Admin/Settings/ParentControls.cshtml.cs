using System.Globalization;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Settings;

/// <summary>What parents may do beyond their child's downtime: for now, lock down now and its longest length.</summary>
public sealed class ParentControlsModel(PortalStore store) : PageModel
{
    public BoundsPolicy Policy { get; private set; } = BoundsPolicy.Default;
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Policy = await store.GetPolicyAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Policy = await store.GetPolicyAsync(ct);
        var lockNow = new LockNowPolicy(Request.Form["lockEnabled"] == "true", Minutes("lockMaxHours"));
        if (Errors.Count > 0) return Page();

        // School hours (and holidays, which the store keeps) are left as they are, so no Mac changes.
        var policy = Policy with { LockNow = lockNow, Version = BoundsPolicy.CurrentVersion };
        Errors.AddRange(SettingsRules.CheckPolicy(policy));
        if (Errors.Count > 0)
        {
            Policy = policy;
            return Page();
        }
        await store.SavePolicyAsync(policy, DeviceAccess.Actor(User), ct);
        Notice = "Saved.";
        return RedirectToPage();
    }

    int Minutes(string name)
    {
        if (double.TryParse(Request.Form[name], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            return (int)Math.Round(h * 60);
        Errors.Add("Lock down now: enter the longest lock-down, in hours.");
        return 0;
    }
}
