using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Lull.Portal.Pages;

/// <summary>
/// First run: while the portal has no admin, the first visitor creates the
/// owner account. Owner decision: no extra code, because IT deploys the
/// portal and opens it first; keep it unreachable to others until then.
/// Gone (redirects to sign-in) as soon as an admin exists.
/// </summary>
[EnableRateLimiting(LoginModel.RateLimit)]
public sealed class FirstRunModel(UserStore users, ILogger<FirstRunModel> log) : PageModel
{
    public string? Name { get; private set; }
    public string? Email { get; private set; }
    public List<string> Errors { get; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await users.HasAdminAsync(ct) ? Redirect("/Login") : Page();

    public async Task<IActionResult> OnPostAsync(string? name, string? email, string? password, string? confirm, CancellationToken ct)
    {
        if (await users.HasAdminAsync(ct)) return Redirect("/Login");
        (Name, Email) = (name, email);
        if (password != confirm) Errors.Add("The two passwords don't match.");
        if (PasswordRules.Problem(Roles.Admin, password) is { } p) Errors.Add(p);
        if (Errors.Count > 0) return Page();
        UserRecord? owner;
        try
        {
            owner = await users.CreateOwnerAsync(email ?? "", name ?? "", password!, ct);
        }
        catch (UserChangeException e)
        {
            Errors.Add(e.Message);
            return Page();
        }
        if (owner is null) return Redirect("/Login"); // someone else got there first
        log.LogWarning("portal set up: owner account {Email} created", owner.Email);
        await HttpContext.SignInAsync(AccountClaims.For(owner));
        return Redirect("/TwoFactor"); // admins need two-step sign-in before anything else
    }
}
