using System.Text.Json;
using Lull.Portal.Api;
using Lull.Portal.Domain;
using Npgsql;

namespace Lull.Portal.Data;

/// <summary>The views of IT's device list.</summary>
public enum DeviceFilter
{
    All,
    /// <summary>A problem to look into: refused settings, overdue or not heard from, or never checked in.</summary>
    Attention,
    /// <summary>Not heard from lately (<see cref="DeviceStatusView.StaleAfter"/>).</summary>
    Offline,
    /// <summary>Reporting scheduled downtime that hasn't been released, or locked down from here.</summary>
    Downtime,
    /// <summary>A change saved here that the Mac hasn't applied yet.</summary>
    Pending,
}

public sealed record DeviceRecord(
    Guid Id,
    string SerialNumber,
    string HardwareUuid,
    string? PrimaryUsername,
    bool ResetAllowed,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastCheckInAt,
    CheckIn? LastReport,
    DeviceSettings Settings,
    DateTimeOffset? DeregisteredAt = null,
    DateTimeOffset? RegisterRefusedAt = null,
    string? PrimaryFullName = null)
{
    public bool IsDeregistered => DeregisteredAt is not null;

    /// <summary>The student as parents know them: their full name from the Mac, else their username.</summary>
    public string? StudentName => PrimaryFullName ?? PrimaryUsername;

    /// <summary>For IT, who also need the username: "Jane Smith (jsmith26)".</summary>
    public string? StudentNameAndUsername => PrimaryFullName is { } f ? $"{f} ({PrimaryUsername})" : PrimaryUsername;
}

public sealed record AuditEntry(DateTimeOffset At, string Actor, Guid? DeviceId, string? SerialNumber, string Action, string? Detail);

public enum RegisterOutcome
{
    Created,
    /// <summary>IT reset the device, so its serial could register again.</summary>
    ReRegistered,
    /// <summary>
    /// The serial and hardware UUID match the record (a reinstall or a lost token):
    /// it starts afresh with the school defaults, like <see cref="ReEnrolled"/>.
    /// </summary>
    ReRegisteredSameHardware,
    /// <summary>Serial already registered to other hardware and not reset by IT.</summary>
    Conflict,
    /// <summary>A <see cref="Conflict"/> again, already audited (lulld retries at every poll).</summary>
    ConflictRepeated,
    /// <summary>A de-registered Mac enrolled again: it starts afresh with the school defaults.</summary>
    ReEnrolled,
}

/// <summary>
/// Device, settings and policy data, in plain SQL over Npgsql (no ORM): each query
/// is written out where it's used. Every change that matters is audited in the
/// same transaction. One class, in a file per area: PortalStore.Devices.cs,
/// .Settings.cs, .Policy.cs and .Audit.cs.
/// </summary>
public sealed partial class PortalStore(NpgsqlDataSource db, TimeProvider clock)
{
    static readonly JsonSerializerOptions Json = Wire.Options;

    DateTimeOffset Now => clock.GetUtcNow();
}
