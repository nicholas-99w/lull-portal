using Lull.Portal.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin;

public sealed class AuditModel(PortalStore store, PortalSettings settings) : PageModel
{
    public string? Query { get; private set; }
    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public Pager Pager { get; private set; } = null!;
    public int RetentionDays { get; private set; }

    public async Task OnGetAsync(string? q, int? p, CancellationToken ct)
    {
        Query = q?.Trim();
        RetentionDays = (await settings.GetAsync(ct)).AuditRetentionDays;
        var page = await store.SearchAuditAsync(Query, p, ct);
        Entries = page.Items;
        Pager = Pager.For(page, "/Admin/Audit", Query, "entries");
    }
}
