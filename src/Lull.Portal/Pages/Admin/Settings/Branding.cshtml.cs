using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Settings;

/// <summary>IT's branding: the school's name, logo and colours, and a few words for parents.</summary>
[RequestFormLimits(MultipartBodyLengthLimit = Branding.MaxLogoBytes + 64 * 1024)]
[RequestSizeLimit(Branding.MaxLogoBytes + 64 * 1024)]
public sealed class BrandingModel(PortalStore store) : PageModel
{
    public BrandingState State { get; private set; } = BrandingState.Default;
    /// <summary>The saved branding, or after a mistake what IT entered.</summary>
    public Branding Branding { get; private set; } = Branding.Default;
    public List<string> Errors { get; } = [];
    [TempData] public string? Notice { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        State = await store.GetBrandingAsync(ct);
        Branding = State.Branding;
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? logo, bool removeLogo, CancellationToken ct)
    {
        State = await store.GetBrandingAsync(ct);
        Branding = new Branding
        {
            SchoolName = Text("schoolName"),
            Primary = Color("primary"),
            HeaderBackground = Color("headerBackground"),
            HeaderText = Color("headerText"),
            Background = Color("background"),
            Downtime = Color("downtime"),
            LogoOnly = Request.Form["logoOnly"] == "true",
            AlwaysLight = Request.Form["theme"] == "light",
            LoginMessage = Text("loginMessage"),
            SupportContact = Text("supportContact"),
        };
        Errors.AddRange(Branding.Check());

        (byte[] Bytes, string Type)? image = null;
        if (logo is { Length: > 0 })
        {
            if (logo.Length > Branding.MaxLogoBytes)
            {
                Errors.Add($"The logo is too big: it can be at most {Branding.MaxLogoBytes / 1024} KB.");
            }
            else
            {
                using var copy = new MemoryStream();
                await logo.CopyToAsync(copy, ct);
                var bytes = copy.ToArray();
                if (Logo.TypeOf(bytes) is { } type) image = (bytes, type);
                else Errors.Add("The logo must be a PNG, JPEG or WebP image.");
            }
        }
        if (Errors.Count > 0) return Page();

        await store.SaveBrandingAsync(Branding, image, removeLogo && image is null, DeviceAccess.Actor(User), ct);
        Notice = "Saved. Everyone sees the new look from their next page.";
        return RedirectToPage();
    }

    string? Text(string name) => Request.Form[name].ToString().Trim() is { Length: > 0 } v ? v : null;

    /// <summary>The colour as "#rrggbb", null if empty, or as typed if it isn't one (for <see cref="Branding.Check"/> to report).</summary>
    string? Color(string name) => Text(name) is { } v ? Branding.NormalizeColor(v) ?? v : null;
}
