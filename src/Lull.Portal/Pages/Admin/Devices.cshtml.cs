using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin;

public sealed class DevicesModel(PortalStore store, TimeProvider clock) : PageModel
{
    static readonly (DeviceFilter Filter, string Key, string Label)[] Views =
    [
        (DeviceFilter.All, "", "All"),
        (DeviceFilter.Attention, "attention", "Needs attention"),
        (DeviceFilter.Offline, "offline", "Offline"),
        (DeviceFilter.Downtime, "downtime", "In downtime"),
        (DeviceFilter.Pending, "pending", "Pending"),
    ];

    public string? Query { get; private set; }
    public string? FilterKey { get; private set; }
    public List<DeviceRow> Rows { get; } = [];
    public IReadOnlyList<FilterChip> Filters { get; private set; } = [];
    public Pager Pager { get; private set; } = null!;

    public async Task OnGetAsync(string? q, string? f, int? p, CancellationToken ct)
    {
        Query = q?.Trim();
        var view = Views.FirstOrDefault(v => v.Key == f, Views[0]);
        FilterKey = view.Key.Length > 0 ? view.Key : null;
        var now = clock.GetUtcNow();
        var (page, counts) = await store.SearchDevicesAsync(Query, view.Filter, p, ct);
        foreach (var d in page.Items)
            Rows.Add(new(d, DeviceStatusView.From(d.Settings, d.LastReport, d.LastCheckInAt, now)));
        Filters = Views.Select(v => new FilterChip(
            v.Label, counts[v.Filter], ListLink.To("/Admin/Devices", Query, v.Key.Length > 0 ? v.Key : null), v.Filter == view.Filter)).ToList();
        Pager = Pager.For(page, "/Admin/Devices", Query, "Macs", FilterKey);
    }
}
