using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Lull.Portal.Pages;

/// <summary>
/// First sign-in: email + one-time setup code from IT, then a new password. The
/// code is what stops someone who merely knows a parent's email from claiming
/// the account. Wrong codes count towards the account lockout. With local
/// sign-in off, only the owner can (after <c>--reset-owner</c>).
/// </summary>
[EnableRateLimiting(LoginModel.RateLimit)]
public sealed class SetupModel(UserStore users, PortalSettings settings) : PageModel
{
    public string? Email { get; private set; }
    public List<string> Errors { get; } = [];

    public void OnGet(string? email) => Email = email;

    public async Task<IActionResult> OnPostAsync(string? email, string? code, string? password, string? confirm, CancellationToken ct)
    {
        Email = email;
        if (password != confirm) Errors.Add("The two passwords don't match.");
        // The stricter admin rule is checked once the code is right, so this page doesn't say which emails are admins.
        if (PasswordRules.Problem(Roles.Parent, password) is { } p) Errors.Add(p);
        if (Errors.Count > 0) return Page();

        var result = await users.CompleteSetupAsync(email, code, password!, ct, ownerOnly: !(await settings.GetAsync(ct)).LocalSignIn);
        HttpContext.LogFailedSignIn("setup", result);
        switch (result.Outcome)
        {
            case SignInOutcome.Ok:
                var user = (await users.GetAsync(result.User!.Id, ct))!;
                await HttpContext.SignInAsync(AccountClaims.For(user));
                return Redirect(user.NeedsTwoFactorSetup ? "/TwoFactor" : user.Role == Roles.Admin ? "/Admin/Devices" : "/");
            case SignInOutcome.WeakPassword:
                Errors.Add($"School IT accounts: {result.Problem}");
                return Page();
            default:
                // A locked account gets this too, even with the right code, so the page doesn't reveal which codes are right.
                Errors.Add($"That email and setup code don't match, the code has expired or was already used, or the account is locked for up to {UserStore.LockoutMinutes} minutes after too many attempts. If it keeps happening, ask your school for a new code.");
                return Page();
        }
    }
}
