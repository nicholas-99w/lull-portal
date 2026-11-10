using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lull.Portal.Api;

namespace Lull.Portal.Tests.Integration;

public class DeviceApiTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task HealthCheckReachesTheDatabase()
    {
        var res = await portal.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task NewMacGetsSchoolDefaultsAndETagsWork()
    {
        var (mac, _) = await portal.RegisterMacAsync("C02DEFAULTS1");
        var res = await mac.GetAsync("/api/device/v1/settings");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var s = (await res.Content.ReadFromJsonAsync<SettingsPayload>(Wire.Options))!;
        Assert.True(s.Enabled);
        Assert.Null(s.SchoolNights); // follows the school's (Jamf profile's) schedule
        Assert.Null(s.Weekend);
        Assert.Equal(portal.Clock.Now.ToUnixTimeMilliseconds(), s.Revision);

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/device/v1/settings");
        again.Headers.IfNoneMatch.Add(res.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await mac.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task OldDefaultsWrittenBySystemMoveToFollowingTheSchool()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02MIGRATE01");
        var db = portal.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
        // What an older portal wrote for a new Mac: its own default times, by "system".
        await using (var cmd = db.CreateCommand(
            "UPDATE device_settings SET school_start = '21:30', school_end = '06:30', weekend_start = '23:00', weekend_end = '08:00', updated_by = 'system' WHERE device_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", Guid.Parse(id));
            await cmd.ExecuteNonQueryAsync();
        }
        var before = await PortalFactory.SettingsAsync(mac);
        Assert.NotNull(before.SchoolNights);

        await RerunBaselineAsync(portal.ConnectionString); // as on a database an older portal created
        var after = await PortalFactory.SettingsAsync(mac);
        Assert.Null(after.SchoolNights);
        Assert.Null(after.Weekend);
        Assert.True(after.Revision > before.Revision, "a new revision, or lulld would refuse the change");
        await RerunBaselineAsync(portal.ConnectionString);
        Assert.Equal(after.Revision, (await PortalFactory.SettingsAsync(mac)).Revision); // a second run changes nothing
    }

    [Fact]
    public async Task TheOldAllOrNothingOptOutBecomesBothKindsOff()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02MIGRATE02");
        var db = portal.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
        await using (var cmd = db.CreateCommand("UPDATE device_settings SET enabled = false, school_start = '21:30', school_end = '06:30' WHERE device_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", Guid.Parse(id));
            await cmd.ExecuteNonQueryAsync();
        }
        var before = await PortalFactory.SettingsAsync(mac);
        Assert.False(before.Enabled);

        await RerunBaselineAsync(portal.ConnectionString);
        var after = await PortalFactory.SettingsAsync(mac);
        Assert.True(after.Enabled);
        Assert.Equal(true, after.SchoolNightsOff);
        Assert.Equal(true, after.WeekendOff);
        Assert.Null(after.SchoolNights);
        Assert.True(after.Revision > before.Revision);
    }

    /// <summary>
    /// The baseline migration's data fixes convert what older portals wrote. A
    /// database those portals created gets the baseline applied on top of it.
    /// </summary>
    static async Task RerunBaselineAsync(string ownerConnectionString)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync();
        await new Npgsql.NpgsqlCommand(Lull.Portal.Data.Migrator.Script("0001_baseline.sql"), conn).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RegistrationNeedsTheSecretAndIsOncePerSerial()
    {
        var anon = portal.CreateClient();
        var wrong = await anon.PostAsJsonAsync("/api/device/v1/register", new RegisterRequest("C02ONCE00001", "HW-1", "wrong-secret-wrong-secret", null));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var (first, _) = await portal.RegisterMacAsync("C02ONCE00001");
        var dup = await anon.PostAsJsonAsync("/api/device/v1/register", new RegisterRequest("C02ONCE00001", "HW-2", PortalFactory.Secret, null));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        // The original Mac is unaffected by the refused attempt.
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/device/v1/settings")).StatusCode);

        var bad = await anon.PostAsJsonAsync("/api/device/v1/register", new RegisterRequest("bad serial!", "HW-3", PortalFactory.Secret, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task UnknownTokensAreRefused()
    {
        var mac = portal.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);
        mac.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mac.PostAsJsonAsync("/api/device/v1/checkin", new { })).StatusCode);
    }

    /// <summary>
    /// Kestrel refuses a bigger body with 413 before it's read. The test server doesn't enforce
    /// the limit, so this checks each endpoint asks for it.
    /// </summary>
    [Theory]
    [InlineData("api/device/v1/register", 4 * 1024)]
    [InlineData("api/device/v1/checkin", 64 * 1024)]
    public void UnauthenticatedAndDeviceBodiesHaveASizeLimit(string route, long bytes)
    {
        var endpoints = portal.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;
        var endpoint = Assert.Single(endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>(), e => e.RoutePattern.RawText?.TrimStart('/') == route);
        Assert.Equal(bytes, endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task MalformedCheckInIsRejected()
    {
        var (mac, _) = await portal.RegisterMacAsync("C02MALFORM01");
        var res = await mac.PostAsync("/api/device/v1/checkin", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

/// <summary>The portal allowing 3 unknown device tokens per client per minute.</summary>
public class FewDeviceAuthFailuresPortalFactory : PortalFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lull:DeviceAuthFailuresPerMinute", "3");
    }
}

public class DeviceAuthGuardTests(FewDeviceAuthFailuresPortalFactory portal) : IClassFixture<FewDeviceAuthFailuresPortalFactory>
{
    [Fact]
    public async Task AFloodOfMadeUpTokensIsRefusedBeforeTheDatabase()
    {
        var (mac, _) = await portal.RegisterMacAsync("C02FLOOD0001");
        Assert.Equal(HttpStatusCode.OK, (await mac.GetAsync("/api/device/v1/settings")).StatusCode); // a real Mac doesn't count
        var guesser = portal.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "made-up-token");
        for (int i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/api/device/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.GetAsync("/api/device/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.PostAsJsonAsync("/api/device/v1/checkin", new { })).StatusCode);
        // The limit is per client, and the test's clients all share one (no) address: the real Mac waits too, for the rest of the minute.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await mac.GetAsync("/api/device/v1/settings")).StatusCode);
    }
}
