using System.Security.Claims;
using Lull.Portal.Data;

namespace Lull.Portal.Auth;

/// <summary>What the sign-in cookie carries: who, their role, and the security stamp it was issued under.</summary>
public static class AccountClaims
{
    public const string Stamp = "lull:stamp";
    public const string DisplayName = "lull:name";
    /// <summary>"true" when the password used to sign in doesn't meet the current rules: only /Account is usable.</summary>
    public const string MustChangePassword = "lull:must-change-password";
    /// <summary>"true" for an account that signs in with Entra ID: signing out also signs out of Microsoft.</summary>
    public const string Entra = "lull:entra";
    /// <summary>"true" for a local admin without two-step sign-in yet: only /TwoFactor (to set it up) is usable.</summary>
    public const string MustSetUpTwoFactor = "lull:must-set-up-two-factor";
    /// <summary>"true" when this session passed two-step sign-in. An account that has it accepts no session without it.</summary>
    public const string TwoFactor = "lull:two-factor";

    /// <param name="twoFactor">The session passed two-step sign-in (or just set it up).</param>
    public static ClaimsPrincipal For(UserRecord user, bool mustChangePassword = false, bool twoFactor = false)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(Stamp, user.SecurityStamp.ToString()),
            new(DisplayName, user.DisplayName),
        ];
        if (mustChangePassword) claims.Add(new(MustChangePassword, "true"));
        if (user.IsEntra) claims.Add(new(Entra, "true"));
        if (user.NeedsTwoFactorSetup) claims.Add(new(MustSetUpTwoFactor, "true"));
        if (twoFactor) claims.Add(new(TwoFactor, "true"));
        return new(new ClaimsIdentity(claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Guid Id(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
