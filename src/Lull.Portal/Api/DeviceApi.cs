using System.Text.RegularExpressions;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Api;

/// <summary>
/// Endpoints lulld calls (contract/PORTAL_API.md). Authenticated by device token,
/// not by the cookie that parents and IT use.
/// </summary>
public static partial class DeviceApi
{
    public const string RegisterRateLimit = "device-register";
    const int MaxCheckInBytes = 64 * 1024;
    /// <summary>A registration is four short strings; anything bigger isn't from lulld.</summary>
    const int MaxRegisterBytes = 4 * 1024;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex Identifier();

    public static void MapDeviceApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/device/v1");

        api.MapPost("/register", async (
            RegisterRequest req, PortalStore store, IOptions<PortalOptions> options, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("DeviceApi");
            // Never log the secret, only that it was wrong.
            if (!DeviceTokens.SecretMatches(options.Value.EnrolmentSecret, req.EnrolmentSecret))
            {
                log.LogWarning("register refused: wrong enrolment secret (serial {Serial})", Clean(req.SerialNumber));
                return Results.Unauthorized();
            }
            if (!Identifier().IsMatch(req.SerialNumber ?? "") || !Identifier().IsMatch(req.HardwareUUID ?? ""))
                return Results.BadRequest(new { error = "serialNumber and hardwareUUID must be 1-64 letters, digits or dashes" });

            var token = DeviceTokens.New();
            var outcome = await store.RegisterAsync(req.SerialNumber!, req.HardwareUUID!, DeviceTokens.Hash(token), ct);
            if (outcome is RegisterOutcome.Conflict or RegisterOutcome.ConflictRepeated)
            {
                // Warned once; the Mac then retries at every poll until IT resets it.
                if (outcome == RegisterOutcome.Conflict)
                    log.LogWarning("register refused: serial {Serial} already registered to other hardware (IT must reset it)", req.SerialNumber);
                else
                    log.LogDebug("register refused again: serial {Serial}", req.SerialNumber);
                return Results.Conflict(new { error = "serial already registered to other hardware; ask IT to reset this device in the portal" });
            }
            var device = await store.FindByTokenHashAsync(DeviceTokens.Hash(token), ct);
            log.LogInformation("device {Serial} {Outcome}", req.SerialNumber, outcome);
            return Results.Ok(new RegisterResponse(device!.Id.ToString(), token));
        }).RequireRateLimiting(RegisterRateLimit).WithMetadata(new RequestSizeLimitAttribute(MaxRegisterBytes));

        api.MapGet("/settings", async (HttpContext http, PortalStore store, DeviceAuthGuard guard, CancellationToken ct) =>
        {
            if (guard.Blocked(http)) return TooManyFailures();
            if (await AuthenticateAsync(http, store, guard, ct) is not { } device) return Results.Unauthorized();
            if (device.IsDeregistered) return Deregistered();
            var etag = $"\"{device.Settings.Revision}\"";
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.Contains(etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            // Holiday periods only matter with a holiday setting, and school hours with an advanced schedule.
            // Read after the device: changing either bumps its revision in the same transaction, so the
            // content served is never older than its revision.
            IReadOnlyList<HolidayPeriod>? periods = null;
            BoundsPolicy? policy = null;
            if (device.Settings.HolidayMode != ScheduleMode.School || device.Settings.Week is not null)
                policy = await store.GetPolicyAsync(ct);
            if (device.Settings.HolidayMode != ScheduleMode.School)
                periods = HolidayPeriods.From(policy!.Holidays, policy.SchoolHours);
            return Results.Json(SettingsPayload.From(device.Settings, periods, policy?.SchoolHours), Wire.Options);
        });

        api.MapPost("/checkin", async (HttpContext http, PortalStore store, DeviceAuthGuard guard, CancellationToken ct) =>
        {
            if (guard.Blocked(http)) return TooManyFailures();
            if (await AuthenticateAsync(http, store, guard, ct) is not { } device) return Results.Unauthorized();
            if (device.IsDeregistered) return Deregistered();
            CheckIn? report;
            try
            {
                report = await http.Request.ReadFromJsonAsync<CheckIn>(Wire.Options, ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "malformed check-in" });
            }
            if (report is null || report.Users is null || report.Schedules is null || report.Users.Length > 50)
                return Results.BadRequest(new { error = "malformed check-in" });
            await store.RecordCheckInAsync(device.Id, report, ct);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaxCheckInBytes));
    }

    /// <summary>
    /// 410 Gone: IT de-registered this Mac. Distinct from 401 (which makes
    /// lulld register again): lulld drops its portal settings, token and
    /// enrolment secret, and falls back to its profile.
    /// </summary>
    static IResult Deregistered() =>
        Results.Json(new { error = "this Mac was de-registered by IT; enrol it again to manage it from the portal" },
            statusCode: StatusCodes.Status410Gone);

    /// <summary>The Mac this request's token belongs to, or null (counted against the client by <paramref name="guard"/>).</summary>
    static async Task<DeviceRecord?> AuthenticateAsync(HttpContext http, PortalStore store, DeviceAuthGuard guard, CancellationToken ct)
    {
        var device = DeviceTokens.FromHeader(http.Request.Headers.Authorization) is { } token
            ? await store.FindByTokenHashAsync(DeviceTokens.Hash(token), ct)
            : null;
        if (device is null) guard.Failed(http);
        return device;
    }

    static IResult TooManyFailures() =>
        Results.Json(new { error = "too many requests with unknown device tokens; try again in a minute" },
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>Request values going into logs: short and printable only.</summary>
    static string Clean(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Take(64).ToArray());
}
