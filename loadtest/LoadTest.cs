#:property PublishAot=false

// Load test for the device API: registers many fake Macs, then has them all
// fetch settings and check in at once, as a school's Macs do when downtime
// starts. Run from the repository root (it reads contract/fixtures/checkin.json):
//
//   LULL_ENROLMENT_SECRET=... dotnet run loadtest/LoadTest.cs -- --url https://portal.example --macs 3000
//
// Options (defaults in brackets):
//   --url            the portal [http://localhost:8080]
//   --macs           how many fake Macs [3000]
//   --rounds         bursts of settings + check-in, one after another [3]
//   --pause          seconds between bursts [5]
//   --prefix         serial prefix, so the fake Macs are easy to find and delete [LOADTEST]
//   --concurrency    registrations in flight at once [50]
//   --retry-seconds  wait after "too many requests" before registering again, as lulld does [60]
//   --secret         the enrolment secret (better: LULL_ENROLMENT_SECRET, which stays out of shell history)
//
// Device tokens are saved to <prefix>.tokens.json in the current folder, so a
// second run skips registration and goes straight to the bursts.
//
// Use a test portal if you can: the fake Macs stay under Devices until deleted, and
// registering adds audit log entries. It prints the command that deletes them when
// it finishes. At the default rate limit, 3,000 Macs take about 10 minutes to
// register; raise LULL_REGISTER_RATE_LIMIT_PER_MINUTE on the test portal to go faster.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var opt = Options.Parse(args);
if (opt is null) return 2;

var fixturePath = Path.Combine("contract", "fixtures", "checkin.json");
if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine($"Can't find {fixturePath}: run this from the repository root.");
    return 2;
}
var checkInTemplate = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();

using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
{
    BaseAddress = new Uri(opt.Url),
    Timeout = TimeSpan.FromSeconds(30),
};

var tokensPath = $"{opt.Prefix}.tokens.json";
var macs = File.Exists(tokensPath)
    ? JsonSerializer.Deserialize<Dictionary<string, Mac>>(File.ReadAllText(tokensPath))!
    : [];
var serials = Enumerable.Range(1, opt.Macs).Select(i => $"{opt.Prefix}{i:D5}").ToList();
var toRegister = serials.Where(s => !macs.ContainsKey(s)).ToList();

Console.WriteLine($"Portal: {opt.Url}");
Console.WriteLine($"Fake Macs: {opt.Macs} ({serials[0]}..{serials[^1]}), {macs.Count} already registered (from {tokensPath})");

// Requests that failed (anything but 200, 304 or 204, or no answer): any makes the exit status 1.
var problems = 0;

// MARK: Registration

if (toRegister.Count > 0)
{
    if (opt.Secret is not { Length: > 0 })
    {
        Console.Error.WriteLine("Registering needs the enrolment secret: set LULL_ENROLMENT_SECRET (or pass --secret).");
        return 2;
    }
    Console.WriteLine($"\nRegistering {toRegister.Count} Macs, {opt.Concurrency} at a time...");
    var watch = Stopwatch.StartNew();
    var latencies = new List<double>();
    int limited = 0, conflicts = 0, failed = 0;
    var gate = new SemaphoreSlim(opt.Concurrency);
    var tasks = toRegister.Select(async serial =>
    {
        while (true)
        {
            await gate.WaitAsync();
            HttpResponseMessage? res = null;
            var t = Stopwatch.StartNew();
            try
            {
                res = await http.PostAsJsonAsync("/api/device/v1/register", new
                {
                    serialNumber = serial,
                    hardwareUUID = DeterministicUuid(serial),
                    enrolmentSecret = opt.Secret,
                    version = "loadtest",
                });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"  {serial}: {e.Message}");
            }
            finally
            {
                gate.Release();
            }
            lock (latencies) latencies.Add(t.Elapsed.TotalMilliseconds);
            if (res?.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Interlocked.Increment(ref limited);
                await Task.Delay(TimeSpan.FromSeconds(opt.RetrySeconds)); // lulld waits a poll interval, then tries again
                continue;
            }
            if (res?.StatusCode == HttpStatusCode.OK)
            {
                var body = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
                lock (macs) macs[serial] = new Mac((string)body["deviceId"]!, (string)body["deviceToken"]!);
            }
            else if (res?.StatusCode == HttpStatusCode.Conflict) Interlocked.Increment(ref conflicts);
            else
            {
                Interlocked.Increment(ref failed);
                if (res is not null) Console.Error.WriteLine($"  {serial}: HTTP {(int)res.StatusCode}");
            }
            return;
        }
    }).ToList();
    using (var progress = new Timer(_ => Console.WriteLine($"  ... {macs.Count}/{opt.Macs} registered, {limited} rate-limited so far ({watch.Elapsed:mm\\:ss})"),
        null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)))
        await Task.WhenAll(tasks);
    File.WriteAllText(tokensPath, JsonSerializer.Serialize(macs));
    Console.WriteLine($"Registered {toRegister.Count - conflicts - failed} in {watch.Elapsed:mm\\:ss}: {limited} \"too many requests\" (retried), " +
        $"{conflicts} already registered (409; their tokens are lost: delete them or use another --prefix), {failed} failed.");
    problems += conflicts + failed;
    Report("register", latencies);
}

// MARK: Bursts

