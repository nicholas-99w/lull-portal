using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lull.Portal.Api;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Lull.Portal.Tests.Integration;

/// <summary>Test clock the tests can move.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>What the portal logged about failed sign-ins (<see cref="SignInLog"/>).</summary>
public sealed class SignInLogCapture : ILoggerProvider
{
    readonly ConcurrentQueue<string> lines = new();
    public IEnumerable<string> Lines => lines;
    public ILogger CreateLogger(string category) => category == SignInLog.Category ? new Capture(lines) : NullLogger.Instance;
    public void Dispose() { }

    sealed class Capture(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}

/// <summary>
/// The real app against a fresh Postgres database per test class. Postgres
/// comes from LULL_TEST_POSTGRES (./test.sh starts one in Docker).
/// </summary>
public class PortalFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Secret = "integration-test-enrolment-secret";
    public const string AdminEmail = "it@school.test";
    public const string AdminPassword = "Correct-Horse-Battery";
    public const string ParentPassword = "Parent-Pass-1";
    /// <summary>The portal's database user's password in tests (the role is shared by every test database).</summary>
    public const string AppPassword = "test-app-db-password";
    static readonly string admin = Environment.GetEnvironmentVariable("LULL_TEST_POSTGRES")
        ?? throw new InvalidOperationException("LULL_TEST_POSTGRES is not set: run ./test.sh, which starts Postgres in Docker.");
    // Once per test run: roles belong to the whole server, and concurrent
    // ALTER ROLEs from parallel test classes can fail with "tuple concurrently updated".
    static readonly Lazy<Task> appRole = new(() => Migrator.EnsureRoleAsync(admin, AppPassword));
    readonly string database = "lull_test_" + Guid.NewGuid().ToString("N");
    public TestClock Clock { get; } = new();
    public SignInLogCapture SignInLogs { get; } = new();

    /// <summary>The owner, as the migrate step connects. For test set-up that changes the schema.</summary>
    public string ConnectionString => new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;

