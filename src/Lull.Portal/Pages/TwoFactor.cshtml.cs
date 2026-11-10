using System.Globalization;
using System.Security.Cryptography;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using QRCoder;

namespace Lull.Portal.Pages;

/// <summary>
/// Two-step sign-in: local admins set it up here (they're held here until they do), with a
/// new key added to an authenticator app and a code from it. The key travels in the form
/// encrypted and signed for this account (<see cref="Pending"/>), and is saved only once a
/// code shows it was added. Entra accounts use Entra's own MFA; parents can't have it yet.
/// </summary>
[EnableRateLimiting(LoginModel.RateLimit)]
public sealed class TwoFactorModel(UserStore users, IDataProtectionProvider protection, TimeProvider clock) : PageModel
{
    /// <summary>How long a new key shown here may take to be confirmed.</summary>
    static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);

    public UserRecord Account { get; private set; } = null!;
    public List<string> Errors { get; } = [];
    /// <summary>The new key, encrypted for this account, for the confirming POST.</summary>
    public string Pending { get; private set; } = "";
    /// <summary>The new key as people type it in: base32, in groups of four.</summary>
    public string KeyText { get; private set; } = "";
    /// <summary>The new key as an otpauth:// link, which opens an authenticator app.</summary>
    public string KeyUri { get; private set; } = "";
    /// <summary>
    /// That link as a QR code to scan with a phone: SVG markup put straight into the page. An
    /// &lt;img&gt; would need a data: URL (which the CSP blocks) or a URL carrying the key.
    /// </summary>
    public string KeyQrSvg { get; private set; } = "";

    IDataProtector Keys => protection.CreateProtector("Lull.Portal.TwoFactorSetup.v1", AccountClaims.Id(User).ToString());

    /// <summary>Only local admins set it up (for now).</summary>
    public bool CanSetUp => Account.Role == Roles.Admin && !Account.IsEntra;

    async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (await users.GetAsync(AccountClaims.Id(User), ct) is not { } account) return false;
        Account = account;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        if (CanSetUp && !Account.HasTwoFactor) Offer(Totp.NewSecret());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? pending, string? code, CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        if (!CanSetUp || Account.HasTwoFactor) return RedirectToPage();
        if (ReadPending(pending) is not { } key)
        {
            Errors.Add("That took too long. Add this new key instead.");
            Offer(Totp.NewSecret());
            return Page();
        }
        if (await users.EnableTwoFactorAsync(Account.Id, key, code, ct) is not { } updated)
        {
            Errors.Add("That code isn't right. Check the key was added, and use the code it shows now.");
            Offer(key);
            return Page();
        }
        // Setting it up ended every session: this one carries on, having passed it.
        await HttpContext.SignInAsync(AccountClaims.For(updated, twoFactor: true));
        return Redirect("/Admin/Devices");
    }

    void Offer(byte[] key)
    {
        Pending = Keys.Protect(string.Join('|', Convert.ToBase64String(key),
            (clock.GetUtcNow() + PendingLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        var text = Totp.Base32(key);
        KeyText = string.Join(' ', Enumerable.Range(0, (text.Length + 3) / 4).Select(i => text.Substring(i * 4, Math.Min(4, text.Length - i * 4))));
        KeyUri = Totp.Uri($"Lull ({Request.Host.Host})", Account.Email, key);
        KeyQrSvg = QrSvg(KeyUri);
    }

    /// <summary>
    /// Black on white with the quiet zone, as phone cameras read best (whatever the page's colours),
    /// sized by CSS: only a viewBox, and fill attributes rather than the inline styles the CSP refuses.
    /// </summary>
    static string QrSvg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(1, 1), "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    byte[]? ReadPending(string? pending)
    {
        try
        {
            var parts = Keys.Unprotect(pending ?? "").Split('|');
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var until)
                && clock.GetUtcNow().ToUnixTimeSeconds() < until)
                return Convert.FromBase64String(parts[0]);
        }
        catch (CryptographicException)
        {
            // Tampered with, for another account, or from before the keys changed.
        }
        return null;
    }
}
