using System.Net;
using System.Net.Http.Headers;

namespace Lull.Portal.Tests.Integration;

/// <summary>IT's branding: saved from Settings, then on every page, the sign-in page included.</summary>
public class BrandingPageTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>Posts the Branding form as a browser does (multipart, for the logo).</summary>
    static async Task<HttpResponseMessage> SaveAsync(HttpClient admin, Dictionary<string, string> fields, (byte[] Bytes, string Name)? logo = null)
    {
        var page = await admin.GetStringAsync("/Admin/Settings/Branding");
        var form = new MultipartFormDataContent { { new StringContent(PortalFactory.Antiforgery(page)), "__RequestVerificationToken" } };
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        if (logo is { } l)
        {
            var file = new ByteArrayContent(l.Bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "logo", l.Name);
        }
        return await admin.PostAsync("/Admin/Settings/Branding", form);
    }

    [Fact]
    public async Task LogoOnlyAndAlwaysLightReachEveryPage()
    {
        var admin = await portal.SignInAdminAsync();
        try
        {
            // Older branding has neither: the saved JSON lacks them, and it's the look it always was.
            var page = await portal.Browser().GetStringAsync("/Login");
            Assert.DoesNotContain("data-theme", page);
            Assert.Contains("media=\"(prefers-color-scheme: dark)\"", page);

            await SaveAsync(admin, new() { ["schoolName"] = "Springfield High", ["logoOnly"] = "true", ["theme"] = "light" }, (Png, "crest.png"));
            page = await portal.Browser().GetStringAsync("/Login");
            Assert.Contains("<html lang=\"en-AU\" data-theme=\"light\">", page);
            Assert.DoesNotContain("media=\"(prefers-color-scheme: dark)\"", page);
            // Out of sight, but still there for screen readers.
            Assert.Contains("<span class=\"sr-only\">Springfield High</span>", page);
            var form = await admin.GetStringAsync("/Admin/Settings/Branding");
            Assert.Contains("name=\"logoOnly\" value=\"true\" checked", form);
            Assert.Contains("name=\"theme\" value=\"light\" checked", form);

            // Logo-only needs a logo: without one, the name is all there is.
            await SaveAsync(admin, new() { ["schoolName"] = "Springfield High", ["logoOnly"] = "true", ["removeLogo"] = "true" });
            page = await portal.Browser().GetStringAsync("/Login");
            Assert.Contains("<span class=\"brand-name\">Springfield High</span>", page);
        }
        finally
        {
            await SaveAsync(admin, [], null);
            await SaveAsync(admin, new() { ["removeLogo"] = "true" });
        }
    }

    [Fact]
    public async Task BrandingReachesEveryPageAndCanBeUndone()
    {
        var admin = await portal.SignInAdminAsync();
        try
        {
            var saved = await SaveAsync(admin, new()
            {
                ["schoolName"] = "Springfield High", ["primary"] = "#1F4E79", ["headerBackground"] = "1f4e79", ["headerText"] = "#fff",
                ["loginMessage"] = "Parents: use your school account.", ["supportContact"] = "ithelp@springfield.test",
            }, (Png, "crest.png"));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

            var signIn = WebUtility.HtmlDecode(await portal.Browser().GetStringAsync("/Login"));
            Assert.Contains("<title>Sign in · Springfield High</title>", signIn);
            Assert.Contains("Parents: use your school account.", signIn);
            Assert.Contains("Need help? Contact ithelp@springfield.test", signIn);
            var stylesheet = System.Text.RegularExpressions.Regex.Match(signIn, "href=\"(/brand/style.css\\?v=\\d+)\"").Groups[1].Value;
            Assert.NotEmpty(stylesheet);

            var css = await portal.Browser().GetAsync(stylesheet);
            Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
            Assert.Contains("--accent-fill: #1f4e79;", await css.Content.ReadAsStringAsync());
            Assert.Contains("--header-text: #ffffff;", await css.Content.ReadAsStringAsync());
            Assert.Contains("immutable", css.Headers.CacheControl?.ToString());

            var logo = await portal.Browser().GetAsync("/brand/logo");
            Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
            Assert.Contains("class=\"brand-logo\"", await admin.GetStringAsync("/Admin/Devices"));
            Assert.Contains("branding_changed", await admin.GetStringAsync("/Admin/Audit"));

            // Saving again without a file keeps the logo; removing it doesn't.
            await SaveAsync(admin, new() { ["schoolName"] = "Springfield High" });
            Assert.Equal(HttpStatusCode.OK, (await portal.Browser().GetAsync("/brand/logo")).StatusCode);
            await SaveAsync(admin, new() { ["removeLogo"] = "true" });
            Assert.Equal(HttpStatusCode.NotFound, (await portal.Browser().GetAsync("/brand/logo")).StatusCode);
            var plain = WebUtility.HtmlDecode(await portal.Browser().GetStringAsync("/Login"));
            Assert.Contains("<title>Sign in · Lull</title>", plain);
            Assert.DoesNotContain("/brand/style.css", plain);
        }
        finally
        {
            await SaveAsync(admin, []);
        }
    }

    [Fact]
    public async Task UnreadableColoursAndNonImagesAreRefused()
    {
        var admin = await portal.SignInAdminAsync();
        var refused = await SaveAsync(admin, new() { ["primary"] = "#ffe600", ["schoolName"] = "Shelbyville" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains("Buttons and links: #ffe600 doesn't stand out enough", page);
        Assert.Contains("value=\"Shelbyville\"", page); // what IT typed stays

        var svg = await SaveAsync(admin, [], ("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "logo.svg"));
        Assert.Contains("The logo must be a PNG, JPEG or WebP image.", WebUtility.HtmlDecode(await svg.Content.ReadAsStringAsync()));
        Assert.DoesNotContain("Shelbyville", await portal.Browser().GetStringAsync("/Login"));
    }

    [Fact]
    public async Task ParentsCantChangeIt()
    {
        var parent = await portal.ParentAsync("brandparent", "brand01");
        var res = await parent.GetAsync("/Admin/Settings/Branding");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }
}
