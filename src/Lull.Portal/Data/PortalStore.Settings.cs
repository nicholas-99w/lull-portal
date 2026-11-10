using Lull.Portal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

// Each Mac's settings: schedules, lock-downs, releases and skips, each change with a new revision.
public sealed partial class PortalStore
{
    /// <summary>
    /// Saves a parent's (or IT's) choice for each kind with a fresh revision:
    /// opted out, following the school (null window), or customised. For
    /// school holidays, following the school means the usual settings.
    /// <paramref name="week"/> and <paramref name="holidayWeek"/>, if any, are advanced schedules
    /// that replace those choices (callers pass the choices as they were, to keep them).
    /// </summary>
    /// <param name="expectedRevision">The revision the person saw: if the settings changed since, nothing is saved (null).</param>
    /// <returns>
    /// What was saved, and whether a release or skip was cancelled: lulld applies one to the window
    /// in progress, or starting, at its instant, so with other windows it would cut or skip the wrong
    /// one, or none. Null if the settings had changed.
    /// </returns>
    public async Task<(DeviceSettings Saved, bool ReleaseCancelled)?> SaveSettingsAsync(
        Guid deviceId, long expectedRevision, ScheduleMode schoolMode, Window? school, ScheduleMode weekendMode, Window? weekend,
        ScheduleMode holidayMode, Window? holiday, WeekGrid? week, WeekGrid? holidayWeek, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var previous = await ReadSettingsForUpdateAsync(conn, tx, deviceId, ct)
            ?? throw new KeyNotFoundException($"device {deviceId}");
        if (previous.Revision != expectedRevision) return null;
        var saved = previous with
        {
            Enabled = true,
            SchoolNightsOff = schoolMode == ScheduleMode.OptOut,
            SchoolNights = schoolMode == ScheduleMode.Custom ? school : null,
            WeekendOff = weekendMode == ScheduleMode.OptOut,
            Weekend = weekendMode == ScheduleMode.Custom ? weekend : null,
            HolidayOff = holidayMode == ScheduleMode.OptOut,
            Holiday = holidayMode == ScheduleMode.Custom ? holiday : null,
            Week = week,
            HolidayWeek = holidayWeek,
            Revision = SettingsRules.NextRevision(previous.Revision, Now),
        };
        var now = SettingsRules.Truncate(Now);
        bool cancelled = !saved.SameSchedule(previous) && (previous.IsReleased(now) || previous.IsSkipPending(now));
        if (!saved.SameSchedule(previous)) saved = saved.WithoutRelease();
        await WriteSettingsAsync(conn, tx, deviceId, saved, actor, ct);
        var policy = await GetPolicyAsync(conn, tx, ct);
        await AuditAsync(conn, tx, actor, deviceId, "settings_changed", Diff(previous, saved, policy.SchoolHours, cancelled), ct);
        await tx.CommitAsync(ct);
        return (saved, cancelled);
    }

    /// <summary>
    /// Starts a "Lock down now" until <paramref name="until"/>. It cancels a release or skip: on the Mac a
    /// lock-down merges with the window around it, which a release would otherwise cut short.
    /// Callers check <see cref="SettingsRules.CheckLockNow"/>.
    /// </summary>
    /// <param name="nextDowntime">When the next scheduled downtime starts, as the Mac reported it, if known (<see cref="DeviceSettings.LockJoinsAt"/>).</param>
    public async Task<DeviceSettings?> LockNowAsync(
        Guid deviceId, long expectedRevision, DateTimeOffset until, DateTimeOffset? nextDowntime, string actor, CancellationToken ct) =>
        await UpdateSettingsAsync(deviceId, expectedRevision, actor, "lock_now", (s, now) => s with
        {
            LockFrom = now,
            LockUntil = SettingsRules.Truncate(until),
            LockJoinsAt = nextDowntime,
            ReleasedAt = null,
            ReleasedUntil = null,
        }, ct);

    /// <summary>
    /// Ends just the lock-down, before the scheduled downtime it runs into starts: moving
    /// <c>lockUntil</c> to now parts the two on the Mac, so that downtime still starts on time.
    /// </summary>
    public async Task<DeviceSettings?> EndLockDownAsync(Guid deviceId, long expectedRevision, string actor, CancellationToken ct) =>
        await UpdateSettingsAsync(deviceId, expectedRevision, actor, "lock_down_ended", (s, now) => s with
        {
            LockUntil = s.LockUntil is { } u && u > now ? now : s.LockUntil,
            LockJoinsAt = null,
        }, ct);

