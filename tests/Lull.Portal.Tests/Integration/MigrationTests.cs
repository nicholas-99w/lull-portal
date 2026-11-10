using Lull.Portal.Data;
using Npgsql;

namespace Lull.Portal.Tests.Integration;

/// <summary>A portal whose database was created but never migrated.</summary>
public sealed class UnmigratedPortalFactory : PortalFactory
{
    protected override bool CreatesOwner => false;
    protected override Task PrepareDatabaseAsync() => Task.CompletedTask;
}

/// <summary>A portal whose database a newer portal has migrated further.</summary>
public sealed class NewerDatabasePortalFactory : PortalFactory
{
    protected override bool CreatesOwner => false;

    protected override async Task PrepareDatabaseAsync()
    {
        await Migrator.RunAsync(ConnectionString);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await new NpgsqlCommand(
            "INSERT INTO schema_migrations (scriptname, applied) VALUES ('Lull.Portal.Data.Migrations.9999_from_the_future.sql', now())",
            conn).ExecuteNonQueryAsync();
        await Migrator.GrantDataAccessAsync(ConnectionString);
    }
}

public class MigrationTests
{
    /// <summary>Every migration, in the order they apply.</summary>
    static readonly string[] All =
    [
        "Lull.Portal.Data.Migrations.0001_baseline.sql",
        "Lull.Portal.Data.Migrations.0002_register_refused_at.sql",
        "Lull.Portal.Data.Migrations.0003_entra_accounts.sql",
        "Lull.Portal.Data.Migrations.0004_released_until.sql",
        "Lull.Portal.Data.Migrations.0005_lock_joins_at.sql",
        "Lull.Portal.Data.Migrations.0006_holiday_schedule.sql",
        "Lull.Portal.Data.Migrations.0007_two_factor.sql",
        "Lull.Portal.Data.Migrations.0008_week_grid.sql",
        "Lull.Portal.Data.Migrations.0009_primary_full_name.sql",
        "Lull.Portal.Data.Migrations.0010_branding.sql",
        "Lull.Portal.Data.Migrations.0011_account_settings.sql",
    ];

    static readonly string Admin = Environment.GetEnvironmentVariable("LULL_TEST_POSTGRES")
        ?? throw new InvalidOperationException("LULL_TEST_POSTGRES is not set: run ./test.sh, which starts Postgres in Docker.");

    /// <summary>Runs <paramref name="body"/> against a new, empty database, then drops it.</summary>
    static async Task WithDatabaseAsync(Func<string, Task> body)
    {
        var name = "lull_migrate_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(Admin))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", conn).ExecuteNonQueryAsync();
        }
        try
        {
            await body(new NpgsqlConnectionStringBuilder(Admin) { Database = name }.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(Admin);
            await conn.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn).ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public Task AnEmptyDatabaseIsMigratedOnce() => WithDatabaseAsync(async cs =>
    {
        Assert.Equal(All, Migrator.Check(cs).Pending);
        Assert.Equal(All, await Migrator.RunAsync(cs));
        Assert.True(Migrator.Check(cs).IsUpToDate);
        Assert.Empty(await Migrator.RunAsync(cs));
    });

    [Fact]
    public Task ADatabaseAnOlderPortalCreatedTakesEveryMigration() => WithDatabaseAsync(async cs =>
    {
        // What the portal did on startup before migrations: the same SQL, with no journal.
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand(Migrator.Script("0001_baseline.sql"), conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand(
                "INSERT INTO audit_log (at, actor, action) VALUES (now(), 'test', 'kept')", conn).ExecuteNonQueryAsync();
        }
        Assert.Equal(All, await Migrator.RunAsync(cs));
        Assert.True(Migrator.Check(cs).IsUpToDate);
        await using var check = new NpgsqlConnection(cs);
        await check.OpenAsync();
        Assert.Equal(1L, await new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE action = 'kept'", check).ExecuteScalarAsync());
    });

    [Fact]
    public Task MigratorsStartedTogetherTakeTurns() => WithDatabaseAsync(async cs =>
    {
        var runs = await Task.WhenAll(Migrator.RunAsync(cs), Migrator.RunAsync(cs));
        Assert.Equal(1, runs.Count(applied => applied.Count == All.Length));
        Assert.Equal(1, runs.Count(applied => applied.Count == 0));
    });

    [Fact]
    public async Task ThePortalWontStartOnAnUnmigratedDatabase()
    {
        await using var portal = new UnmigratedPortalFactory();
        await ((IAsyncLifetime)portal).InitializeAsync();
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => portal.CreateClient());
            Assert.Contains("--migrate", ex.ToString());
        }
        finally
        {
            await ((IAsyncLifetime)portal).DisposeAsync();
        }
    }

    [Fact]
    public async Task ThePortalWontStartOnADatabaseANewerPortalMigrated()
    {
        await using var portal = new NewerDatabasePortalFactory();
        await ((IAsyncLifetime)portal).InitializeAsync();
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => portal.CreateClient());
            Assert.Contains("newer portal", ex.ToString());
        }
        finally
        {
            await ((IAsyncLifetime)portal).DisposeAsync();
        }
    }
}
