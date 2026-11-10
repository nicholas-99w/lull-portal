using Lull.Portal.Data;
using Npgsql;

namespace Lull.Portal.Tests.Integration;

/// <summary>lull_app, the portal's own database user: data yes, schema no.</summary>
public class DatabaseUserTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    string As(string role = Migrator.AppRole, string password = PortalFactory.AppPassword) =>
        new NpgsqlConnectionStringBuilder(portal.ConnectionString) { Username = role, Password = password, Pooling = false }.ConnectionString;

    async Task<NpgsqlConnection> AsAppAsync(string role = Migrator.AppRole, string password = PortalFactory.AppPassword)
    {
        var conn = new NpgsqlConnection(As(role, password));
        await conn.OpenAsync();
        return conn;
    }

    static Task<int> RunAsync(NpgsqlConnection conn, string sql) => new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();

    [Fact]
    public async Task ItCanReadAndWriteData()
    {
        await using var conn = await AsAppAsync();
        Assert.Equal(1, await RunAsync(conn, "INSERT INTO audit_log (at, actor, action) VALUES (now(), 'test', 'app_can_write')"));
        Assert.Equal(1, await RunAsync(conn, "DELETE FROM audit_log WHERE action = 'app_can_write'")); // retention
        Assert.Equal(1, await RunAsync(conn, $"UPDATE users SET updated_at = now() WHERE email = '{PortalFactory.AdminEmail}'"));
        Assert.True(Migrator.Check(As()).IsUpToDate); // the startup check reads the journal
    }

    [Theory]
    [InlineData("CREATE TABLE sneaky (id int)")]
    [InlineData("DROP TABLE audit_log")]
    [InlineData("ALTER TABLE devices ADD COLUMN sneaky text")]
    [InlineData("TRUNCATE audit_log")]
    [InlineData("DELETE FROM schema_migrations")]
    [InlineData("INSERT INTO schema_migrations (scriptname, applied) VALUES ('x', now())")]
    [InlineData("UPDATE audit_log SET actor = 'someone else'")] // entries are added and pruned, never rewritten
    public async Task ItCantChangeTheSchemaOrTheMigrationsJournal(string sql)
    {
        await using var conn = await AsAppAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(conn, sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState);
    }

    [Fact]
    public async Task AnyPasswordWorksAndSettingItAgainReplacesIt()
    {
        // Its own role, so the shared lull_app's password isn't changed under other tests.
        const string role = "lull_app_password_test";
        const string first = "Sémi;colon 'quote' \"dq\" $dollar\\back=slash";
        try
        {
            await Migrator.EnsureRoleAsync(portal.ConnectionString, first, role);
            await Migrator.GrantDataAccessAsync(portal.ConnectionString, role);
            await using (var conn = await AsAppAsync(role, first))
                Assert.Equal(1L, await new NpgsqlCommand("SELECT 1::bigint", conn).ExecuteScalarAsync());

            await Migrator.EnsureRoleAsync(portal.ConnectionString, "second-password", role); // the role exists now: just a new password
            await Assert.ThrowsAsync<PostgresException>(() => AsAppAsync(role, first));
            await using (var conn = await AsAppAsync(role, "second-password"))
                Assert.Equal(1L, await new NpgsqlCommand("SELECT 1::bigint", conn).ExecuteScalarAsync());
        }
        finally
        {
            await using var owner = new NpgsqlConnection(portal.ConnectionString);
            await owner.OpenAsync();
            await RunAsync(owner, $"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }
}
