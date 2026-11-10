using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Lull.Portal.Pages;

/// <summary>
/// One Mac's downtime schedule, on two pages, as a parent thinks of them: school weeks, and school
/// holidays (/Schedule/Holidays). Each saves only what it shows, and keeps the other as it is.
/// </summary>
public sealed class DeviceScheduleModel(PortalStore store, UserStore users, TimeProvider clock) : DevicePageModel(store, users, clock)
{
    public List<string> Errors { get; } = [];

    /// <summary>This is the school holidays page, not the school weeks one.</summary>
    public bool IsHolidays => RouteData.Values["section"] is not null;

    /// <summary>The school's (Jamf profile's) times for these days on this Mac, as last reported.</summary>
    public string SchoolText(WindowKind kind) =>
        SchoolSchedule.Describe(kind, Device.LastReport?.ProfileSchedules, Device.LastReport?.ProfileSchedules is not null);

    /// <summary>What the time fields show: the parent's times, else the school's to start from.</summary>
    public Window Shown(WindowKind kind) =>
        Device.Settings.For(kind) ?? SchoolSchedule.Suggestion(kind, Device.LastReport?.ProfileSchedules);

    /// <summary>The holiday times shown: the parent's, else the weekend's to start from.</summary>
    public Window ShownHoliday => Device.Settings.Holiday ?? Shown(WindowKind.Weekend);

    /// <summary>The Mac last reported an older Lull, which keeps its usual settings through holidays.</summary>
    public bool HolidayUnsupported =>
        Device.LastReport?.Version is { } v && Version.TryParse(v, out var version) && version < HolidayVersion;

    /// <summary>The Mac last reported an older Lull, which applies the simple settings instead.</summary>
    public bool AdvancedUnsupported =>
        Device.LastReport?.Version is { } v && Version.TryParse(v, out var version) && version < AdvancedVersion;

    /// <summary>
    /// The school-week grid shown: the parent's advanced schedule, else what the simple
    /// settings give, to start from (to the nearest hour, outside school hours).
    /// </summary>
    public WeekGrid TermGrid => Device.Settings.Week
        ?? WeekGrid.Covering(SchoolSchedule.Simple(Device.Settings, Device.LastReport?.ProfileSchedules)).WithoutSchool(Policy.SchoolHours);

    /// <summary>The holiday grid shown: the parent's, else what the holiday choice gives, to start from.</summary>
    public WeekGrid HolidayGrid => Device.Settings.HolidayWeek ?? Device.Settings.HolidayMode switch
    {
        ScheduleMode.OptOut => WeekGrid.Empty,
        ScheduleMode.Custom => WeekGrid.Covering(Enum.GetValues<DayOfWeek>().Select(d => new WeekWindow(d, ShownHoliday.Start, ShownHoliday.End))),
        _ => TermGrid,
    };

    /// <summary>The simple holiday choice: what applies without an hour-by-hour schedule, which keeps it for going back.</summary>
    public ScheduleMode HolidayChoice =>
        Device.Settings.HolidayOff ? ScheduleMode.OptOut : Device.Settings.Holiday is null ? ScheduleMode.School : ScheduleMode.Custom;

    /// <summary>The holiday under way or next on the Mac's clock, if IT has set one.</summary>
    public SchoolHoliday? NextHoliday { get; private set; }
    public bool HolidayUnderWay { get; private set; }

