using System.Security.Cryptography;
using System.Text;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace Lull.Portal.Data;

/// <summary>
/// The database schema, as numbered SQL scripts in Data/Migrations (embedded
/// in the assembly) that DbUp applies in name order. Each runs once, in its own
/// transaction, and is recorded in <c>schema_migrations</c>. Migrating is a
/// deployment step (<c>--migrate</c>, the compose "migrate" service), not
/// something the web app does as it starts: the app only checks the database
/// is up to date, so a failed migration stops the deploy, not the running portal.
/// </summary>
public static class Migrator
{
    const long LockKey = 0x4c756c6c; // "Lull"
    const string Journal = "schema_migrations";
    const string Prefix = "Lull.Portal.Data.Migrations.";

    /// <summary>Whether the database matches this build's scripts.</summary>
    /// <param name="Pending">Scripts not yet applied.</param>
    /// <param name="Unknown">Applied scripts this build doesn't have: a newer portal migrated the database.</param>
    public sealed record Status(IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown)
    {
        public bool IsUpToDate => Pending.Count == 0 && Unknown.Count == 0;
    }

    static UpgradeEngine Engine(string connectionString, ILoggerFactory? logs)
    {
        var builder = DeployChanges.To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.StartsWith(Prefix) && name.EndsWith(".sql"))
            .JournalToPostgresqlTable("public", Journal)
            .WithTransactionPerScript()
            // Otherwise DbUp substitutes $name$ in scripts, which would corrupt Postgres $$ quoting.
            .WithVariablesDisabled();
        return (logs is null ? builder.LogToNowhere() : builder.LogTo(logs)).Build();
    }

    /// <summary>Applies the pending scripts, returning their names. Throws if one fails (its transaction is rolled back).</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(string connectionString, ILoggerFactory? logs = null, CancellationToken ct = default)
    {
        // A session advisory lock on a connection of our own: two migrators
        // started at once (two deploys, or a retried job) take turns, and the
        // second finds nothing to do. DbUp itself doesn't lock.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            acquire.Parameters.AddWithValue("k", LockKey);
            await acquire.ExecuteNonQueryAsync(ct);
        }
        try
        {
            var result = Engine(connectionString, logs).PerformUpgrade();
            if (!result.Successful)
                throw new InvalidOperationException($"Migration {result.ErrorScript?.Name} failed: {result.Error?.Message}", result.Error);
            return result.Scripts.Select(s => s.Name).ToList();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            release.Parameters.AddWithValue("k", LockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>The portal's database user: data only, no schema changes.</summary>
    public const string AppRole = "lull_app";

    /// <summary>
    /// Creates <paramref name="role"/> if it's missing and sets its password.
    /// Roles belong to the whole Postgres server, not one database, so this is
    /// part of every deploy rather than a migration script: a database restored
    /// onto a new server gets its user back. Needs the owner's connection.
    /// </summary>
    public static async Task EnsureRoleAsync(string ownerConnectionString, string password, string role = AppRole, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);
        // Two deploys at once would both see the role missing; the loser's
        // duplicate is harmless. Role names can't be parameters, hence Quote.
        await using (var create = new NpgsqlCommand(
            $"""
            DO $$ BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role.Replace("'", "''")}') THEN CREATE ROLE {Quote(role)} LOGIN; END IF;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL;
            END $$
            """, conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }
        // The password goes over as a SCRAM verifier, never in plain text, so
        // it can't end up in the Postgres log (as psql's \password does).
        await using var alter = new NpgsqlCommand($"ALTER ROLE {Quote(role)} LOGIN PASSWORD '{ScramVerifier(password)}'", conn);
        await alter.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gives <paramref name="role"/> read and write access to every table in
    /// this database, read-only access to the migrations journal, and no UPDATE
    /// on the audit log: entries are added, and old ones deleted (retention), but
    /// never changed. Run after migrating, so tables a migration just added are included.
    /// </summary>
    public static async Task GrantDataAccessAsync(string ownerConnectionString, string role = AppRole, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);
        var r = Quote(role);
        await using var grant = new NpgsqlCommand(
            $"""
            GRANT USAGE ON SCHEMA public TO {r};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {r};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {r};
            REVOKE INSERT, UPDATE, DELETE ON {Journal} FROM {r};
            REVOKE UPDATE ON audit_log FROM {r};
            """, conn);
        await grant.ExecuteNonQueryAsync(ct);
    }

    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// A Postgres SCRAM-SHA-256 password verifier (RFC 5802/7677), in the form
    /// Postgres stores: SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey.
    /// The password's UTF-8 bytes are used as they are, as Npgsql sends them.
    /// </summary>
    internal static string ScramVerifier(string password, int iterations = 4096)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        static byte[] Hmac(byte[] key, string text) =>
            HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(text));
        var storedKey = SHA256.HashData(Hmac(salted, "Client Key"));
        var serverKey = Hmac(salted, "Server Key");
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    public static Status Check(string connectionString)
    {
        var engine = Engine(connectionString, null);
        return new Status(engine.GetScriptsToExecute().Select(s => s.Name).ToList(), engine.GetExecutedButNotDiscoveredScripts());
    }

    /// <summary>One script's SQL, by file name (e.g. <c>0001_baseline.sql</c>).</summary>
    public static string Script(string fileName)
    {
        using var stream = typeof(Migrator).Assembly.GetManifestResourceStream(Prefix + fileName)
            ?? throw new ArgumentException($"No migration script {fileName}.", nameof(fileName));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
