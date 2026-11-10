using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Lull.Portal.Pages;

/// <summary>
/// Change your own password. Also where someone whose password predates the
/// current rules is held until they choose a compliant one.
/// </summary>
[EnableRateLimiting(LoginModel.RateLimit)]
public sealed class AccountModel(UserStore users) : PageModel
{
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }
    string Role => User.IsInRole(Roles.Admin) ? Roles.Admin : Roles.Parent;
    public string Rules => PasswordRules.Describe(Role);
    public bool Required => User.HasClaim(AccountClaims.MustChangePassword, "true");
    /// <summary>Signs in with Entra ID: no portal password to change.</summary>
    public bool IsEntra => User.HasClaim(AccountClaims.Entra, "true");
    public bool IsAdmin => User.IsInRole(Roles.Admin);

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? current, string? password, string? confirm, CancellationToken ct)
    {
        if (IsEntra) return BadRequest();
        if (password != confirm) Errors.Add("The two new passwords don't match.");
        if (PasswordRules.Problem(Role, password) is { } p) Errors.Add(p);
        if (Errors.Count > 0) return Page();
        var result = await users.ChangePasswordAsync(AccountClaims.Id(User), current ?? "", password!, ct);
        HttpContext.LogFailedSignIn("change-password", result);
        if (result.Outcome == SignInOutcome.LockedOut)
        {
            Errors.Add($"Too many attempts. Try again in up to {UserStore.LockoutMinutes} minutes, or ask your school to unlock your account.");
            return Page();
        }
        if (result.Outcome != SignInOutcome.Ok)
        {
            Errors.Add("Your current password isn't right.");
            return Page();
        }
        // Changing it ended every session: this one carries on (without the must-change flag), the others are signed out.
        await HttpContext.SignInAsync(AccountClaims.For(result.User!, twoFactor: User.HasClaim(AccountClaims.TwoFactor, "true")));
        if (Required) return Redirect(Role == Roles.Admin ? "/Admin/Devices" : "/");
        Notice = "Changed.";
        return RedirectToPage();
    }
}