    protected override async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        if (!await base.LoadAsync(id, ct)) return false;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(LoadedAt, Zone).DateTime);
        NextHoliday = Policy.Holidays.Where(h => h.Last >= today).MinBy(h => h.First);
        HolidayUnderWay = NextHoliday?.Contains(today) == true;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        // De-registered, it takes no settings: its own page says so.
        return Device.IsDeregistered ? RedirectToPage("/Device", new { id }) : Page();
    }

    static ScheduleMode Mode(string? value) => value switch
    {
        "off" => ScheduleMode.OptOut,
        "custom" => ScheduleMode.Custom,
        _ => ScheduleMode.School,
    };

    /// <param name="termView">"advanced" for the school-week grid (<paramref name="week"/>), else the simple choices.</param>
    /// <param name="schoolMode">"off" (opt out), "school" (follow the Jamf schedule) or "custom".</param>
    /// <param name="holidayView">"advanced" for the holiday grid (<paramref name="holidayWeek"/>), else the simple choices.</param>
    /// <param name="holidayMode">"off", "school" (the usual settings) or "custom".</param>
    /// <param name="week">The grid's downtime cells (<see cref="WeekGrid"/>).</param>
    public async Task<IActionResult> OnPostAsync(
        Guid id, long? revision, string? termView, string? schoolMode, string? schoolStart, string? schoolEnd,
        string? weekendMode, string? weekendStart, string? weekendEnd,
        string? holidayView, string? holidayMode, string? holidayStart, string? holidayEnd, int[]? week, int[]? holidayWeek, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (Stale(revision, Errors)) return Page();
        var s = Device.Settings;
        // What this page doesn't show stays as it is, unchecked: someone else may have set it, outside what this person may.
        var (sm, wm) = (s.ModeFor(WindowKind.SchoolNights), s.ModeFor(WindowKind.Weekend));
        var (school, weekend, termGrid) = (s.SchoolNights, s.Weekend, s.Week);
        var hm = HolidayChoice;
        var (holiday, holidayGrid) = (s.Holiday, s.HolidayWeek);

        if (IsHolidays)
        {
            if (holidayView == "advanced")
            {
                // An advanced schedule keeps the simple choice as it was (the form's is hidden, and not checked).
                holidayGrid = ParseGrid(SettingsRules.HolidayLabel, holidayWeek);
            }
            else
            {
                hm = Mode(holidayMode);
                holidayGrid = null;
                holiday = hm == ScheduleMode.Custom ? ParseWindow(SettingsRules.HolidayLabel, holidayStart, holidayEnd) : null;
                if (Errors.Count > 0) return Page();
                // Holiday times have no school hours to keep out of.
                if (holiday is { } h) Errors.AddRange(SettingsRules.CheckHolidayShape(h));
            }
        }
        else if (termView == "advanced")
        {
            // An advanced schedule keeps the simple choices as they were (the form's are hidden, and not checked).
            termGrid = ParseGrid(SettingsRules.AdvancedLabel, week);
            if (Errors.Count > 0) return Page();
            // School hours apply to everyone's grid: they're taken out of its windows (WeekGrid.Windows) whatever it says.
            Errors.AddRange(SettingsRules.CheckWeek(termGrid!, Policy.SchoolHours));
        }
        else
        {
            (sm, wm, termGrid) = (Mode(schoolMode), Mode(weekendMode), null);
            school = sm == ScheduleMode.Custom ? ParseWindow(SettingsRules.Label(WindowKind.SchoolNights), schoolStart, schoolEnd) : null;
            weekend = wm == ScheduleMode.Custom ? ParseWindow(SettingsRules.Label(WindowKind.Weekend), weekendStart, weekendEnd) : null;
            if (Errors.Count > 0) return Page();
            // Only customised times are checked (the school's own schedule is IT's), and only as posted.
            foreach (var (kind, window) in new[] { (WindowKind.SchoolNights, school), (WindowKind.Weekend, weekend) })
                if (window is { } w)
                    Errors.AddRange(IsAdmin ? SettingsRules.CheckShape(kind, w) : SettingsRules.CheckForParent(kind, w, Policy));
        }
        if (Errors.Count > 0) return Page();

        var cancelled = Controls.SkippingAt is not null ? "skip" : Controls.ReleasedUntil is not null ? "unlock" : null;
        if (await Store.SaveSettingsAsync(
                id, Device.Settings.Revision, sm, school, wm, weekend, hm, holiday, termGrid, holidayGrid, DeviceAccess.Actor(User), ct)
            is not { } result)
            return await ChangedMeanwhileAsync(id, Errors, ct);
        Notice = result.ReleaseCancelled && cancelled is not null
            ? $"Saved. The {cancelled} was cancelled: it was for the old times. Applies within a few minutes."
            : "Saved. Applies within a few minutes.";
        return RedirectToPage("/Device", new { id });
    }

    WeekGrid? ParseGrid(string label, int[]? cells)
    {
        if (WeekGrid.FromCells(cells ?? []) is { } grid) return grid;
        Errors.Add($"{label}: that isn't an hour of the week. Reload the page and try again.");
        return null;
    }

    Window ParseWindow(string label, string? start, string? end)
    {
        if (!Window.TryParseTime(start, out var s) || !Window.TryParseTime(end, out var e))
        {
            Errors.Add($"{label}: enter start and end times.");
            return default;
        }
        return new Window(s, e);
    }
}
