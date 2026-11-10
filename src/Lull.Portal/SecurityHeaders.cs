namespace Lull.Portal;

/// <summary>
/// Browser hardening headers on every response (pages, static files, API), and
/// <c>Cache-Control: no-store</c> on pages. HSTS is separate (UseHsts), because it
/// belongs only on HTTPS responses.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The portal's only scripts are its own week-grid.js (dragging across an advanced
    /// schedule), branding.js (colour pickers on IT's Branding page), menu.js (closing a menu
    /// from elsewhere), admin.js (local times, copying a setup code) and live.js (keeping a parent's
    /// pages up to date), all of which pages work without, and it has no inline scripts or styles (style="" attributes are classes
    /// in site.css; IT's colours are the /brand/style.css stylesheet), so everything is
    /// limited to its own origin. An inline script or a third-party asset means changing this deliberately.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// With Entra sign-in: the Sign out form leads on to Microsoft's sign-out page,
    /// and browsers apply form-action to where a form's response redirects.
    /// </summary>
    public const string ContentSecurityPolicyWithEntra =
        "default-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self' " + EntraOptions.LoginOrigin + "; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool entra) => app.Use(async (http, next) =>
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = entra ? ContentSecurityPolicyWithEntra : ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        // frame-ancestors covers current browsers; X-Frame-Options covers older ones.
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // Pages (not static files, which have no endpoint, nor the device API, nor the branding's
        // stylesheet and logo, which are the same for everyone): never stored, so a shared
        // computer's back button or cache can't show a setup code or a child's Mac after sign-out.
        http.Response.OnStarting(() =>
        {
            if (http.GetEndpoint() is not null && !http.Request.Path.StartsWithSegments("/api") && !http.Request.Path.StartsWithSegments("/brand"))
                http.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });
}