    /// <summary>
    /// "Release now": ends whatever downtime is in progress on the Mac
    /// (scheduled window or lock-down). lulld cuts the window containing
    /// <c>releasedAt</c> short; the schedule carries on afterwards. A lock-down
    /// is left as it is: it may be merged with scheduled downtime that hasn't
    /// started yet, and the cut ends both (<see cref="DeviceSettings.IsLockedDown"/>
    /// treats it as over).
    /// Callers check <see cref="SettingsRules.CheckRelease"/>.
    /// </summary>
    /// <param name="windowEnd">When the scheduled downtime in progress would end, as the Mac last reported it.</param>
    public async Task<DeviceSettings?> ReleaseAsync(Guid deviceId, long expectedRevision, DateTimeOffset? windowEnd, string actor, CancellationToken ct) =>
        await UpdateSettingsAsync(deviceId, expectedRevision, actor, "released", (s, now) => s with
        {
            ReleasedAt = now,
            ReleasedUntil = Later(windowEnd, s.IsLockedDown(now) ? s.LockUntil : null),
        }, ct);

    static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// "Skip next downtime": a release at the exact start of the window, which lulld
    /// cuts to nothing (the window containing <c>releasedAt</c> ends there). Callers
    /// check <see cref="SettingsRules.CheckSkip"/>.
    /// </summary>
    public async Task<DeviceSettings?> SkipAsync(
        Guid deviceId, long expectedRevision, DateTimeOffset windowStart, DateTimeOffset windowEnd, string actor, CancellationToken ct) =>
        await UpdateSettingsAsync(deviceId, expectedRevision, actor, "skipped", (s, _) => s with
        {
            ReleasedAt = SettingsRules.Truncate(windowStart),
            ReleasedUntil = SettingsRules.Truncate(windowEnd),
        }, ct);

    /// <summary>"Reset overrides": no lock-down, release or skip; the schedule applies as set.</summary>
    public async Task<DeviceSettings?> ResetOverridesAsync(Guid deviceId, long expectedRevision, string actor, CancellationToken ct) =>
        await UpdateSettingsAsync(deviceId, expectedRevision, actor, "overrides_reset", (s, _) => s with
        {
            LockFrom = null,
            LockUntil = null,
            LockJoinsAt = null,
            ReleasedAt = null,
            ReleasedUntil = null,
        }, ct);

    /// <summary>
    /// One change to a Mac's lock-down, release or skip, with a new revision. Only if the settings are
    /// still at <paramref name="expectedRevision"/>, the ones the person saw: otherwise (someone else,
    /// or another tab, changed them meanwhile) nothing is saved and the result is null.
    /// </summary>
    async Task<DeviceSettings?> UpdateSettingsAsync(
        Guid deviceId, long expectedRevision, string actor, string action, Func<DeviceSettings, DateTimeOffset, DeviceSettings> change,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var previous = await ReadSettingsForUpdateAsync(conn, tx, deviceId, ct)
            ?? throw new KeyNotFoundException($"device {deviceId}");
        if (previous.Revision != expectedRevision) return null;
        var saved = change(previous, SettingsRules.Truncate(Now)) with { Revision = SettingsRules.NextRevision(previous.Revision, Now) };
        if (saved.LockFrom is { } f && saved.LockUntil is { } e && e <= f) saved = saved with { LockFrom = null, LockUntil = null };
        await WriteSettingsAsync(conn, tx, deviceId, saved, actor, ct);
        await AuditAsync(conn, tx, actor, deviceId, action,
            new { lockFrom = saved.LockFrom, lockUntil = saved.LockUntil, releasedAt = saved.ReleasedAt, releasedUntil = saved.ReleasedUntil, revision = saved.Revision }, ct);
        await tx.CommitAsync(ct);
        return saved;
    }

    /// <summary>
    /// The device_settings columns <see cref="ReadSettings"/> reads, by name: every query that reads
    /// settings selects this list (alongside any other columns, which must not share these names).
    /// </summary>
    const string SettingsColumns =
        "enabled, school_start, school_end, weekend_start, weekend_end, revision, lock_from, lock_until, released_at, " +
        "school_off, weekend_off, released_until, lock_joins_at, holiday_start, holiday_end, holiday_off, week, holiday_week";

