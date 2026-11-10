using System.Globalization;
using System.Security.Cryptography;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Pages;

/// <summary>
/// Sign-in with Microsoft (Entra ID, when set up) or with email and password.
/// Accounts without a password yet use <see cref="SetupModel"/>. With local
/// sign-in off, the password form is only for the owner, behind <c>?local=true</c>.
/// An account with two-step sign-in then gives a code from its authenticator app: the
/// right password only earns a short-lived, signed <see cref="Ticket"/> for that step.
/// </summary>
[EnableRateLimiting(RateLimit)]
public sealed class LoginModel(
    UserStore users, PortalSettings settings, IOptions<PortalOptions> options, IDataProtectionProvider protection, TimeProvider clock, ILogger<LoginModel> log)
    : PageModel
{
    public const string RateLimit = "login";

    /// <summary>How long the code step may take after the password.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    IDataProtector Tickets => protection.CreateProtector("Lull.Portal.SignInTicket.v1");

    /// <summary>Set once the password is right for an account with two-step sign-in: the page then asks for the code.</summary>
    public string? Ticket { get; private set; }

    public string? Email { get; private set; }
    public string? ReturnUrl { get; private set; }
    public string? Error { get; private set; }
    public bool EntraEnabled => options.Value.Entra.Enabled;
    /// <summary>Whether local accounts can sign in (Settings, Sign-in), read before every handler.</summary>
    public bool LocalSignIn { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        LocalSignIn = (await settings.GetAsync(HttpContext.RequestAborted)).LocalSignIn;
        await next();
    }
    /// <summary>The password form: always with local sign-in on, otherwise only when the owner asks for it.</summary>
    public bool ShowPasswordForm { get; private set; }

    public IActionResult OnGet(string? returnUrl, string? entra, bool local = false)
    {
        // Already signed in (with either kind of account): nothing to do here.
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        ReturnUrl = returnUrl;
        Error = EntraSignIn.Refused.Message(entra);
        ShowPasswordForm = LocalSignIn || local;
        return Page();
    }

    /// <summary>"Sign in with Microsoft": off to Entra, and back to /signin-oidc.</summary>
    public IActionResult OnGetEntra(string? returnUrl)
    {
        if (!EntraEnabled) return NotFound();
        var redirect = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return Challenge(new AuthenticationProperties { RedirectUri = redirect }, EntraSignIn.Scheme);
    }

    public async Task<IActionResult> OnPostAsync(string? email, string? password, string? returnUrl, CancellationToken ct)
    {
        Email = email;
        ReturnUrl = returnUrl;
        ShowPasswordForm = true;
        var result = await users.SignInAsync(email, password, ct, ownerOnly: !LocalSignIn);
        HttpContext.LogFailedSignIn("password", result);
        // A password set before the current rules works once more, only to choose a new one.
        bool mustChange = result.User is { } u && PasswordRules.Problem(u.Role, password) is not null;
        switch (result.Outcome)
        {
            case SignInOutcome.Ok:
                return await FinishAsync(result.User!, mustChange, twoFactor: false, returnUrl);
            case SignInOutcome.NeedsSecondFactor:
                Ticket = IssueTicket(result.User!, mustChange);
                return Page();
            default:
                // Same answer for an unknown email, a wrong password, an account still waiting for
                // its first sign-in and a locked account (even with the right password), so the
                // page doesn't reveal which accounts exist or which passwords are right.
                var mismatch = $"That email and password don't match, or the account is locked for up to {UserStore.LockoutMinutes} minutes after too many attempts.";
                Error = EntraEnabled
                    ? $"{mismatch} If your school uses Microsoft sign-in, use Sign in with Microsoft instead."
                    : $"{mismatch} If you haven't signed in before, set up your password with your setup code.";
                return Page();
        }
    }

    /// <summary>The code from the authenticator app, with the <see cref="Ticket"/> from the password step.</summary>
    public async Task<IActionResult> OnPostCodeAsync(string? ticket, string? code, string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = returnUrl;
        if (ReadTicket(ticket) is not { } t)
        {
            HttpContext.LogFailedSignIn("code", new(SignInOutcome.Invalid, Failure: SignInFailure.OutdatedTicket));
            Error = "That took too long. Sign in again.";
            ShowPasswordForm = true;
            return Page();
        }
        var result = await users.VerifySecondFactorAsync(t.Id, t.Stamp, code, ct);
        HttpContext.LogFailedSignIn("code", result);
        switch (result.Outcome)
        {
            case SignInOutcome.Ok:
                return await FinishAsync(result.User!, t.MustChange, twoFactor: true, returnUrl);
            case SignInOutcome.LockedOut:
                Error = $"Too many attempts. Try again in up to {UserStore.LockoutMinutes} minutes, or ask your school to unlock your account.";
                ShowPasswordForm = true;
                return Page();
            default:
                Error = "That code isn't right. Use the current code from your authenticator app.";
                Ticket = ticket;
                return Page();
        }
    }

    async Task<IActionResult> FinishAsync(UserRecord user, bool mustChange, bool twoFactor, string? returnUrl)
    {
        await HttpContext.SignInAsync(AccountClaims.For(user, mustChange, twoFactor));
        log.LogInformation("{Email} signed in ({Role}{TwoFactor})", user.Email, user.Role, twoFactor ? ", two-step" : "");
        if (mustChange) return Redirect("/Account");
        if (user.NeedsTwoFactorSetup) return Redirect("/TwoFactor");
        if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl!);
        return Redirect(user.Role == Roles.Admin ? "/Admin/Devices" : "/");
    }

    // The ticket says whose password was right, under which security stamp (a reset since makes it
    // useless), whether that password must be changed, and until when it may be used.
    string IssueTicket(UserRecord user, bool mustChange) => Tickets.Protect(string.Join('|',
        user.Id, user.SecurityStamp, (clock.GetUtcNow() + TicketLifetime).ToUnixTimeSeconds(), mustChange ? "1" : "0"));

    (Guid Id, Guid Stamp, bool MustChange)? ReadTicket(string? ticket)
    {
        try
        {
            var parts = Tickets.Unprotect(ticket ?? "").Split('|');
            if (parts.Length == 4 && Guid.TryParse(parts[0], out var id) && Guid.TryParse(parts[1], out var stamp)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var until)
                && clock.GetUtcNow().ToUnixTimeSeconds() < until)
                return (id, stamp, parts[3] == "1");
        }
        catch (CryptographicException)
        {
            // Tampered with, or from before the keys changed.
        }
        return null;
    }
}
