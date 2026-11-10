using Lull.Portal.Api;

namespace Lull.Portal.Domain;

/// <summary>
/// The school's baseline schedule as the Mac reports it (its Jamf profile's
/// `Schedules`, which IT may set per year or device group), viewed per kind of
/// window so parents can see what "follow the school" means for this Mac.
/// </summary>
public static class SchoolSchedule
{
    static readonly string[] MondayFirst = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    static string Day(DayOfWeek d) => MondayFirst[((int)d + 6) % 7];

    /// <summary>Profile rules starting on this kind's days, restricted to those days.</summary>
    public static IReadOnlyList<ScheduleSummary> For(WindowKind kind, IEnumerable<ScheduleSummary>? profile)
    {
        var kindDays = SettingsRules.StartDays(kind).Select(Day).ToHashSet();
        return (profile ?? [])
            .Select(r => r with { Days = r.Days.Where(kindDays.Contains).ToArray() })
            .Where(r => r.Days.Length > 0)
            .ToList();
    }

    /// <summary>"Mon–Thu 9:30 pm – 6:30 am; Fri 10:00 pm – 7:00 am", or a note when the school has nothing on those days.</summary>
    public static string Describe(WindowKind kind, IEnumerable<ScheduleSummary>? profile, bool known)
    {
        if (!known) return "shown once the Mac is online";
        var rules = For(kind, profile);
        return rules.Count == 0 ? "no downtime" : string.Join("; ", rules.Select(r => $"{Days(r.Days)} {Friendly(r.Start)} – {Friendly(r.End)}"));
    }

    /// <summary>The profile's "21:30" as "9:30 pm" (as reported, if it isn't HH:mm).</summary>
    static string Friendly(string time) => Window.TryParseTime(time, out var t) ? Window.Friendly(t) : time;

    /// <summary>
    /// What this Mac follows on these days, for the parent's card: "no downtime",
    /// or the parent's or school's times ("Sun–Thu 9:30 pm – 6:30 am").
    /// </summary>
    /// <remarks>An advanced schedule shows its windows starting on these days, outside <paramref name="school"/> hours.</remarks>
    public static string Current(WindowKind kind, DeviceSettings settings, IEnumerable<ScheduleSummary>? profile, bool known, SchoolHours school) =>
        settings.Week is { } week ? week.Describe(school, SettingsRules.StartDays(kind))
        : settings.ModeFor(kind) switch
        {
            ScheduleMode.OptOut => "no downtime",
            ScheduleMode.Custom => $"{Days(SettingsRules.StartDays(kind).Select(Day))} {settings.For(kind)!.Value.Friendly()}",
            _ when !known => "times shown once the Mac is online",
            _ => Describe(kind, profile, known),
        };

    /// <summary>
    /// The kind of window that applies now on the Mac's clock: the one in
    /// progress (by the day it started), else by the evening we're in. Until
    /// noon counts as the previous evening, so Saturday morning is still the
    /// weekend and Monday morning still a school night.
    /// </summary>
    public static WindowKind ActiveKind(CheckIn? report, DateTimeOffset now, TimeZoneInfo tz)
    {
        var day = report is { Phase: Phase.Locked, WindowStart: { } start } && start <= now
            ? TimeZoneInfo.ConvertTime(start, tz).DayOfWeek
            : TimeZoneInfo.ConvertTime(now, tz).AddHours(-12).DayOfWeek;
        return SettingsRules.StartDays(WindowKind.SchoolNights).Contains(day) ? WindowKind.SchoolNights : WindowKind.Weekend;
    }

    /// <summary>
    /// The school-week windows the simple settings give (the school's as last reported, none
    /// if it isn't known yet): where an advanced schedule starts from.
    /// </summary>
    public static IEnumerable<WeekWindow> Simple(DeviceSettings settings, IEnumerable<ScheduleSummary>? profile) =>
        new[] { WindowKind.SchoolNights, WindowKind.Weekend }.SelectMany(kind => settings.ModeFor(kind) switch
        {
            ScheduleMode.OptOut => [],
            ScheduleMode.Custom => SettingsRules.StartDays(kind).Select(d => new WeekWindow(d, settings.For(kind)!.Value.Start, settings.For(kind)!.Value.End)),
            _ => For(kind, profile).SelectMany(r =>
                Window.TryParseTime(r.Start, out var start) && Window.TryParseTime(r.End, out var end) && start != end
                    ? r.Days.Select(d => new WeekWindow((DayOfWeek)((Array.IndexOf(MondayFirst, d) + 1) % 7), start, end))
                    : []),
        });

    /// <summary>Times to pre-fill "Customise" with: the school's own, else a sensible evening.</summary>
    public static Window Suggestion(WindowKind kind, IEnumerable<ScheduleSummary>? profile)
    {
        var first = For(kind, profile).FirstOrDefault();
        if (first is not null && Window.TryParseTime(first.Start, out var s) && Window.TryParseTime(first.End, out var e) && s != e)
            return new Window(s, e);
        return kind == WindowKind.SchoolNights ? new(new(21, 30), new(6, 30)) : new(new(23, 0), new(8, 0));
    }

    /// <summary>"Sun–Thu", "Mon–Fri", "Fri, Sat" or "Mon, Wed": runs of 3+ days (wrapping past Sunday) as ranges.</summary>
    public static string Days(IEnumerable<string> days)
    {
        var set = days.Select(d => Array.IndexOf(MondayFirst, d)).Where(i => i >= 0).ToHashSet();
        if (set.Count is >= 3 and < 7)
        {
            // A run is contiguous round the week if exactly one member has no predecessor.
            var starts = set.Where(i => !set.Contains((i + 6) % 7)).ToList();
            if (starts.Count == 1)
                return $"{MondayFirst[starts[0]]}–{MondayFirst[(starts[0] + set.Count - 1) % 7]}";
        }
        if (set.Count == 7) return "every day";
        return string.Join(", ", set.Order().Select(i => MondayFirst[i]));
    }
}
