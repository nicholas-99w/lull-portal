using Lull.Portal.Data;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Pages.Admin.Users;

public sealed class UsersModel(UserStore users, PortalSettings settings, TimeProvider clock, IOptions<PortalOptions> options) : PageModel
{
    public bool EntraEnabled => options.Value.Entra.Enabled;

    /// <summary>Local accounts can be created (one by one or by CSV) (Settings, Sign-in), read before every handler.</summary>
    public bool LocalSignIn { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        LocalSignIn = (await settings.GetAsync(HttpContext.RequestAborted)).LocalSignIn;
        await next();
    }
    static readonly (UserFilter Filter, string Key, string Label)[] Views =
    [
        (UserFilter.All, "", "All"),
        (UserFilter.Parents, "parents", "Parents"),
        (UserFilter.Admins, "admins", "Admins"),
        (UserFilter.Waiting, "waiting", "Waiting for first sign-in"),
        (UserFilter.Locked, "locked", "Locked"),
    ];

    public string? Query { get; private set; }
    public string? FilterKey { get; private set; }
    public IReadOnlyList<FilterChip> Filters { get; private set; } = [];
    public IReadOnlyList<UserRecord> Users { get; private set; } = [];
    public Pager Pager { get; private set; } = null!;
    public DateTimeOffset Now { get; private set; }
    [Microsoft.AspNetCore.Mvc.TempData] public string? Notice { get; set; }

    public async Task OnGetAsync(string? q, string? f, int? p, CancellationToken ct)
    {
        Query = q?.Trim();
        Now = clock.GetUtcNow();
        var view = Views.FirstOrDefault(v => v.Key == f, Views[0]);
        FilterKey = view.Key.Length > 0 ? view.Key : null;
        var (page, counts) = await users.SearchAsync(Query, view.Filter, p, ct);
        Users = page.Items;
        Filters = Views.Select(v => new FilterChip(
            v.Label, counts[v.Filter], ListLink.To("/Admin/Users", Query, v.Key.Length > 0 ? v.Key : null), v.Filter == view.Filter)).ToList();
        Pager = Pager.For(page, "/Admin/Users", Query, "users", FilterKey);
    }
}
