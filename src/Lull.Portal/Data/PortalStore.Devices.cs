using System.Text.Json;
using Lull.Portal.Api;
using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

// Macs: registering, check-ins, finding and listing them, and IT's actions on them.
public sealed partial class PortalStore
{
    public async Task<RegisterOutcome> RegisterAsync(string serial, string hardwareUuid, byte[] tokenHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? existing = null;
        bool resetAllowed = false, deregistered = false, refusedBefore = false, sameHardware = false;
        await using (var find = new NpgsqlCommand(
            """
            SELECT id, reset_allowed, deregistered_at IS NOT NULL, register_refused_at IS NOT NULL, hardware_uuid
            FROM devices WHERE serial_number = @s FOR UPDATE
            """, conn, tx))
        {
            find.Parameters.AddWithValue("s", serial);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                existing = r.GetGuid(0);
                resetAllowed = r.GetBoolean(1);
                deregistered = r.GetBoolean(2);
                refusedBefore = r.GetBoolean(3);
                sameHardware = string.Equals(r.GetString(4), hardwareUuid, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (existing is { } tomb && deregistered)
        {
            // Enrolled again after IT de-registered it (lulld deletes its enrolment
            // secret on de-registration, so this needs the Jamf enrolment again).
            await StartAfreshAsync(conn, tx, tomb, hardwareUuid, tokenHash, "re_enrolled", new { serial }, ct);
            await tx.CommitAsync(ct);
            return RegisterOutcome.ReEnrolled;
        }

        if (existing is { } same && sameHardware && !resetAllowed)
        {
            // The same Mac without its token: reinstalled (re-imaged) or its token lost.
            // A serial alone isn't enough (it's printed on the case), but the hardware
            // UUID matching too is. It may now be another student's Mac, so start afresh
            // and pin whoever signs in next. (An IT reset keeps settings and user: below.)
            await StartAfreshAsync(conn, tx, same, hardwareUuid, tokenHash, "re_registered_same_hardware", new { serial }, ct);
            await tx.CommitAsync(ct);
            return RegisterOutcome.ReRegisteredSameHardware;
        }

        if (existing is { } id)
        {
            if (!resetAllowed)
            {
                // Audited once: the Mac retries at every poll until IT resets it.
                if (refusedBefore) return RegisterOutcome.ConflictRepeated;
                await using (var mark = new NpgsqlCommand("UPDATE devices SET register_refused_at = @now WHERE id = @id", conn, tx))
                {
                    mark.Parameters.AddWithValue("now", Now);
                    mark.Parameters.AddWithValue("id", id);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await AuditAsync(conn, tx, "device", id, "register_refused", new { reason = "serial already registered to other hardware" }, ct);
                await tx.CommitAsync(ct);
                return RegisterOutcome.Conflict;
            }
            await using var update = new NpgsqlCommand(
                """
                UPDATE devices SET token_hash = @t, hardware_uuid = @h, reset_allowed = false, register_refused_at = NULL
                WHERE id = @id
                """, conn, tx);
            update.Parameters.AddWithValue("t", tokenHash);
            update.Parameters.AddWithValue("h", hardwareUuid);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
            await AuditAsync(conn, tx, "device", id, "re_registered", null, ct);
            await tx.CommitAsync(ct);
            return RegisterOutcome.ReRegistered;
        }

        var newId = Guid.NewGuid();
        try
        {
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO devices (id, serial_number, hardware_uuid, token_hash, registered_at)
                VALUES (@id, @s, @h, @t, @now)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("id", newId);
                insert.Parameters.AddWithValue("s", serial);
                insert.Parameters.AddWithValue("h", hardwareUuid);
                insert.Parameters.AddWithValue("t", tokenHash);
                insert.Parameters.AddWithValue("now", Now);
                await insert.ExecuteNonQueryAsync(ct);
            }
            var defaults = DeviceSettings.FollowSchool(SettingsRules.NextRevision(0, Now));
            await WriteSettingsAsync(conn, tx, newId, defaults, "system", ct);
            await AuditAsync(conn, tx, "device", newId, "registered", new { serial }, ct);
            await tx.CommitAsync(ct);
            return RegisterOutcome.Created;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two Macs racing with the same serial: the loser is a conflict.
            return RegisterOutcome.Conflict;
        }
    }

    /// <summary>
    /// A known serial registering as if new: school defaults (with a higher
    /// revision, so lulld accepts them), no pinned user, no check-in history.
    /// </summary>
    async Task StartAfreshAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string hardwareUuid, byte[] tokenHash,
        string action, object detail, CancellationToken ct)
    {
        var previous = await ReadSettingsForUpdateAsync(conn, tx, id, ct);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE devices SET token_hash = @t, hardware_uuid = @h, reset_allowed = false, deregistered_at = NULL,
                primary_username = NULL, primary_full_name = NULL, last_checkin_at = NULL, last_report = NULL, registered_at = @now,
                register_refused_at = NULL
            WHERE id = @id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("t", tokenHash);
            update.Parameters.AddWithValue("h", hardwareUuid);
            update.Parameters.AddWithValue("now", Now);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        var fresh = DeviceSettings.FollowSchool(SettingsRules.NextRevision(previous?.Revision ?? 0, Now));
        await WriteSettingsAsync(conn, tx, id, fresh, "system", ct);
        await AuditAsync(conn, tx, "device", id, action, detail, ct);
    }

    public async Task<DeviceRecord?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct) =>
        (await QueryDevicesAsync("d.token_hash = @t", p => p.AddWithValue("t", tokenHash), ct)).FirstOrDefault();

