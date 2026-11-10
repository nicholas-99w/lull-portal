using System.Security.Claims;
using System.Threading.RateLimiting;
using Lull.Portal;
using Lull.Portal.Api;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// Docker HEALTHCHECK: the runtime image has no curl, so the app checks itself.
if (args.Contains("--healthcheck"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        var res = await http.GetAsync("http://127.0.0.1:8080/healthz");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(PortalOptions.Section).Get<PortalOptions>() ?? new PortalOptions();
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));

// Fail at startup rather than run a portal any Mac (or anyone) could register with.
if (string.IsNullOrWhiteSpace(options.ConnectionString))
    throw new InvalidOperationException("Lull__ConnectionString is not set.");
if (options.EnrolmentSecret.Length < PortalOptions.MinimumEnrolmentSecretLength)
    throw new InvalidOperationException(
        $"Lull__EnrolmentSecret must be at least {PortalOptions.MinimumEnrolmentSecretLength} characters.");
if (options.AuditRetentionDays < 0)
    throw new InvalidOperationException("Lull__AuditRetentionDays must be 0 (keep forever) or a number of days.");
if (options.Problem() is { } problem)
    throw new InvalidOperationException(problem);

builder.Logging.ClearProviders();
// Scopes add RequestId to each request's log lines: the reference the error page shows.
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
// The cookie-signing keys live unencrypted in the /keys volume (normal for
// Linux containers; see docs/administration.md, "Backups"). Without this, every new key logs "No XML
// encryptor configured". Real key-ring problems are logged as errors.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", LogLevel.Error);

builder.Services.AddSingleton(TimeProvider.System);
var connectionString = options.EffectiveConnectionString(); // fails here, clearly, rather than in a restart loop
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PortalStore>();
builder.Services.AddSingleton<PortalSettings>();
builder.Services.AddSingleton(new PasswordHasher(options.PasswordIterations));
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<DeviceAuthGuard>();
builder.Services.AddHostedService<AuditRetention>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Wire.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Wire.Options.DefaultIgnoreCondition;
});

if (options.DataProtectionKeysPath is { Length: > 0 } keys)
{
    // Keeps sign-in cookies valid across container restarts.
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("Lull.Portal");
}

// AllowedHosts (LULL_ALLOWED_HOSTS): when it names the portal's domain, the container's own
// health check (http://127.0.0.1:8080) must still get through.
builder.Services.PostConfigure<HostFilteringOptions>(o =>
{
    if (o.AllowedHosts is { Count: > 0 } hosts && !hosts.Contains("*"))
        o.AllowedHosts = [.. hosts.Union(["localhost", "127.0.0.1"])];
});

builder.Services.AddProblemDetails();
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));
// SecurityHeaders sends X-Frame-Options: DENY on every response; antiforgery would add SAMEORIGIN to forms.
builder.Services.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = true);

var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.LogoutPath = "/Logout";
        o.AccessDeniedPath = "/Denied";
        o.Cookie.Name = "lull.portal";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Behind the proxy (production), always Secure: the proxy terminates TLS, so the request here is plain HTTP.
        // Without one (local testing on http://localhost), only on HTTPS requests.
        o.Cookie.SecurePolicy = options.TrustForwardedHeaders ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // A page left open polls (LiveUpdates): that isn't someone using the portal, so it doesn't keep them signed in.
        o.Events.OnCheckSlidingExpiration = ctx =>
        {
            if (LiveUpdates.IsLive(ctx.HttpContext.Request)) ctx.ShouldRenew = false;
            return Task.CompletedTask;
        };
        // Every request re-checks the account: deleting a user, resetting their
        // password or changing their role/email ends their sessions at once.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var principal = ctx.Principal;
            UserRecord? user = null;
            if (principal is not null && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
                user = await users.GetAsync(id, ctx.HttpContext.RequestAborted);
            // Two-step sign-in too: an account that has it takes only sessions that passed it, and
            // a local admin without it only a session that's held to setting it up.
            bool valid = principal is not null && user is not null
                && user.SecurityStamp.ToString() == principal.FindFirstValue(AccountClaims.Stamp)
                && principal.IsInRole(user.Role) && user.Email == principal.Identity?.Name
                && (!user.HasTwoFactor || principal.HasClaim(AccountClaims.TwoFactor, "true"))
                && (!user.NeedsTwoFactorSetup || principal.HasClaim(AccountClaims.MustSetUpTwoFactor, "true"));
            if (!valid)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
if (options.Entra.Enabled)
    authentication.AddOpenIdConnect(EntraSignIn.Scheme, "Microsoft", o => EntraSignIn.Configure(o, options.Entra));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Roles.Parent, p => p.RequireRole(Roles.Parent));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AuthorizeFolder("/Admin", Roles.Admin);
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/FirstRun");
    o.Conventions.AllowAnonymousToPage("/Denied");
    o.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per client (IPv4 address or IPv6 /64): behind a reverse proxy, that needs
    // Lull__TrustForwardedHeaders, or every request counts against the proxy's address.
    Func<HttpContext, RateLimitPartition<string>> PerClient(int perMinute) => http =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimits.ClientKey(http),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
    o.GlobalLimiter = RateLimits.PasswordHashing(Environment.ProcessorCount);
    o.AddPolicy(DeviceApi.RegisterRateLimit, PerClient(options.RegisterRateLimitPerMinute));
    o.AddPolicy(Lull.Portal.Pages.LoginModel.RateLimit, PerClient(options.RateLimitPerMinute));
});

var app = builder.Build();