    /// <summary>The portal's own user (data only), as in production.</summary>
    string AppConnectionString => new NpgsqlConnectionStringBuilder(admin)
    { Database = database, Username = Migrator.AppRole, Password = AppPassword }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {database}", conn).ExecuteNonQueryAsync();
        }
        await appRole.Value;
        await PrepareDatabaseAsync();
        // Most tests want a set-up portal; FirstRunTests start from an empty one.
        if (CreatesOwner)
        {
            var owner = await Users.CreateOwnerAsync(AdminEmail, "IT", AdminPassword, default);
            Assert.NotNull(owner);
            await EnableTwoFactorAsync(owner.Id);
        }
    }

    /// <summary>Each account's two-step key, once a test (or set-up) turned it on, by email.</summary>
    public Dictionary<string, byte[]> TwoFactorKeys { get; } = [];

    /// <summary>Turns on two-step sign-in for an account, as the /TwoFactor page would, and keeps its key.</summary>
    public async Task EnableTwoFactorAsync(Guid id)
    {
        var key = Totp.NewSecret();
        var user = await Users.EnableTwoFactorAsync(id, key, Totp.Code(key, Totp.StepAt(Clock.Now)), default);
        Assert.NotNull(user);
        TwoFactorKeys[user.Email] = key;
    }

    /// <summary>
    /// A code for the account's current step that it will accept. Tests sign in as the same admin
    /// many times within one 30-second step of the test clock, which a real code wouldn't survive
    /// (each works once), so the record of the last step used is cleared first.
    /// </summary>
    public async Task<string> TwoFactorCodeAsync(string email)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE users SET totp_last_step = NULL WHERE email = @e", conn);
        cmd.Parameters.AddWithValue("e", email);
        await cmd.ExecuteNonQueryAsync();
        return Totp.Code(TwoFactorKeys[email], Totp.StepAt(Clock.Now));
    }

    /// <summary>
    /// Sets up two-step sign-in on the /TwoFactor page, as a person would: reads the key it shows,
    /// and answers with a code from it. Keeps the key; returns the response to the code.
    /// </summary>
    public async Task<HttpResponseMessage> SetUpTwoFactorAsync(HttpClient browser, string email)
    {
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync("/TwoFactor"));
        var keyText = Regex.Match(page, "<code class=\"big\">([A-Z2-7 ]+)</code>").Groups[1].Value;
        var pending = Regex.Match(page, "name=\"pending\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(keyText);
        var key = Base32Decode(keyText.Replace(" ", ""));
        TwoFactorKeys[email] = key;
        return await PostFormAsync(browser, "/TwoFactor", new() { ["pending"] = pending, ["code"] = Totp.Code(key, Totp.StepAt(Clock.Now)) });
    }

    static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = buffer << 5 | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The signed ticket the sign-in page carries from the password to the code step, or "" if none.</summary>
    public static string TicketOnPage(string html) =>
        Regex.Match(html, "name=\"ticket\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>The deployment step: the app refuses to start against an unmigrated database.</summary>
    protected virtual async Task PrepareDatabaseAsync()
    {
        await Migrator.RunAsync(ConnectionString);
        await Migrator.GrantDataAccessAsync(ConnectionString);
    }

    /// <summary>Whether to create the owner admin (as the first-run page would) before the tests.</summary>
    protected virtual bool CreatesOwner => true;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(admin);
        await conn.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", conn).ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (not ConfigureAppConfiguration): Program reads these before Build().
        builder.UseSetting("Lull:ConnectionString", AppConnectionString);
        builder.UseSetting("Lull:EnrolmentSecret", Secret);
        builder.UseSetting("Lull:PasswordIterations", "1000"); // production uses 600,000
        builder.UseSetting("Lull:RateLimitPerMinute", "10000");
        // Every test request comes from the same (no) address, so this would count them all together.
        builder.UseSetting("Lull:DeviceAuthFailuresPerMinute", "10000");
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddSingleton<ILoggerProvider>(SignInLogs);
        });
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // MARK: Acting as a Mac

    /// <summary>Registers a Mac; a new hardware UUID unless one is given (the same Mac again).</summary>
    public async Task<(HttpClient Mac, string DeviceId)> RegisterMacAsync(string serial, string? hardwareUuid = null)
    {
        var mac = CreateClient();
        var res = await mac.PostAsJsonAsync("/api/device/v1/register",
            new RegisterRequest(serial, hardwareUuid ?? Guid.NewGuid().ToString(), Secret, "0.2.0"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<RegisterResponse>())!;
        mac.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.DeviceToken);
        return (mac, body.DeviceId);
    }

    /// <summary>
    /// The shared fixture check-in, with the given first user and applied revision, and any other <paramref name="change"/>.
    /// Its <c>nextTransition</c> follows the phase and window the change sets (the window's end while locked, else its
    /// start). The fixture's own window is on 15 Oct, whatever the test clock says, so with it nothing is due: a check-in
    /// can't become overdue unless the test gives it a window or a <c>nextTransition</c>.
    /// </summary>
    public static async Task CheckInAsync(HttpClient mac, string username, long? revision, bool enabled = true, Action<JsonObject>? change = null)
    {
        var node = Fixtures.Node("checkin.json");
        node["users"] = new JsonArray(new JsonObject { ["username"] = username, ["exempt"] = false, ["agentConnected"] = true });
        node["settingsRevision"] = revision;
        node["enabled"] = enabled;
        if (!enabled) node["phase"] = "disabled";
        var (start, end, next) = (node["windowStart"]?.ToJsonString(), node["windowEnd"]?.ToJsonString(), node["nextTransition"]?.ToJsonString());
        change?.Invoke(node.AsObject());
        if (node["nextTransition"]?.ToJsonString() == next)
            node["nextTransition"] = node["windowStart"]?.ToJsonString() == start && node["windowEnd"]?.ToJsonString() == end
                ? null
                : (string?)node["phase"] == "locked" ? node["windowEnd"]?.DeepClone() : node["windowStart"]?.DeepClone();
        var res = await mac.PostAsync("/api/device/v1/checkin", new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    public static async Task<SettingsPayload> SettingsAsync(HttpClient mac) =>
        (await mac.GetFromJsonAsync<SettingsPayload>("/api/device/v1/settings", Wire.Options))!;

    // MARK: Acting as a person in a browser

    public static string Antiforgery(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Signs in with a password, and the two-step code too if the account has it.</summary>
    public async Task<HttpClient> SignInAsync(string email, string password)
    {
        var browser = Browser();
        var res = await PasswordAndCodeAsync(browser, email, password);
        Assert.True(res.StatusCode == HttpStatusCode.Redirect, $"sign-in as {email} failed");
        return browser;
    }

    /// <summary>The sign-in form, then the code form if the page asks for one.</summary>
    public async Task<HttpResponseMessage> PasswordAndCodeAsync(HttpClient browser, string email, string password)
    {
        // ?local=true: the form is there even with local sign-in off (for the owner).
        var res = await PostFormAsync(browser, "/Login", new() { ["email"] = email, ["password"] = password }, formPage: "/Login?local=true");
        if (res.StatusCode != HttpStatusCode.OK || TicketOnPage(await res.Content.ReadAsStringAsync()) is not { Length: > 0 } ticket)
            return res;
        return await PostFormAsync(browser, "/Login?handler=Code",
            new() { ["ticket"] = ticket, ["code"] = await TwoFactorCodeAsync(email) }, formPage: "/Login?local=true");
    }

    public Task<HttpClient> SignInAdminAsync() => SignInAsync(AdminEmail, AdminPassword);

    public UserStore Users => Services.GetRequiredService<UserStore>();

    /// <summary>
    /// A parent account <c>{name}@parents.test</c> with these students, set up
    /// the way a real one is (created, setup code, password), signed in. Calling it
    /// again for the same name replaces the students.
    /// </summary>
    public async Task<HttpClient> ParentAsync(string name, params string[] students)
    {
        var email = $"{name}@parents.test";
        if (await Users.FindByEmailAsync(email, default) is { } existing)
            await Users.UpdateAsync(existing.Id, email, name, Roles.Parent, students, existing.Id, "test", default);
        else
        {
            var (_, code) = await Users.CreateAsync(email, name, Roles.Parent, students, "test", default);
            Assert.Equal(SignInOutcome.Ok, (await Users.CompleteSetupAsync(email, code, ParentPassword, default)).Outcome);
        }
        return await SignInAsync(email, ParentPassword);
    }

    /// <summary>
    /// GETs the page for its antiforgery token, then POSTs the form, as a browser would: with the
    /// settings revision the form carries (the form posting to <paramref name="path"/>, else the
    /// page's first), unless the test gives one.
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string path, Dictionary<string, string> fields, string? formPage = null)
    {
        var page = await browser.GetStringAsync(formPage ?? path);
        fields = new(fields) { ["__RequestVerificationToken"] = Antiforgery(page) }; // the caller's may be posted again
        if (!fields.ContainsKey("revision") && RevisionOnPage(page, path) is { } revision) fields["revision"] = revision;
        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static string? RevisionOnPage(string html, string path)
    {
        var form = Regex.Match(html, $"<form[^>]*action=\"{Regex.Escape(path)}\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
        var match = Regex.Match(form.Success ? form.Groups[1].Value : html, "name=\"revision\" value=\"(\\d+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}