    public async Task<DeviceRecord?> GetDeviceAsync(Guid id, CancellationToken ct) =>
        (await QueryDevicesAsync("d.id = @id", p => p.AddWithValue("id", id), ct)).FirstOrDefault();

    /// <summary>
    /// Each <see cref="DeviceFilter"/> in SQL, so a school's thousands of Macs never load to be
    /// filtered. They say the same as <see cref="DeviceStatusView"/>, from the same columns:
    /// <c>@now</c>, <c>@stale</c> (<see cref="DeviceStatusView.StaleAfter"/> ago) and
    /// <c>@overdue</c> (<see cref="DeviceStatusView.CheckInGrace"/> ago).
    /// </summary>
    static string FilterSql(DeviceFilter filter) => filter switch
    {
        DeviceFilter.Attention => """
            (d.deregistered_at IS NULL AND (d.last_checkin_at IS NULL OR d.last_checkin_at < @stale
                OR d.last_report->>'settingsError' IS NOT NULL
                OR (d.last_report->>'nextTransition')::timestamptz < @overdue))
            """,
        DeviceFilter.Offline => "(d.deregistered_at IS NULL AND d.last_checkin_at < @stale)",
        DeviceFilter.Downtime => """
            (d.deregistered_at IS NULL AND (
                (s.lock_until > @now AND NOT COALESCE(s.released_at <= @now, false))
                OR (d.last_report->>'phase' = 'locked' AND NOT COALESCE(s.released_at <= @now AND s.released_until > @now, false))))
            """,
        DeviceFilter.Pending => """
            (d.deregistered_at IS NULL AND d.last_report IS NOT NULL AND d.last_report->>'settingsError' IS NULL
                AND (d.last_report->>'settingsRevision')::bigint IS DISTINCT FROM s.revision)
            """,
        _ => "true",
    };

    /// <summary>
    /// IT's device list: one page of the Macs whose serial, primary user or their full name contains
    /// <paramref name="query"/> (any case; all of them when empty) and that <paramref name="filter"/>
    /// keeps, and how many that search finds for each filter.
    /// </summary>
    public async Task<(Paged<DeviceRecord> Page, IReadOnlyDictionary<DeviceFilter, int> Counts)> SearchDevicesAsync(
        string? query, DeviceFilter filter, int? page, CancellationToken ct)
    {
        const string search =
            "(@q = '' OR strpos(lower(d.serial_number), @q) > 0 OR strpos(lower(d.primary_username), @q) > 0 " +
            "OR strpos(lower(d.primary_full_name), @q) > 0)";
        var q = Paging.Normalize(query);
        var now = Now;
        void Bind(NpgsqlParameterCollection p)
        {
            p.AddWithValue("q", q);
            p.AddWithValue("now", now);
            p.AddWithValue("stale", now - DeviceStatusView.StaleAfter);
            p.AddWithValue("overdue", now - DeviceStatusView.CheckInGrace);
        }

        var filters = Enum.GetValues<DeviceFilter>();
        var columns = string.Join(", ", filters.Select(f => $"count(*) FILTER (WHERE {FilterSql(f)})"));
        await using var count = db.CreateCommand(
            $"SELECT {columns} FROM devices d JOIN device_settings s ON s.device_id = d.id WHERE {search}");
        Bind(count.Parameters);
        var counts = new Dictionary<DeviceFilter, int>();
        await using (var r = await count.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            for (int i = 0; i < filters.Length; i++) counts[filters[i]] = (int)r.GetInt64(i);
        }

        var number = Paging.Clamp(page, counts[filter]);
        var rows = await QueryDevicesAsync($"{search} AND {FilterSql(filter)}", p =>
        {
            Bind(p);
            p.AddWithValue("offset", Paging.Offset(number));
            p.AddWithValue("limit", Paging.Size);
        }, ct, tail: "OFFSET @offset LIMIT @limit");
        return (new(rows, number, counts[filter]), counts);
    }

    public Task<IReadOnlyList<DeviceRecord>> ListDevicesForChildrenAsync(IReadOnlyCollection<string> children, CancellationToken ct) =>
        QueryDevicesAsync(
            "d.primary_username = ANY(@c) AND d.deregistered_at IS NULL", p => p.AddWithValue("c", children.ToArray()), ct);

