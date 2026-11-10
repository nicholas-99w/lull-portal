using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class SettingsRulesTests
{
    static Window W(string start, string end) => new(TimeOnly.Parse(start), TimeOnly.Parse(end));
    static readonly BoundsPolicy P = BoundsPolicy.Default; // school hours Mon–Fri 08:30–15:30
    static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    static DateTimeOffset Syd(string local) => new(DateTime.Parse(local), Sydney.GetUtcOffset(DateTime.Parse(local)));

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(SettingsRules.CheckPolicy(P));
        Assert.Equal("Mon–Fri 8:30 am – 3:30 pm", P.SchoolHours.ToString());
    }

    [Theory]
    [InlineData("21:30", "06:30")]
    [InlineData("15:30", "08:30")] // right up to the edges
    [InlineData("19:00", "23:00")] // evening only
    [InlineData("00:30", "07:00")] // after midnight
    public void SchoolNightsOutsideSchoolHours(string start, string end) =>
        Assert.Empty(SettingsRules.CheckForParent(WindowKind.SchoolNights, W(start, end), P));

    [Theory]
    [InlineData("15:00", "06:30")] // starts before school ends (Mon–Thu)
    [InlineData("21:30", "09:00")] // runs into the next school morning
    [InlineData("10:00", "12:00")] // a daytime block on a school day
    public void SchoolNightsTouchingSchoolHoursAreRefused(string start, string end) =>
        Assert.Contains(SettingsRules.CheckForParent(WindowKind.SchoolNights, W(start, end), P), e => e.Contains("can't overlap school hours"));

    [Fact]
    public void WeekendWindowsOnlyMeetSchoolOnFriday()
    {
        // Parents can't change Friday alone on this page, so it points them to where they can.
        var clash = Assert.Single(SettingsRules.CheckForParent(WindowKind.Weekend, W("14:00", "22:00"), P));
        Assert.Contains("interfere with Friday's school hours", clash);
        Assert.Contains("Advanced schedule", clash);
        Assert.DoesNotContain("must start", clash);

        // Fri and Sat starts: Saturday daytime is fine, Friday daytime isn't.
        Assert.Contains(SettingsRules.CheckForParent(WindowKind.Weekend, W("10:00", "14:00"), P), e => e.Contains("Friday"));
        Assert.Empty(SettingsRules.CheckForParent(WindowKind.Weekend, W("16:00", "14:00"), P)); // Fri 16:00 → Sat 14:00
        Assert.Empty(SettingsRules.CheckForParent(WindowKind.Weekend, W("23:00", "08:00"), P));
        // Sunday night belongs to school nights: Sunday 14:00 is fine, Monday 09:00 isn't.
        Assert.NotEmpty(SettingsRules.CheckForParent(WindowKind.SchoolNights, W("14:00", "09:00"), P));
    }

    [Fact]
    public void ClampTrimsAroundSchoolHoursNeverLonger()
    {
        var later = P with { SchoolHours = new(new(8, 0), new(16, 0)) };
        Assert.Equal(W("21:30", "06:30"), SettingsRules.Clamp(WindowKind.SchoolNights, W("21:30", "06:30"), later));
        Assert.Equal(W("21:30", "08:00"), SettingsRules.Clamp(WindowKind.SchoolNights, W("21:30", "08:15"), later)); // end at school start
        Assert.Equal(W("16:00", "06:30"), SettingsRules.Clamp(WindowKind.SchoolNights, W("15:45", "06:30"), later)); // start at school end
        // Friday 10:00–14:00 can't be trimmed without making it longer: back to following the school.
        Assert.Null(SettingsRules.Clamp(WindowKind.Weekend, W("10:00", "14:00"), later));
        Assert.Null(SettingsRules.Clamp(WindowKind.Weekend, null, later)); // following the school stays so
    }

    [Fact]
    public void BrokenPoliciesAreRefused()
    {
        Assert.Contains(SettingsRules.CheckPolicy(P with { SchoolHours = new(new(15, 0), new(9, 0)) }), e => e.Contains("end after they start"));
        Assert.Contains(SettingsRules.CheckPolicy(P with { LockNow = P.LockNow with { MaxMinutes = 2000 } }), e => e.Contains("longest lock-down"));
    }

    [Fact]
    public void LockNowStaysOutOfSchoolHours()
    {
        var tue1pm = Syd("2026-10-20 13:00");
        var tue4pm = Syd("2026-10-20 16:00");
        var tue10pm = Syd("2026-10-20 22:00");
        Assert.Contains(SettingsRules.CheckLockNow(P, tue1pm, tue1pm.AddHours(1), Sydney, false), e => e.Contains("isn't available during school hours"));
        Assert.Empty(SettingsRules.CheckLockNow(P, tue4pm, tue4pm.AddHours(2), Sydney, false));
        // Overnight until 9 am runs into Wednesday's school day.
        Assert.Contains(SettingsRules.CheckLockNow(P, tue10pm, Syd("2026-10-21 09:00"), Sydney, false), e => e.Contains("must end by 8:30 am"));
        Assert.Empty(SettingsRules.CheckLockNow(P, tue10pm, Syd("2026-10-21 08:30"), Sydney, false));
        // Saturday daytime is fine; so is anything for IT.
        var sat = Syd("2026-10-17 13:00");
        Assert.Empty(SettingsRules.CheckLockNow(P, sat, sat.AddHours(4), Sydney, false));
        Assert.Empty(SettingsRules.CheckLockNow(P, tue1pm, tue1pm.AddHours(1), Sydney, isAdmin: true));
        // Length and the on/off switch.
        Assert.Contains(SettingsRules.CheckLockNow(P, sat, sat.AddHours(13), Sydney, false), e => e.Contains("at most 12 h"));
        Assert.Contains(SettingsRules.CheckLockNow(P with { LockNow = P.LockNow with { Enabled = false } }, sat, sat.AddHours(1), Sydney, false), e => e.Contains("turned off"));
        Assert.NotEmpty(SettingsRules.CheckLockNow(P, tue1pm, tue1pm.AddHours(25), Sydney, isAdmin: true));
        // Judged in the Mac's time zone: 02:00 UTC Tuesday is 1 pm in Sydney.
        Assert.Equal(tue1pm, DateTimeOffset.Parse("2026-10-20T02:00:00Z"));
    }

    [Fact]
    public void ReleaseNotDuringSchoolHoursForParents()
    {
        Assert.NotEmpty(SettingsRules.CheckRelease(P, Syd("2026-10-20 10:00"), Sydney, false));
        Assert.Empty(SettingsRules.CheckRelease(P, Syd("2026-10-20 22:00"), Sydney, false));
        Assert.Empty(SettingsRules.CheckRelease(P, Syd("2026-10-20 10:00"), Sydney, isAdmin: true));
    }

    static SchoolHoliday Holiday(string first, string last) => new(DateOnly.Parse(first), DateOnly.Parse(last), "Break");

    [Fact]
    public void SchoolHoursDontHoldParentsBackOnHolidays()
    {
        var p = P with { Holidays = [Holiday("2026-10-19", "2026-10-20")] }; // Mon and Tue
        var tue1pm = Syd("2026-10-20 13:00");
        Assert.Empty(SettingsRules.CheckLockNow(p, tue1pm, tue1pm.AddHours(1), Sydney, false));
        Assert.Empty(SettingsRules.CheckRelease(p, tue1pm, Sydney, false));
        Assert.Empty(SettingsRules.CheckSkip(p, tue1pm, Sydney, false));
        // Inclusive at both ends: Monday counts, and Wednesday is a school day again.
        Assert.Empty(SettingsRules.CheckRelease(p, Syd("2026-10-19 08:30"), Sydney, false));
        Assert.NotEmpty(SettingsRules.CheckRelease(p, Syd("2026-10-21 08:30"), Sydney, false));
        // A lock-down from the last holiday evening can't run into the first school morning.
        var tue10pm = Syd("2026-10-20 22:00");
        Assert.Contains(SettingsRules.CheckLockNow(p, tue10pm, Syd("2026-10-21 09:00"), Sydney, false), e => e.Contains("must end by 8:30 am"));
        // Holiday dates are the Mac's: 23:00 UTC Monday is already Tuesday in Sydney, not yet in UTC.
        Assert.Empty(SettingsRules.CheckRelease(P with { Holidays = [Holiday("2026-10-20", "2026-10-20")] },
            DateTimeOffset.Parse("2026-10-19T23:00:00Z"), Sydney, false));
        Assert.NotEmpty(SettingsRules.CheckRelease(P with { Holidays = [Holiday("2026-10-20", "2026-10-20")] },
            DateTimeOffset.Parse("2026-10-20T23:00:00Z"), Sydney, false));
        // Weekly downtime repeats into term, so holidays don't loosen it.
        Assert.NotEmpty(SettingsRules.CheckForParent(WindowKind.SchoolNights, W("10:00", "12:00"), p));
    }

    [Fact]
    public void HolidaysMustBeInOrderUpcomingAndApart()
    {
        var today = DateOnly.Parse("2026-10-22");
        IReadOnlyList<SchoolHoliday> existing = [Holiday("2026-12-18", "2027-01-27")];
        Assert.Empty(SettingsRules.CheckHoliday(Holiday("2026-10-22", "2026-10-22"), existing, today)); // one day, today
        Assert.Empty(SettingsRules.CheckHoliday(Holiday("2026-10-01", "2026-10-21"), existing, today)); // a day's grace across time zones
        Assert.Contains(SettingsRules.CheckHoliday(Holiday("2026-10-01", "2026-10-20"), existing, today), e => e.Contains("already over"));
        Assert.Contains(SettingsRules.CheckHoliday(Holiday("2026-11-02", "2026-11-01"), existing, today), e => e.Contains("before its first day"));
        Assert.Contains(SettingsRules.CheckHoliday(Holiday("2027-01-27", "2027-01-30"), existing, today), e => e.Contains("overlap Break"));
        Assert.Contains(SettingsRules.CheckPolicy(P with { Holidays = [Holiday("2026-11-02", "2026-11-01")] }), e => e.Contains("on or after"));
        Assert.Equal("Fri 18 Dec 2026 – Wed 27 Jan 2027", existing[0].Dates);
        Assert.Equal("Mon 2 Nov – Fri 6 Nov 2026", Holiday("2026-11-02", "2026-11-06").Dates);
    }

    [Fact]
    public void LockUntilATime()
    {
        var now = DateTimeOffset.Parse("2026-10-17T02:00:00Z"); // 13:00 Sydney
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T05:30:00Z"), SettingsRules.NextOccurrence(new(16, 30), now, Sydney));
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T20:00:00Z"), SettingsRules.NextOccurrence(new(7, 0), now, Sydney)); // tomorrow
    }

    [Fact]
    public void SchoolScheduleDescriptions()
    {
        Lull.Portal.Api.ScheduleSummary[] profile =
        [
            new("School night", ["Mon", "Tue", "Wed", "Thu", "Fri", "Sun"], "21:30", "06:30"),
            new("Late Saturday", ["Sat"], "23:30", "09:00"),
        ];
        Assert.Equal("Sun–Thu 9:30 pm – 6:30 am", SchoolSchedule.Describe(WindowKind.SchoolNights, profile, true));
        Assert.Equal("Fri 9:30 pm – 6:30 am; Sat 11:30 pm – 9:00 am", SchoolSchedule.Describe(WindowKind.Weekend, profile, true));
        Assert.Equal("no downtime", SchoolSchedule.Describe(WindowKind.Weekend, [profile[0] with { Days = ["Mon"] }], true));
        Assert.Equal("shown once the Mac is online", SchoolSchedule.Describe(WindowKind.Weekend, null, false));
        Assert.Equal(W("21:30", "06:30"), SchoolSchedule.Suggestion(WindowKind.SchoolNights, profile));
        Assert.Equal(W("23:00", "08:00"), SchoolSchedule.Suggestion(WindowKind.Weekend, []));
        Assert.Equal("Mon–Fri", SchoolSchedule.Days(["Fri", "Mon", "Tue", "Wed", "Thu"]));
        Assert.Equal("Fri, Sat", SchoolSchedule.Days(["Sat", "Fri"]));
    }

    [Fact]
    public void RevisionsAlwaysIncrease()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        Assert.Equal(1_760_000_000_000, SettingsRules.NextRevision(5, now));
        Assert.Equal(1_760_000_000_001, SettingsRules.NextRevision(1_760_000_000_000, now));
    }
}