    static DeviceSettings ReadSettings(NpgsqlDataReader r)
    {
        T? Value<T>(string column) where T : struct => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetFieldValue<T>(r.GetOrdinal(column));
        // NULL start and end = follow the school's schedule (or, for holidays, the usual settings).
        Window? W(string prefix) => Value<TimeOnly>(prefix + "_start") is { } start && Value<TimeOnly>(prefix + "_end") is { } end
            ? new Window(start, end)
            : null;
        bool Flag(string column) => r.GetBoolean(r.GetOrdinal(column));
        // NULL = no advanced schedule (the column's CHECK keeps anything else parseable).
        WeekGrid? Grid(string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : WeekGrid.Parse(r.GetString(r.GetOrdinal(column)));
        return new(
            Flag("enabled"), W("school"), W("weekend"), r.GetInt64(r.GetOrdinal("revision")),
            Value<DateTimeOffset>("lock_from"), Value<DateTimeOffset>("lock_until"), Value<DateTimeOffset>("released_at"),
            Flag("school_off"), Flag("weekend_off"), Value<DateTimeOffset>("released_until"), Value<DateTimeOffset>("lock_joins_at"),
            W("holiday"), Flag("holiday_off"), Grid("week"), Grid("holiday_week"));
    }

    static async Task<DeviceSettings?> ReadSettingsForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SettingsColumns} FROM device_settings WHERE device_id = @id FOR UPDATE",
            conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSettings(r) : null;
    }

    async Task WriteSettingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, DeviceSettings s, string actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO device_settings (device_id, enabled, school_start, school_end, weekend_start, weekend_end, revision,
                lock_from, lock_until, released_at, school_off, weekend_off, released_until, lock_joins_at,
                holiday_start, holiday_end, holiday_off, week, holiday_week, updated_by, updated_at)
            VALUES (@id, @e, @ss, @se, @ws, @we, @rev, @lf, @lu, @ra, @so, @wo, @ru, @lj, @hs, @he, @ho, @wk, @hw, @by, @now)
            ON CONFLICT (device_id) DO UPDATE SET enabled = EXCLUDED.enabled, school_start = EXCLUDED.school_start,
                school_end = EXCLUDED.school_end, weekend_start = EXCLUDED.weekend_start, weekend_end = EXCLUDED.weekend_end,
                revision = EXCLUDED.revision, lock_from = EXCLUDED.lock_from, lock_until = EXCLUDED.lock_until,
                released_at = EXCLUDED.released_at, school_off = EXCLUDED.school_off, weekend_off = EXCLUDED.weekend_off,
                released_until = EXCLUDED.released_until, lock_joins_at = EXCLUDED.lock_joins_at, holiday_start = EXCLUDED.holiday_start,
                holiday_end = EXCLUDED.holiday_end, holiday_off = EXCLUDED.holiday_off, week = EXCLUDED.week,
                holiday_week = EXCLUDED.holiday_week, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("e", s.Enabled);
        // NULL = follow the school's (profile's) schedule for those days.
        static NpgsqlParameter Time(string name, TimeOnly? t) => new(name, NpgsqlDbType.Time) { Value = (object?)t ?? DBNull.Value };
        cmd.Parameters.Add(Time("ss", s.SchoolNights?.Start));
        cmd.Parameters.Add(Time("se", s.SchoolNights?.End));
        cmd.Parameters.Add(Time("ws", s.Weekend?.Start));
        cmd.Parameters.Add(Time("we", s.Weekend?.End));
        cmd.Parameters.AddWithValue("rev", s.Revision);
        cmd.Parameters.Add(new NpgsqlParameter("lf", NpgsqlDbType.TimestampTz) { Value = (object?)s.LockFrom ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("lu", NpgsqlDbType.TimestampTz) { Value = (object?)s.LockUntil ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ra", NpgsqlDbType.TimestampTz) { Value = (object?)s.ReleasedAt ?? DBNull.Value });
        cmd.Parameters.AddWithValue("so", s.SchoolNightsOff);
        cmd.Parameters.AddWithValue("wo", s.WeekendOff);
        cmd.Parameters.Add(new NpgsqlParameter("ru", NpgsqlDbType.TimestampTz) { Value = (object?)s.ReleasedUntil ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("lj", NpgsqlDbType.TimestampTz) { Value = (object?)s.LockJoinsAt ?? DBNull.Value });
        cmd.Parameters.Add(Time("hs", s.Holiday?.Start));
        cmd.Parameters.Add(Time("he", s.Holiday?.End));
        cmd.Parameters.AddWithValue("ho", s.HolidayOff);
        cmd.Parameters.Add(new NpgsqlParameter("wk", NpgsqlDbType.Text) { Value = (object?)s.Week?.Text ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("hw", NpgsqlDbType.Text) { Value = (object?)s.HolidayWeek?.Text ?? DBNull.Value });
        cmd.Parameters.AddWithValue("by", actor);
        cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static object Diff(DeviceSettings from, DeviceSettings to, SchoolHours school, bool releaseCancelled = false) => new
    {
        schoolNights = new[] { from.Describe(WindowKind.SchoolNights), to.Describe(WindowKind.SchoolNights) },
        weekend = new[] { from.Describe(WindowKind.Weekend), to.Describe(WindowKind.Weekend) },
        week = from.Week is null && to.Week is null ? null : new[] { from.DescribeWeek(school), to.DescribeWeek(school) },
        holiday = new[] { from.DescribeHoliday(), to.DescribeHoliday() },
        revision = to.Revision,
        releaseCancelled = releaseCancelled ? true : (bool?)null,
    };
}
