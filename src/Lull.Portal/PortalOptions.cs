namespace Lull.Portal;

/// <summary>
/// Settings, bound from the "Lull" configuration section. In Docker they come
/// from environment variables such as <c>Lull__ConnectionString</c>
/// (see .env.example); nothing is baked into the image.
/// </summary>
public sealed class PortalOptions
{
    public const string Section = "Lull";

    /// <summary>Npgsql connection string.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// The database password, kept out of <see cref="ConnectionString"/> so it
    /// may contain any character (a ';' or quote pasted into a connection
    /// string breaks it). Overrides any password in the connection string.
    /// </summary>
    public string? DatabasePassword { get; set; }

    /// <summary>
    /// Read by <c>--migrate</c> only: the password it gives the portal's own
    /// database user (<see cref="Data.Migrator.AppRole"/>), which can read and
    /// write data but not change the schema. The portal then connects as that
    /// user. Unset: no app user is set up, and the portal must use the owner.
    /// </summary>
    public string? AppDatabasePassword { get; set; }

    /// <summary>
    /// The connection string to use: <see cref="ConnectionString"/> with
    /// <see cref="DatabasePassword"/> set through Npgsql's builder (which quotes
    /// it correctly). Throws <see cref="InvalidOperationException"/> with a
    /// message that never contains the password.
    /// </summary>
    public string EffectiveConnectionString()
    {
        Npgsql.NpgsqlConnectionStringBuilder b;
        try
        {
            b = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "Lull__ConnectionString isn't a valid connection string. A value containing ';' or quotes breaks it: " +
                "put the database password in Lull__DatabasePassword instead (compose.yml does this from POSTGRES_PASSWORD).");
        }
        if (!string.IsNullOrEmpty(DatabasePassword)) b.Password = DatabasePassword;
        // The slim .NET image has no Kerberos library, so Npgsql's default probe
        // for GSS encryption only logs "Cannot load library libgssapi_krb5.so.2".
        // Off unless the connection string asks for it.
        if (!ConnectionString.Contains("gss", StringComparison.OrdinalIgnoreCase)) b.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        return b.ConnectionString;
    }

    /// <summary>
    /// Shared secret a Mac presents once, to register. Deployed to Macs by a
    /// Jamf policy into a root-only file, never in the (user-readable) profile.
    /// </summary>
    public string EnrolmentSecret { get; set; } = "";

    /// <summary>PBKDF2 iterations for new password hashes (tests lower it for speed).</summary>
    public int PasswordIterations { get; set; } = Auth.PasswordHasher.DefaultIterations;

    /// <summary>Behind a reverse proxy that sets X-Forwarded-For/-Proto.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// With <see cref="TrustForwardedHeaders"/>: the proxies whose headers count, as addresses or
    /// CIDR ranges, comma-separated ("10.0.0.5, 172.16.0.0/12"). Empty: private and loopback
    /// addresses only (<see cref="PrivateNetworks"/>), which covers a proxy on the same host or
    /// network, while a forged header from the internet is ignored.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>Where a proxy in front of the portal connects from, unless <see cref="TrustedProxies"/> says otherwise.</summary>
    public static readonly string[] PrivateNetworks =
        ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"];

    /// <summary>The networks whose forwarded headers are trusted; throws <see cref="FormatException"/> for a bad entry.</summary>
    public IReadOnlyList<System.Net.IPNetwork> TrustedProxyNetworks()
    {
        var entries = (TrustedProxies ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        return (entries.Length > 0 ? entries : PrivateNetworks).Select(Network).ToList();
    }

    /// <summary>"172.16.0.0/12", or one address as a network of its own.</summary>
    static System.Net.IPNetwork Network(string entry)
    {
        if (entry.Contains('/')) return System.Net.IPNetwork.Parse(entry);
        var address = System.Net.IPAddress.Parse(entry);
        return new(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    /// <summary>Where ASP.NET Core keeps its cookie-encryption keys (a volume in Docker).</summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>Per client IP, per minute: the sign-in, password setup and first-run pages.</summary>
    public int RateLimitPerMinute { get; set; } = 20;

    /// <summary>
    /// Per client IP, per minute: Mac registration. Higher than sign-in because
    /// a school's Macs register from behind one NAT address, and the enrolment
    /// secret (16+ characters) can't be guessed at any rate.
    /// </summary>
    public int RegisterRateLimitPerMinute { get; set; } = 300;

    /// <summary>
    /// Per client IP, per minute: device API requests whose token matched no Mac, before that
    /// client is refused (429) without a database lookup (<see cref="Api.DeviceAuthGuard"/>).
    /// </summary>
    public int DeviceAuthFailuresPerMinute { get; set; } = 60;

    /// <summary>
    /// Audit log entries older than this many days are deleted, once at start-up
    /// and then daily. 0 keeps them forever.
    /// Only until the owner saves Settings, Sign-in: then <see cref="Data.PortalSettings"/> uses the saved value.
    /// </summary>
    public int AuditRetentionDays { get; set; } = 365;

    /// <summary>
    /// Whether people can sign in with an email and password (local accounts,
    /// created by IT or imported from a CSV). False needs Entra sign-in: then only
    /// the owner (the emergency admin) can still use a password.
    /// Only until the owner saves Settings, Sign-in: then <see cref="Data.PortalSettings"/> uses the saved value.
    /// </summary>
    public bool LocalSignIn { get; set; } = true;

    /// <summary>Sign-in with Entra ID (Microsoft). Off unless configured.</summary>
    public EntraOptions Entra { get; set; } = new();

    public const int MinimumEnrolmentSecretLength = 16;

    /// <summary>A setting that can't work, as a message for the person deploying; null if all is well.</summary>
    public string? Problem()
    {
        if (Entra.Problem() is { } entra) return entra;
        if (!LocalSignIn && !Entra.Enabled) return "Lull__LocalSignIn is false but Entra sign-in isn't set up: nobody but the owner could sign in.";
        try
        {
            TrustedProxyNetworks();
        }
        catch (FormatException)
        {
            return "Lull__TrustedProxies must be IP addresses or CIDR ranges (such as 10.0.0.5 or 172.16.0.0/12), comma-separated.";
        }
        return null;
    }
}

/// <summary>
/// Entra ID sign-in (OpenID Connect), with the school's own single-tenant app
/// registration. Set <see cref="TenantId"/>, <see cref="ClientId"/> and
/// <see cref="ClientSecret"/> to turn it on (docs/entra-id.md).
/// </summary>
public sealed class EntraOptions
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>The ID token claim with the parent's student usernames, comma-separated (e.g. from an extension attribute).</summary>
    public string StudentsClaim { get; set; } = "students";

    /// <summary>The app role (the token's <c>roles</c> claim) that makes someone an admin.</summary>
    public string AdminRole { get; set; } = "Admin";

    /// <summary>
    /// The app role that makes someone a parent. Empty: everyone who can sign in
    /// and isn't an admin is a parent (rely on "Assignment required" in Entra).
    /// </summary>
    public string ParentRole { get; set; } = "Parent";

    /// <summary>
    /// A first Entra sign-in whose email matches a local account takes that account
    /// over (keeping its history and students; its password stops working). For
    /// moving a portal's existing accounts to Entra. Off: such a sign-in is refused
    /// until IT deletes the local account. Never the owner's account.
    /// Only until the owner saves Settings, Sign-in: then <see cref="Data.PortalSettings"/> uses the saved value.
    /// </summary>
    public bool LinkLocalAccountsByEmail { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret);

    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>Where the browser goes to sign in and out (the CSP must allow forms to lead there).</summary>
    public const string LoginOrigin = "https://login.microsoftonline.com";

    public string? Problem()
    {
        if (!Enabled) return null;
        if (!Guid.TryParse(TenantId, out _)) return "Lull__Entra__TenantId must be the directory (tenant) ID, a GUID.";
        if (!Guid.TryParse(ClientId, out _)) return "Lull__Entra__ClientId must be the application (client) ID, a GUID.";
        if (string.IsNullOrWhiteSpace(ClientSecret)) return "Lull__Entra__ClientSecret is not set.";
        if (string.IsNullOrWhiteSpace(StudentsClaim)) return "Lull__Entra__StudentsClaim must name a claim.";
        if (string.IsNullOrWhiteSpace(AdminRole)) return "Lull__Entra__AdminRole must name an app role.";
        return null;
    }
}
