using Lull.Portal.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Pages;

public sealed class LogoutModel(IOptions<PortalOptions> options) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        bool entra = User.HasClaim(AccountClaims.Entra, "true") && options.Value.Entra.Enabled;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // Out of Microsoft too, so the next person on a shared computer isn't signed straight back in.
        if (entra) return SignOut(new AuthenticationProperties { RedirectUri = "/Login" }, EntraSignIn.Scheme);
        return Redirect("/Login");
    }
}
