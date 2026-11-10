using System.Text.Json;
using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

// IT's rules (school hours, lock-downs) and school holidays.
public sealed partial class PortalStore
{
    public async Task<BoundsPolicy> GetPolicyAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await GetPolicyAsync(conn, null, ct);
    }

    /// <summary>
    /// Saves IT's school hours and lock-down rules (keeping the stored holidays)
    /// and moves every device's windows inside them
    /// (<see cref="SettingsRules.Clamp"/>), bumping the revision of each one
    /// that changed. New school hours change every advanced schedule's windows
    /// (<see cref="WeekGrid.Windows"/>), so those devices change too, dropping
    /// any hours now wholly in school. Returns how many devices changed.
    /// </summary>
    public async Task<int> SavePolicyAsync(BoundsPolicy policy, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var previous = await GetPolicyAsync(conn, tx, ct, forUpdate: true);
        policy = policy with { Holidays = previous.Holidays };
        await WritePolicyAsync(conn, tx, policy, actor, ct);
        await AuditAsync(conn, tx, actor, null, "bounds_changed", policy, ct);
        // Holiday periods start when school ends.
        if (policy.Holidays.Count > 0 && policy.SchoolHours.End != previous.SchoolHours.End)
            await BumpHolidaySettingsAsync(conn, tx, actor, ct);

        var all = new List<(Guid Id, DeviceSettings Settings)>();
        await using (var list = new NpgsqlCommand(
            $"SELECT device_id, {SettingsColumns} FROM device_settings FOR UPDATE",
            conn, tx))
        await using (var r = await list.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) all.Add((r.GetGuid(0), ReadSettings(r)));
        }
        int changed = 0;
        foreach (var (id, s) in all)
        {
            var school = SettingsRules.Clamp(WindowKind.SchoolNights, s.SchoolNights, policy);
            var weekend = SettingsRules.Clamp(WindowKind.Weekend, s.Weekend, policy);
            var newWindows = s.Week is not null && policy.SchoolHours != previous.SchoolHours;
            if (school == s.SchoolNights && weekend == s.Weekend && !newWindows) continue;
            // New windows: a release or skip of the old ones would cut or skip the wrong one (see SaveSettingsAsync).
            var saved = (s with
            {
                SchoolNights = school,
                Weekend = weekend,
                Week = s.Week?.WithoutSchool(policy.SchoolHours),
                Revision = SettingsRules.NextRevision(s.Revision, Now),
            }).WithoutRelease();
            await WriteSettingsAsync(conn, tx, id, saved, $"{actor} (bounds)", ct);
            await AuditAsync(conn, tx, $"{actor} (bounds)", id, "settings_clamped", Diff(s, saved, policy.SchoolHours), ct);
            changed++;
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    /// <summary>
    /// Adds a school holiday (callers check <see cref="SettingsRules.CheckHoliday"/>)
    /// and drops those over before <paramref name="today"/>. Devices are
    /// untouched: holidays only change what parents may do in the portal.
    /// </summary>
    public async Task AddHolidayAsync(SchoolHoliday holiday, DateOnly today, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var policy = await GetPolicyAsync(conn, tx, ct, forUpdate: true);
        var holidays = policy.Holidays.Where(h => h.Last >= today.AddDays(-1)).Append(holiday).OrderBy(h => h.First).ToList();
        await WritePolicyAsync(conn, tx, policy with { Holidays = holidays }, actor, ct);
        await AuditAsync(conn, tx, actor, null, "holiday_added", holiday, ct);
        await BumpHolidaySettingsAsync(conn, tx, actor, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Removes the school holiday starting on <paramref name="first"/>. False if there's none.</summary>
    public async Task<bool> RemoveHolidayAsync(DateOnly first, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var policy = await GetPolicyAsync(conn, tx, ct, forUpdate: true);
        if (policy.Holidays.FirstOrDefault(h => h.First == first) is not { } holiday) return false;
        await WritePolicyAsync(conn, tx, policy with { Holidays = policy.Holidays.Where(h => h != holiday).ToList() }, actor, ct);
        await AuditAsync(conn, tx, actor, null, "holiday_removed", holiday, ct);
        await BumpHolidaySettingsAsync(conn, tx, actor, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Macs with a holiday setting are sent the holiday periods, so a change to the holidays (or
    /// to when school ends) is a change to their settings: a new revision makes lulld take it
    /// (the same one with new content would be refused).
    /// </summary>
    async Task BumpHolidaySettingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string actor, CancellationToken ct)
    {
        var bumped = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE device_settings
            SET revision = GREATEST(revision + 1, @ms), updated_by = @by, updated_at = @now
            WHERE holiday_off OR holiday_start IS NOT NULL OR holiday_week IS NOT NULL
            RETURNING device_id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("ms", Now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("by", $"{actor} (holidays)");
            cmd.Parameters.AddWithValue("now", Now);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) bumped.Add(r.GetGuid(0));
        }
        foreach (var id in bumped)
            await AuditAsync(conn, tx, $"{actor} (holidays)", id, "settings_holidays_changed", null, ct);
    }

    async Task WritePolicyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, BoundsPolicy policy, string actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO bounds_policy (id, policy, updated_by, updated_at) VALUES (1, @p, @a, @now)
            ON CONFLICT (id) DO UPDATE SET policy = EXCLUDED.policy, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(policy, Json) });
        cmd.Parameters.AddWithValue("a", actor);
        cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<BoundsPolicy> GetPolicyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, bool forUpdate = false)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT policy::text FROM bounds_policy WHERE id = 1" + (forUpdate ? " FOR UPDATE" : ""), conn, tx);
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return BoundsPolicy.Default;
        // An older stored shape (or a broken one) falls back to the defaults.
        try
        {
            var p = JsonSerializer.Deserialize<BoundsPolicy>(json, Json);
            return p is { Version: BoundsPolicy.CurrentVersion, LockNow: not null } && SettingsRules.CheckPolicy(p).Count == 0
                ? p
                : BoundsPolicy.Default;
        }
        catch (JsonException)
        {
            return BoundsPolicy.Default;
        }
    }
}
