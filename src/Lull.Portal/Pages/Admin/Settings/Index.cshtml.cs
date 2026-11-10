using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Settings;

/// <summary>IT's school hours, which parents' downtime, lock-downs and releases can never touch.</summary>
public sealed class SchoolHoursModel(PortalStore store) : PageModel
{
    public BoundsPolicy Policy { get; private set; } = BoundsPolicy.Default;
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Policy = await store.GetPolicyAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Policy = await store.GetPolicyAsync(ct);
        var hours = new SchoolHours(Time("schoolStart", "the time school starts"), Time("schoolEnd", "the time school ends"));
        if (Errors.Count > 0) return Page();

        // Parent controls (and holidays, which the store keeps) are left as they are.
        var policy = Policy with { SchoolHours = hours, Version = BoundsPolicy.CurrentVersion };
        Errors.AddRange(SettingsRules.CheckPolicy(policy));
        if (Errors.Count > 0)
        {
            Policy = policy;
            return Page();
        }
        var changed = await store.SavePolicyAsync(policy, DeviceAccess.Actor(User), ct);
        Notice = changed == 0 ? "Saved. No Mac needed changing." : $"Saved. {changed} Mac(s) were adjusted to stay out of school hours.";
        return RedirectToPage();
    }

    TimeOnly Time(string name, string label)
    {
        if (Window.TryParseTime(Request.Form[name], out var t)) return t;
        Errors.Add($"Enter {label}.");
        return default;
    }
}
