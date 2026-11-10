using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Users;

/// <summary>
/// Create, edit, re-code, unlock or delete one user. Entra ID accounts can only
/// be viewed or deleted: their details come from Entra.
/// </summary>
public sealed class EditUserModel(UserStore users, PortalSettings settings, TimeProvider clock) : PageModel
{
    const string LocalOff = "Local accounts are turned off (Settings, Sign-in): people sign in with Microsoft, and appear here after their first sign-in.";

    public UserRecord? Existing { get; private set; }
    public string Name { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string Role { get; private set; } = Roles.Parent;
    public string Students { get; private set; } = "";
    public DateTimeOffset Now => clock.GetUtcNow();
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }
    /// <summary>Shown once, straight after creating a user or issuing a code. Never stored.</summary>
    public (string Email, string Code)? IssuedCode { get; private set; }

    /// <summary>Whether local accounts can sign in (Settings, Sign-in), read before every handler.</summary>
    public bool LocalSignIn { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        LocalSignIn = (await settings.GetAsync(HttpContext.RequestAborted)).LocalSignIn;
        await next();
    }

    string Actor => DeviceAccess.Actor(User);
    Guid Me => AccountClaims.Id(User);
    /// <summary>
    /// The owner's account can only be changed by the owner; an Entra account only in
    /// Entra; and with local sign-in off, no new local account can be made.
    /// </summary>
    public bool ReadOnly => Existing is { IsOwner: true } && Existing.Id != Me || Existing is { IsEntra: true }
        || Existing is null && !LocalSignIn;

    async Task<bool> LoadAsync(Guid? id, CancellationToken ct)
    {
        if (id is null) return true;
        Existing = await users.GetAsync(id.Value, ct);
        if (Existing is null) return false;
        (Name, Email, Role, Students) = (Existing.DisplayName, Existing.Email, Existing.Role, string.Join(", ", Existing.Students));
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Existing is null && !LocalSignIn) Errors.Add(LocalOff);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, string? name, string? email, string? role, string? students, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        (Name, Email, Role, Students) = (name ?? "", email ?? "", role ?? "", students ?? "");
        var (names, invalid) = ChildList.Parse(students);
        if (invalid.Count > 0) Errors.Add($"Not a valid student username: {string.Join(", ", invalid)}.");
        if (Errors.Count > 0) return Page();
        try
        {
            if (Existing is null)
            {
                if (!LocalSignIn) throw new UserChangeException(LocalOff);
                var (created, code) = await users.CreateAsync(Email, Name, Role, names, Actor, ct);
                Existing = created;
                (Name, Email, Role, Students) = (created.DisplayName, created.Email, created.Role, string.Join(", ", created.Students));
                IssuedCode = (created.Email, code);
                return Page(); // not a redirect: the code must be shown now, it isn't stored
            }
            await users.UpdateAsync(Existing.Id, Email, Name, Existing.IsOwner ? Roles.Admin : Role, names, Me, Actor, ct);
            Notice = "Saved.";
            return RedirectToPage(new { id = Existing.Id });
        }
        catch (UserChangeException e)
        {
            Errors.Add(e.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCodeAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        try
        {
            var code = await users.NewSetupCodeAsync(id, Me, Actor, ct);
            await LoadAsync(id, ct);
            IssuedCode = (Existing!.Email, code);
            if (id == Me) await HttpContext.SignOutAsync(); // you just reset your own password
        }
        catch (UserChangeException e)
        {
            Errors.Add(e.Message);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostUnlockAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        await users.UnlockAsync(id, Actor, ct);
        Notice = "Unlocked.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool confirm, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (!confirm)
        {
            Errors.Add("Tick the box to confirm.");
            return Page();
        }
        try
        {
            await users.DeleteAsync(id, AccountClaims.Id(User), Actor, ct);
        }
        catch (UserChangeException e)
        {
            Errors.Add(e.Message);
            return Page();
        }
        TempData["Notice"] = $"Deleted {Existing!.Email}.";
        return Redirect("/Admin/Users");
    }
}