if (options.TrustForwardedHeaders)
{
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    // The proxy's address (on the container network) isn't known in advance: by default any
    // private or loopback address, never one from the internet (Lull__TrustedProxies narrows it).
    fwd.KnownIPNetworks.Clear();
    fwd.KnownProxies.Clear();
    foreach (var network in options.TrustedProxyNetworks()) fwd.KnownIPNetworks.Add(network);
    app.UseForwardedHeaders(fwd);
}

// Deployment step, run before the new version starts (the compose "migrate" service):
//   dotnet Lull.Portal.dll --migrate
if (args.Contains("--migrate"))
{
    var applied = await Migrator.RunAsync(connectionString, app.Services.GetRequiredService<ILoggerFactory>());
    Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}");
    if (options.AppDatabasePassword is { Length: > 0 } appPassword)
    {
        await Migrator.EnsureRoleAsync(connectionString, appPassword);
        await Migrator.GrantDataAccessAsync(connectionString);
        Console.WriteLine($"Database user {Migrator.AppRole} is set up (data access only).");
    }
    else
    {
        Console.WriteLine($"Lull__AppDatabasePassword isn't set: no {Migrator.AppRole} user, so the portal must connect as the owner.");
    }
    return 0;
}

// Refuse to serve against a schema this build wasn't written for, rather than fail request by request.
var schema = Migrator.Check(connectionString);
if (schema.Unknown.Count > 0)
    throw new InvalidOperationException(
        $"The database was migrated by a newer portal ({string.Join(", ", schema.Unknown)}). Deploy that version or later.");
if (schema.Pending.Count > 0)
    throw new InvalidOperationException(
        $"The database needs migrating ({string.Join(", ", schema.Pending)}). Run: dotnet Lull.Portal.dll --migrate");

// Recovery for a lost owner password, run on the server:
//   docker compose exec portal dotnet Lull.Portal.dll --reset-owner
if (args.Contains("--reset-owner"))
{
    var reset = await app.Services.GetRequiredService<UserStore>().ResetOwnerAsync(CancellationToken.None);
    Console.WriteLine(reset is { } r
        ? $"New one-time setup code for the owner ({r.Email}): {r.Code}\nUse it on the portal's \"Set up your password\" page within 30 days."
        : "No owner yet: open the portal to set it up.");
    return 0;
}

static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");
if (!app.Environment.IsDevelopment())
{
    // Unhandled errors: lulld gets application/problem+json (it only reads the
    // status), people get the error page. Development shows the exception instead.
    app.UseWhen(IsApi, api => api.UseExceptionHandler());
    app.UseWhen(http => !IsApi(http), web => web.UseExceptionHandler("/Error"));
    app.UseHsts(); // only on HTTPS requests: behind a proxy, that needs Lull__TrustForwardedHeaders
}
// Empty 4xx responses (not found, rate limited, expired form) get the error page too.
app.UseWhen(http => !IsApi(http), web => web.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
app.UseSecurityHeaders(options.Entra.Enabled);
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
// First run: until an admin exists, every page leads to the first-run page,
// where the first visitor (IT, deploying the portal) creates the owner. The
// device API and health check keep working, so Macs can register meanwhile.
app.Use(async (http, next) =>
{
    var path = http.Request.Path;
    // Static files never get here (UseStaticFiles answered them); a request with no endpoint is a 404 anyway.
    bool exempt = http.GetEndpoint() is null || path.StartsWithSegments("/api") || path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/FirstRun") || path.StartsWithSegments("/Error") || path.StartsWithSegments("/brand");
    if (!exempt && !await http.RequestServices.GetRequiredService<UserStore>().HasAdminAsync(http.RequestAborted))
    {
        http.Response.Redirect("/FirstRun");
        return;
    }
    // Signed in with a password that predates the current rules: only the
    // Account page (to change it) and signing out are available.
    if (!exempt && http.User.HasClaim(AccountClaims.MustChangePassword, "true")
        && !path.StartsWithSegments("/Account") && !path.StartsWithSegments("/Logout"))
    {
        http.Response.Redirect("/Account");
        return;
    }
    // Then (once any password change is done) a local admin without two-step sign-in sets it up before anything else.
    if (!exempt && http.User.HasClaim(AccountClaims.MustSetUpTwoFactor, "true") && !http.User.HasClaim(AccountClaims.MustChangePassword, "true")
        && !path.StartsWithSegments("/TwoFactor") && !path.StartsWithSegments("/Logout"))
    {
        http.Response.Redirect("/TwoFactor");
        return;
    }
    await next();
});
app.UseAuthorization();

app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
}).AllowAnonymous();
// IT's branding (Admin/Settings/Branding): its colours as a stylesheet (the Content-Security-Policy
// allows no inline styles) and its logo. Pages link them with the branding's version (?v=), so
// browsers keep them until it changes. Anyone may fetch them: the sign-in page is branded too.
app.MapGet("/brand/style.css", async (PortalStore store, HttpContext http, CancellationToken ct) =>
{
    var brand = await store.GetBrandingAsync(ct);
    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Text(brand.Branding.Css(), "text/css; charset=utf-8");
}).AllowAnonymous();
app.MapGet("/brand/logo", async (PortalStore store, HttpContext http, CancellationToken ct) =>
{
    if (await store.GetLogoAsync(ct) is not { } logo) return Results.NotFound();
    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(logo.Bytes, logo.Type);
}).AllowAnonymous();
app.MapDeviceApi();
// The Setup page became Settings: old bookmarks still work.
app.MapGet("/Admin/Setup", () => Results.Redirect("/Admin/Settings"));
app.UseLiveUpdates();
app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
