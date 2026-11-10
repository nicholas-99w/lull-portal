using System.Globalization;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Settings;

/// <summary>IT's school holidays, on which school hours don't apply in the portal.</summary>
public sealed class HolidaysModel(PortalStore store, TimeProvider clock) : PageModel
{
    public BoundsPolicy Policy { get; private set; } = BoundsPolicy.Default;
    public List<string> HolidayErrors { get; } = [];
    [TempData] public string? Notice { get; set; }

    /// <summary>What the add-a-holiday form shows again after a mistake.</summary>
    public string? HolidayName { get; private set; }
    public string? HolidayFirst { get; private set; }
    public string? HolidayLast { get; private set; }

    DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>Holidays still to come or under way (those over are dropped at the next change).</summary>
    public IEnumerable<SchoolHoliday> Holidays => Policy.Holidays.Where(h => h.Last >= Today.AddDays(-1));

    public bool IsUnderWay(SchoolHoliday h) => h.Contains(Today);

    public async Task OnGetAsync(CancellationToken ct) => Policy = await store.GetPolicyAsync(ct);

    public async Task<IActionResult> OnPostAddHolidayAsync(string? name, string? first, string? last, CancellationToken ct)
    {
        Policy = await store.GetPolicyAsync(ct);
        (HolidayName, HolidayFirst, HolidayLast) = (name?.Trim(), first, last);
        var from = Date(first, "first day");
        var to = Date(last, "last day");
        if (HolidayErrors.Count > 0) return Page();

        var holiday = new SchoolHoliday(from, to, HolidayName is { Length: > 0 } ? HolidayName : null);
        HolidayErrors.AddRange(SettingsRules.CheckHoliday(holiday, Policy.Holidays, Today));
        if (HolidayErrors.Count > 0) return Page();
        await store.AddHolidayAsync(holiday, Today, DeviceAccess.Actor(User), ct);
        Notice = $"Added {holiday}. Parents can lock down, unlock and skip downtime during school hours on those days.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveHolidayAsync(string? first, CancellationToken ct)
    {
        if (DateOnly.TryParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && await store.RemoveHolidayAsync(day, DeviceAccess.Actor(User), ct))
            Notice = "Holiday removed.";
        return RedirectToPage();
    }

    DateOnly Date(string? value, string label)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        HolidayErrors.Add($"Choose the {label}.");
        return default;
    }
}