// Only the Macs asked for (--macs), even if the tokens file holds more.
var fleet = serials.Where(macs.ContainsKey).ToDictionary(s => s, s => macs[s]);
var etags = new Dictionary<string, (string ETag, long Revision)>();
for (int round = 1; round <= opt.Rounds; round++)
{
    if (round > 1) await Task.Delay(TimeSpan.FromSeconds(opt.Pause));
    Console.WriteLine($"\nBurst {round} of {opt.Rounds}: {fleet.Count} Macs fetch settings and check in, all at once...");
    var settings = new List<double>();
    var checkIns = new List<double>();
    var statuses = new Dictionary<string, int>();
    void Count(string what) { lock (statuses) statuses[what] = statuses.GetValueOrDefault(what) + 1; }

    var watch = Stopwatch.StartNew();
    await Task.WhenAll(fleet.Select(async pair =>
    {
        var (serial, mac) = pair;
        try
        {
            // GET settings, with the ETag from the last burst (304 when nothing changed), as lulld does.
            using var get = new HttpRequestMessage(HttpMethod.Get, "/api/device/v1/settings");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mac.Token);
            (string ETag, long Revision) known;
            lock (etags) etags.TryGetValue(serial, out known);
            if (known.ETag is not null) get.Headers.TryAddWithoutValidation("If-None-Match", known.ETag);
            var t = Stopwatch.StartNew();
            using var res = await http.SendAsync(get);
            var body = res.StatusCode == HttpStatusCode.OK ? await res.Content.ReadFromJsonAsync<JsonObject>() : null;
            lock (settings) settings.Add(t.Elapsed.TotalMilliseconds);
            Count($"settings {(int)res.StatusCode}");
            if (body is not null && res.Headers.ETag is { } etag)
                lock (etags) known = etags[serial] = (etag.Tag, (long)body["revision"]!);
            if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.NotModified) return;

            // POST the check-in: the contract fixture, as this Mac.
            var report = checkInTemplate.DeepClone().AsObject();
            report["users"] = new JsonArray(new JsonObject
            {
                ["username"] = $"{opt.Prefix.ToLowerInvariant()}{serial[opt.Prefix.Length..]}",
                ["exempt"] = false,
                ["agentConnected"] = true,
            });
            report["settingsRevision"] = known.ETag is null ? null : known.Revision;
            report["reportedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var post = new HttpRequestMessage(HttpMethod.Post, "/api/device/v1/checkin")
            {
                Content = new StringContent(report.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mac.Token);
            t.Restart();
            using var sent = await http.SendAsync(post);
            lock (checkIns) checkIns.Add(t.Elapsed.TotalMilliseconds);
            Count($"checkin {(int)sent.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Count($"error ({(e.InnerException ?? e).Message})");
        }
    }));
    var total = settings.Count + checkIns.Count;
    Console.WriteLine($"Done in {watch.Elapsed.TotalSeconds:F1} s: {total} requests ({total / watch.Elapsed.TotalSeconds:F0}/s).");
    Console.WriteLine("  " + string.Join(", ", statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}: {s.Value}")));
    Report("settings", settings);
    Report("checkin", checkIns);
    problems += statuses.Where(s => s.Key is not ("settings 200" or "settings 304" or "checkin 204")).Sum(s => s.Value);
}

Console.WriteLine($"""

    The fake Macs stay registered (IT sees them under Devices) until you delete them. On the server:
      docker compose exec db psql -U lull -d lull -c "DELETE FROM audit_log WHERE device_id IN (SELECT id FROM devices WHERE serial_number LIKE '{opt.Prefix}%'); DELETE FROM devices WHERE serial_number LIKE '{opt.Prefix}%';"
    then delete {tokensPath} here.
    """);
if (problems > 0) Console.WriteLine($"{problems} requests failed (see above).");
return problems > 0 ? 1 : 0;

static void Report(string what, List<double> ms)
{
    if (ms.Count == 0) return;
    ms.Sort();
    double P(double p) => ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(p * ms.Count) - 1)];
    Console.WriteLine($"  {what,-9} {ms.Count,6} requests   p50 {P(0.50),7:F0} ms   p95 {P(0.95),7:F0} ms   p99 {P(0.99),7:F0} ms   max {ms[^1],7:F0} ms");
}

// The same hardware UUID for a serial on every run.
static string DeterministicUuid(string serial) =>
    new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(serial))).ToString().ToUpperInvariant();

record Mac(string DeviceId, string Token);

sealed record Options(string Url, int Macs, int Rounds, int Pause, string Prefix, int Concurrency, int RetrySeconds, string? Secret)
{
    public static Options? Parse(string[] args)
    {
        var o = new Options("http://localhost:8080", 3000, 3, 5, "LOADTEST", 50, 60, Environment.GetEnvironmentVariable("LULL_ENROLMENT_SECRET"));
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            int Number() => int.TryParse(Next(), out var n) && n > 0 ? n : throw new ArgumentException($"{args[i - 1]} needs a positive number");
            try
            {
                o = args[i] switch
                {
                    "--url" => o with { Url = Next().TrimEnd('/') },
                    "--macs" => o with { Macs = Number() },
                    "--rounds" => o with { Rounds = Number() },
                    "--pause" => o with { Pause = Number() },
                    "--prefix" => o with { Prefix = Next() },
                    "--concurrency" => o with { Concurrency = Number() },
                    "--retry-seconds" => o with { RetrySeconds = Number() },
                    "--secret" => o with { Secret = Next() },
                    _ => throw new ArgumentException($"unknown option {args[i]}"),
                };
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"{e.Message} (see the top of loadtest/LoadTest.cs)");
                return null;
            }
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.Prefix, "^[A-Za-z0-9]{1,40}$"))
        {
            Console.Error.WriteLine("--prefix must be 1-40 letters or digits");
            return null;
        }
        return o;
    }
}
