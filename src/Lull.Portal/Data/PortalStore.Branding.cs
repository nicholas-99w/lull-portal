using System.Text.Json;
using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

/// <summary>
/// The branding every page shows, with <see cref="Version"/> (when it last changed, in
/// Unix milliseconds) for the stylesheet's and logo's URLs, so browsers can keep them.
/// </summary>
public sealed record BrandingState(Branding Branding, string? LogoType, long Version)
{
    public bool HasLogo => LogoType is not null;
    public string StylesheetUrl => $"/brand/style.css?v={Version}";
    public string LogoUrl => $"/brand/logo?v={Version}";

    public static BrandingState Default { get; } = new(Branding.Default, null, 0);
}

// IT's branding: the school's name, colours and logo.
public sealed partial class PortalStore
{
    /// <summary>How long branding and account settings are kept in memory (each is replaced at once when this portal saves it).</summary>
    static readonly TimeSpan SettingsCacheFor = TimeSpan.FromMinutes(1);
    sealed record CachedBranding(BrandingState State, DateTimeOffset At);
    volatile CachedBranding? brandingCache;

    /// <summary>
    /// For the layout, which the error page uses too: Lull's own look if the database can't be
    /// reached, rather than an error page that fails itself.
    /// </summary>
    public async Task<BrandingState> GetBrandingOrDefaultAsync(CancellationToken ct)
    {
        try
        {
            return await GetBrandingAsync(ct);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or OperationCanceledException)
        {
            return BrandingState.Default;
        }
    }

    public async Task<BrandingState> GetBrandingAsync(CancellationToken ct)
    {
        if (brandingCache is { } cached && Now - cached.At < SettingsCacheFor) return cached.State;
        await using var cmd = db.CreateCommand("SELECT settings::text, logo_type, updated_at FROM branding WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var state = BrandingState.Default;
        if (await r.ReadAsync(ct))
        {
            Branding? branding = null;
            try
            {
                branding = JsonSerializer.Deserialize<Branding>(r.GetString(0), Json);
            }
            catch (JsonException)
            {
            }
            // Anything unreadable or no longer valid: Lull's own look rather than a broken one.
            state = new(branding is not null && branding.Check().Count == 0 ? branding : Branding.Default,
                r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2).ToUnixTimeMilliseconds());
        }
        brandingCache = new(state, Now);
        return state;
    }

    /// <summary>The logo's bytes and media type, or null if there's none.</summary>
    public async Task<(byte[] Bytes, string Type)?> GetLogoAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT logo, logo_type FROM branding WHERE id = 1 AND logo IS NOT NULL");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetFieldValue<byte[]>(0), r.GetString(1)) : null;
    }

    /// <summary>
    /// Saves the branding (callers check <see cref="Branding.Check"/>). The logo is replaced by
    /// <paramref name="logo"/> if given (callers check it with <see cref="Logo.TypeOf"/>), removed if
    /// <paramref name="removeLogo"/>, and otherwise kept.
    /// </summary>
    public async Task SaveBrandingAsync(Branding branding, (byte[] Bytes, string Type)? logo, bool removeLogo, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO branding (id, settings, logo, logo_type, updated_by, updated_at) VALUES (1, @s, @logo, @type, @a, @now)
            ON CONFLICT (id) DO UPDATE SET settings = EXCLUDED.settings, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at,
                logo = CASE WHEN @replace THEN EXCLUDED.logo ELSE branding.logo END,
                logo_type = CASE WHEN @replace THEN EXCLUDED.logo_type ELSE branding.logo_type END
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(branding, Json) });
            cmd.Parameters.Add(new NpgsqlParameter("logo", NpgsqlDbType.Bytea) { Value = (object?)logo?.Bytes ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)logo?.Type ?? DBNull.Value });
            cmd.Parameters.AddWithValue("replace", logo is not null || removeLogo);
            cmd.Parameters.AddWithValue("a", actor);
            cmd.Parameters.AddWithValue("now", Now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var logoChange = logo is not null ? "replaced" : removeLogo ? "removed" : "kept";
        await AuditAsync(conn, tx, actor, null, "branding_changed", new { branding, logo = logoChange }, ct);
        await tx.CommitAsync(ct);
        brandingCache = null;
    }
}
