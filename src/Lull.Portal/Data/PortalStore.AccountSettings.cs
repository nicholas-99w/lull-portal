using System.Text.Json;
using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

// The owner's sign-in and audit log settings, which replace the .env's once saved.
public sealed partial class PortalStore
{
    /// <summary>Read on every sign-in and many admin pages, so kept for a minute (and replaced at once on a save here).</summary>
    sealed record CachedAccountSettings(AccountSettings? Saved, DateTimeOffset At);
    volatile CachedAccountSettings? accountSettingsCache;

    /// <summary>The saved settings, or null if the owner hasn't saved any (the .env's apply).</summary>
    public async Task<AccountSettings?> GetAccountSettingsAsync(CancellationToken ct)
    {
        if (accountSettingsCache is { } cached && Now - cached.At < SettingsCacheFor) return cached.Saved;
        await using var cmd = db.CreateCommand("SELECT settings::text FROM account_settings WHERE id = 1");
        AccountSettings? saved = null;
        if (await cmd.ExecuteScalarAsync(ct) is string json)
        {
            try
            {
                saved = JsonSerializer.Deserialize<AccountSettings>(json, Json);
            }
            catch (JsonException)
            {
                // Unreadable: the .env's, rather than guessing.
            }
        }
        accountSettingsCache = new(saved, Now);
        return saved;
    }

    /// <summary>Saves the settings (callers check <see cref="AccountSettings.Check"/>), audited with what they were.</summary>
    public async Task SaveAccountSettingsAsync(AccountSettings settings, AccountSettings previous, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO account_settings (id, settings, updated_by, updated_at) VALUES (1, @s, @a, @now)
            ON CONFLICT (id) DO UPDATE SET settings = EXCLUDED.settings, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(settings, Json) });
            cmd.Parameters.AddWithValue("a", actor);
            cmd.Parameters.AddWithValue("now", Now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(conn, tx, actor, null, "account_settings_changed", new { from = previous, to = settings }, ct);
        await tx.CommitAsync(ct);
        accountSettingsCache = null;
    }
}
