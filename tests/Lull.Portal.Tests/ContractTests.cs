using System.Text.Json;
using System.Text.Json.Nodes;
using Lull.Portal.Api;
using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

/// <summary>
/// The portal reads and writes exactly the JSON lulld does: copies of the same
/// fixtures are checked by the Mac repository's RemoteSettingsTests.swift.
/// </summary>
public class ContractTests
{
    static JsonNode ToNode<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, Wire.Options))!;

    [Fact]
    public void SettingsWeSendMatchTheFixture()
    {
        var settings = new DeviceSettings(true, new(new(21, 30), new(6, 30)), new(new(23, 0), new(8, 0)), 1_760_000_000_000);
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings.json"), ToNode(SettingsPayload.From(settings))));
    }

    [Fact]
    public void LockDownWeSendMatchesTheFixture()
    {
        // lulld can't parse fractional seconds: the portal must write exactly this format.
        var settings = new DeviceSettings(
            false, new(new(21, 30), new(6, 30)), new(new(10, 0), new(14, 0)), 1_760_000_000_003,
            DateTimeOffset.Parse("2026-10-17T13:00:00+11:00"), DateTimeOffset.Parse("2026-10-17T04:00:00Z"));
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-lock.json"), ToNode(SettingsPayload.From(settings))),
            ToNode(SettingsPayload.From(settings)).ToJsonString());
    }

    [Fact]
    public void FollowingTheSchoolOmitsTheWindows()
    {
        var settings = DeviceSettings.FollowSchool(1_760_000_000_005);
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-follow.json"), ToNode(SettingsPayload.From(settings))),
            ToNode(SettingsPayload.From(settings)).ToJsonString());
    }

    [Fact]
    public void PerKindOptOutWeSendMatchesTheFixture()
    {
        var settings = new DeviceSettings(true, new(new(21, 30), new(6, 30)), new(new(23, 0), new(8, 0)), 1_760_000_000_006, WeekendOff: true);
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-optout.json"), ToNode(SettingsPayload.From(settings))),
            ToNode(SettingsPayload.From(settings)).ToJsonString());
    }

    [Fact]
    public void ReleaseWeSendMatchesTheFixture()
    {
        var settings = new DeviceSettings(
            true, new(new(21, 30), new(6, 30)), new(new(23, 0), new(8, 0)), 1_760_000_000_004,
            ReleasedAt: DateTimeOffset.Parse("2026-10-15T23:00:00+11:00"));
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-release.json"), ToNode(SettingsPayload.From(settings))),
            ToNode(SettingsPayload.From(settings)).ToJsonString());
    }

    static readonly IReadOnlyList<SchoolHoliday> Holidays =
    [
        new(new(2026, 10, 19), new(2026, 10, 23), "Term 3 holidays"),
        new(new(2026, 12, 21), new(2027, 1, 29), "Summer holidays"),
    ];

    static readonly SchoolHours SchoolHours = BoundsPolicy.Default.SchoolHours; // 08:30–15:30

    [Fact]
    public void HolidaySettingsMatchTheFixtures()
    {
        var custom = new DeviceSettings(true, new(new(21, 30), new(6, 30)), null, 1_760_000_000_007, Holiday: new(new(23, 0), new(9, 0)));
        var payload = SettingsPayload.From(custom, HolidayPeriods.From(Holidays, SchoolHours));
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-holiday.json"), ToNode(payload)), ToNode(payload).ToJsonString());

        var off = DeviceSettings.FollowSchool(1_760_000_000_008) with { HolidayOff = true, Holiday = new(new(23, 0), new(9, 0)) };
        payload = SettingsPayload.From(off, HolidayPeriods.From(Holidays.Take(1), SchoolHours));
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-holiday-off.json"), ToNode(payload)), ToNode(payload).ToJsonString());

        // No holiday setting, or no holidays: nothing about holidays is sent.
        Assert.Null(SettingsPayload.From(custom with { Holiday = null }, HolidayPeriods.From(Holidays, SchoolHours)).HolidayPeriods);
        Assert.Null(SettingsPayload.From(custom, HolidayPeriods.From([], SchoolHours)).HolidayPeriods);
    }

    /// <summary>Cells from <paramref name="day"/> (Monday = 0) at <paramref name="hour"/>:00, for <paramref name="hours"/> hours.</summary>
    static IEnumerable<int> Hours(int day, int hour, int hours) =>
        Enumerable.Range(0, hours).Select(i => (WeekGrid.Cell(day, hour) + i) % WeekGrid.Cells);

    [Fact]
    public void AdvancedScheduleMatchesTheFixture()
    {
        // Nights 9 pm – 7 am (Fri 10 pm to Sun 8 am at the weekend), and Monday 3–5 pm, school until 3:30 pm.
        var week = WeekGrid.FromCells(new[] { Hours(0, 15, 2), Hours(6, 21, 10), Hours(4, 22, 34) }
            .Concat(Enumerable.Range(0, 4).Select(d => Hours(d, 21, 10))).SelectMany(c => c))!;
        var settings = new DeviceSettings(true, new(new(21, 30), new(6, 30)), null, 1_760_000_000_009, Week: week);
        var payload = SettingsPayload.From(settings, null, SchoolHours);
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-week.json"), ToNode(payload)), ToNode(payload).ToJsonString());
        // School hours are always applied to an advanced schedule.
        Assert.Throws<ArgumentNullException>(() => SettingsPayload.From(settings));
    }

    [Fact]
    public void AdvancedHolidaysMatchTheFixture()
    {
        var holidayWeek = WeekGrid.FromCells(Enumerable.Range(0, 7)
            .SelectMany(d => d is 4 or 5 ? Hours(d, 23, 10) : Hours(d, 22, 10)))!;
        var settings = new DeviceSettings(true, new(new(21, 30), new(6, 30)), null, 1_760_000_000_010, HolidayWeek: holidayWeek);
        var payload = SettingsPayload.From(settings, HolidayPeriods.From(Holidays, SchoolHours));
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("settings-holiday-week.json"), ToNode(payload)), ToNode(payload).ToJsonString());
        // Without holidays, nothing about them is sent.
        Assert.Null(SettingsPayload.From(settings, HolidayPeriods.From([], SchoolHours)).HolidayWeek);
    }

    [Fact]
    public void RegisterRequestFromTheMacIsUnderstood()
    {
        var req = Fixtures.Read<RegisterRequest>("register-request.json");
        Assert.Equal("C02EXAMPLE01", req.SerialNumber);
        Assert.Equal("6B29FC40-CA47-1067-B31D-00DD010662DA", req.HardwareUUID);
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("register-request.json"), ToNode(req)), "field names match lulld's");
    }

    [Fact]
    public void RegisterResponseWeSendMatchesTheFixture()
    {
        var res = new RegisterResponse("3f1d2c9e-8a4b-4f7e-9d61-2b5a7c8e9f00", "example-device-token");
        Assert.True(JsonNode.DeepEquals(Fixtures.Node("register-response.json"), ToNode(res)));
    }

    [Fact]
    public void CheckInFromTheMacIsUnderstood()
    {
        var c = Fixtures.Read<CheckIn>("checkin.json");
        Assert.Equal("locked", c.Phase);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T19:30:00Z"), c.WindowEnd);
        Assert.Equal(1_760_000_000_000, c.SettingsRevision);
        Assert.Equal("Australia/Sydney", c.TimeZone);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Sun"], c.Schedules[0].Days);
        Assert.Equal("jsmith26", c.PrimaryUserCandidate);
        Assert.Equal("Jane Smith", c.FullNameOf("jsmith26"));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri"], c.ProfileSchedules![0].Days);
        Assert.Null(c.PhaseReason);
    }

    [Fact]
    public void PrimaryUserSkipsExemptAccounts()
    {
        var c = Fixtures.Read<CheckIn>("checkin.json") with
        {
            Users = [new("itadmin", Exempt: true, AgentConnected: false), new("ASmith29", Exempt: false, AgentConnected: true)],
        };
        Assert.Equal("asmith29", c.PrimaryUserCandidate);
        Assert.Null((c with { Users = [new("itadmin", true, false)] }).PrimaryUserCandidate);
    }

    [Fact]
    public void FullNamesAreTidiedAndOnlyForTheirOwnUser()
    {
        var c = Fixtures.Read<CheckIn>("checkin.json") with
        {
            Users =
            [
                new("ASmith29", false, true, " Alex Smith\n"), new("bsmith30", false, true, "  "),
                new("csmith31", false, true, "CSmith31"), new("dsmith32", false, true, new string('D', 150)), new("esmith33", false, true),
            ],
        };
        Assert.Equal("Alex Smith", c.FullNameOf("asmith29"));
        Assert.Null(c.FullNameOf("bsmith30")); // blank
        Assert.Null(c.FullNameOf("csmith31")); // just the username again
        Assert.Equal(CheckIn.MaxFullNameLength, c.FullNameOf("dsmith32")!.Length);
        Assert.Null(c.FullNameOf("esmith33")); // an older lulld
        Assert.Null(c.FullNameOf("nobody"));
    }
}
