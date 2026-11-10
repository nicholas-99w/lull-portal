using System.Text.Json;
using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

// The audit log: entries written in the same transaction as the change, searched and pruned.
public sealed partial class PortalStore
{
    /// <summary>
    /// IT's audit log, newest first: one page of the entries whose actor, action,
    /// Mac serial or details contain <paramref name="query"/> (any case; all of them when empty).
    /// </summary>
    public async Task<Paged<AuditEntry>> SearchAuditAsync(string? query, int? page, CancellationToken ct)
    {
        const string from = """
            FROM audit_log a LEFT JOIN devices d ON d.id = a.device_id
            WHERE (@q = '' OR strpos(lower(a.actor), @q) > 0 OR strpos(lower(a.action), @q) > 0
                OR strpos(lower(d.serial_number), @q) > 0 OR strpos(lower(a.detail::text), @q) > 0)
            """;
        var q = Paging.Normalize(query);
        await using var count = db.CreateCommand($"SELECT count(*) {from}");
        count.Parameters.AddWithValue("q", q);
        var total = (int)(long)(await count.ExecuteScalarAsync(ct))!;
        var number = Paging.Clamp(page, total);
        await using var cmd = db.CreateCommand(
            $"""
            SELECT a.at, a.actor, a.device_id, d.serial_number, a.action, a.detail::text
            {from}
            ORDER BY a.id DESC OFFSET @offset LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("q", q);
        cmd.Parameters.AddWithValue("offset", Paging.Offset(number));
        cmd.Parameters.AddWithValue("limit", Paging.Size);
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AuditEntry(
                r.GetFieldValue<DateTimeOffset>(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return new(list, number, total);
    }

    /// <summary>
    /// Deletes audit entries older than <paramref name="before"/>, in batches so a
    /// first run on a large log doesn't hold one long transaction, and audits how
    /// many went. Returns that number.
    /// </summary>
    public async Task<int> PruneAuditAsync(DateTimeOffset before, CancellationToken ct)
    {
        const int batch = 10_000;
        int deleted = 0, n;
        do
        {
            await using var cmd = db.CreateCommand(
                "DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log WHERE at < @before ORDER BY id LIMIT @batch)");
            cmd.Parameters.AddWithValue("before", before);
            cmd.Parameters.AddWithValue("batch", batch);
            n = await cmd.ExecuteNonQueryAsync(ct);
            deleted += n;
        } while (n == batch);
        if (deleted > 0)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await AuditAsync(conn, tx, "system", null, "audit_pruned", new { deleted, before }, ct);
            await tx.CommitAsync(ct);
        }
        return deleted;
    }

    Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string actor, Guid? deviceId, string action, object? detail, CancellationToken ct) =>
        WriteAuditAsync(conn, tx, Now, actor, deviceId, action, detail, ct);

    /// <summary>Shared with <see cref="UserStore"/>. Never pass secrets (passwords, codes, tokens) in <paramref name="detail"/>.</summary>
    internal static async Task WriteAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DateTimeOffset now, string actor, Guid? deviceId, string action, object? detail,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO audit_log (at, actor, device_id, action, detail) VALUES (@now, @a, @d, @act, @det)", conn, tx);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)deviceId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("act", action);
        cmd.Parameters.Add(new NpgsqlParameter("det", NpgsqlDbType.Jsonb)
        {
            Value = detail is null ? DBNull.Value : JsonSerializer.Serialize(detail, Json),
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