    /// <summary>
    /// Stores the report and pins the primary user if none is set yet. A later
    /// check-in by someone else never moves the device to another family.
    /// The primary user's full name is updated whenever the report has it, and
    /// kept from earlier check-ins when not.
    /// </summary>
    public async Task RecordCheckInAsync(Guid deviceId, CheckIn report, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? pinnedBefore;
        await using (var find = new NpgsqlCommand("SELECT primary_username FROM devices WHERE id = @id FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("id", deviceId);
            pinnedBefore = await find.ExecuteScalarAsync(ct) as string;
        }
        var pin = pinnedBefore ?? report.PrimaryUserCandidate;
        var fullName = pin is null ? null : report.FullNameOf(pin);
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE devices SET last_checkin_at = @now, last_report = @r, primary_username = @u,
                primary_full_name = COALESCE(@f, primary_full_name)
            WHERE id = @id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("now", Now);
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(report, Json) });
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)pin ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Text) { Value = (object?)fullName ?? DBNull.Value });
            cmd.Parameters.AddWithValue("id", deviceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (pinnedBefore is null && pin is not null)
            await AuditAsync(conn, tx, "device", deviceId, "primary_user_pinned", new { username = pin }, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// IT removes the Mac from the portal. The row stays as a tombstone so the
    /// Mac's next call gets 410 Gone, which makes lulld drop its token, its
    /// enrolment secret and its cached portal settings and fall back to the
    /// profile. Parents stop seeing it at once (no pinned user).
    /// </summary>
    public async Task DeregisterAsync(Guid deviceId, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string serial;
        string? pinned;
        await using (var find = new NpgsqlCommand(
            "SELECT serial_number, primary_username FROM devices WHERE id = @id AND deregistered_at IS NULL FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("id", deviceId);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new KeyNotFoundException($"device {deviceId} (or already de-registered)");
            serial = r.GetString(0);
            pinned = r.IsDBNull(1) ? null : r.GetString(1);
        }
        await using (var update = new NpgsqlCommand(
            """
            UPDATE devices SET deregistered_at = @now, reset_allowed = false, primary_username = NULL, primary_full_name = NULL,
                last_report = NULL
            WHERE id = @id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("now", Now);
            update.Parameters.AddWithValue("id", deviceId);
            await update.ExecuteNonQueryAsync(ct);
        }
        // The serial goes in the detail too: the audit page shows it even if the row changes later.
        await AuditAsync(conn, tx, actor, deviceId, "deregistered", new { serial, primaryUser = pinned }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ResetDeviceAsync(Guid deviceId, string actor, CancellationToken ct)
    {
        await ExecAuditedAsync("UPDATE devices SET reset_allowed = true WHERE id = @id", deviceId, actor, "device_reset", null, null, ct);
    }

    /// <summary>A different user's full name comes from the Mac's next check-in that lists them.</summary>
    public async Task SetPrimaryUserAsync(Guid deviceId, string? username, string actor, CancellationToken ct)
    {
        await ExecAuditedAsync(
            """
            UPDATE devices SET primary_username = @u,
                primary_full_name = CASE WHEN primary_username IS NOT DISTINCT FROM @u THEN primary_full_name END
            WHERE id = @id
            """, deviceId, actor, "primary_user_changed",
            new { username }, p => p.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)username ?? DBNull.Value }), ct);
    }

    async Task<IReadOnlyList<DeviceRecord>> QueryDevicesAsync(
        string where, Action<NpgsqlParameterCollection> bind, CancellationToken ct, string tail = "")
    {
        await using var cmd = db.CreateCommand(
            $"""
            SELECT d.id, d.serial_number, d.hardware_uuid, d.primary_username, d.reset_allowed, d.registered_at,
                   d.last_checkin_at, d.last_report::text, d.deregistered_at, d.register_refused_at,
                   d.primary_full_name, {SettingsColumns}
            FROM devices d JOIN device_settings s ON s.device_id = d.id
            WHERE {where}
            ORDER BY lower(COALESCE(d.primary_full_name, d.primary_username)) NULLS LAST, d.primary_username, d.serial_number
            {tail}
            """);
        bind(cmd.Parameters);
        var list = new List<DeviceRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DeviceRecord(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4),
                r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : JsonSerializer.Deserialize<CheckIn>(r.GetString(7), Json),
                ReadSettings(r),
                r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    async Task ExecAuditedAsync(
        string sql, Guid deviceId, string actor, string action, object? detail, Action<NpgsqlParameterCollection>? bind,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", deviceId);
            bind?.Invoke(cmd.Parameters);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"device {deviceId}");
        }
        await AuditAsync(conn, tx, actor, deviceId, action, detail, ct);
        await tx.CommitAsync(ct);
    }
}
