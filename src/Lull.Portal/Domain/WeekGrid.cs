namespace Lull.Portal.Domain;

/// <summary>
/// One downtime window of an advanced schedule, as lulld takes it: starting on
/// <see cref="Day"/>, ending the next day if <see cref="End"/> is at or before
/// <see cref="Start"/>; the same time is a whole 24 hours.
/// </summary>
public sealed record WeekWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End)
{
    public bool AllDay => Start == End;
}

/// <summary>How much of an hour of the school week is school hours (<see cref="WeekGrid.School"/>).</summary>
public enum SchoolInHour
{
    None,
    /// <summary>All of it: never downtime.</summary>
    Whole,
    /// <summary>School ends during the hour (3 pm, school until 3:30 pm): downtime there starts when it ends.</summary>
    FirstPart,
    /// <summary>School starts during the hour (8 am, school from 8:30 am): downtime there ends when it starts.</summary>
    LastPart,
}

/// <summary>
/// An advanced schedule: downtime hour by hour across the week, as 168 cells
/// from Monday midnight (Monday's 24 hours, then Tuesday's, ...). Stored as
/// 168 '0'/'1' characters. On school weeks school hours are never downtime: a
/// cell they cover can't be chosen, and in one they cover in part (8 am, when
/// school starts at 8:30) downtime is just the rest of the hour. School hours
/// are applied when the windows are worked out (<see cref="Windows"/>), so the
/// grid itself doesn't change with them.
/// </summary>
public sealed record WeekGrid
{
    public const int Cells = 7 * 24;
    const int Day = 1440, Week = 7 * Day;

    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>168 characters, '1' for downtime.</summary>
    public string Text { get; }

    WeekGrid(string text) => Text = text;

    public static WeekGrid Empty { get; } = new(new string('0', Cells));

    /// <summary>The stored form, or null if it isn't one.</summary>
    public static WeekGrid? Parse(string? text) =>
        text is { Length: Cells } && text.All(c => c is '0' or '1') ? new(text) : null;

    /// <summary>Downtime in these cells (as a form posts them), or null if one isn't a cell.</summary>
    public static WeekGrid? FromCells(IEnumerable<int> cells)
    {
        var on = new char[Cells];
        Array.Fill(on, '0');
        foreach (var c in cells)
        {
            if (c is < 0 or >= Cells) return null;
            on[c] = '1';
        }
        return new(new string(on));
    }

    /// <summary>
    /// The cells mostly covered by these windows (at least half an hour), to start an advanced
    /// schedule from the simple one: 9:30 pm – 6:30 am becomes 9 pm – 7 am.
    /// </summary>
    public static WeekGrid Covering(IEnumerable<WeekWindow> windows)
    {
        var minutes = new bool[Week];
        foreach (var w in windows)
        {
            int start = DayIndex(w.Day) * Day + Minutes(w.Start);
            int length = (Minutes(w.End) - Minutes(w.Start) + Day) % Day;
            if (length == 0) length = Day;
            for (int m = 0; m < length; m++) minutes[(start + m) % Week] = true;
        }
        return FromCells(Enumerable.Range(0, Cells).Where(c => minutes.Skip(c * 60).Take(60).Count(on => on) >= 30))!;
    }

    public bool this[int cell] => Text[cell] == '1';

    public static int Cell(int day, int hour) => day * 24 + hour;

    /// <summary>Monday = 0 ... Sunday = 6.</summary>
    public static int DayIndex(DayOfWeek d) => ((int)d + 6) % 7;

    static DayOfWeek DayOf(int index) => (DayOfWeek)((index + 1) % 7);

    static int Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>School hours as minutes of the week: Monday to Friday.</summary>
    static IEnumerable<(int Start, int End)> SchoolBlocks(SchoolHours h) =>
        Enumerable.Range(0, 5).Select(d => (d * Day + Minutes(h.Start), d * Day + Minutes(h.End)));

    /// <summary>How much of this cell is school hours (none on holidays: <paramref name="school"/> null).</summary>
    public static SchoolInHour School(int cell, SchoolHours? school)
    {
        if (school is null) return SchoolInHour.None;
        int from = cell * 60, to = from + 60;
        foreach (var (start, end) in SchoolBlocks(school))
        {
            if (end <= from || to <= start) continue;
            if (start <= from && to <= end) return SchoolInHour.Whole;
            // Partly: school at the start of the hour, or (also when it's in the middle) later in it.
            return start <= from ? SchoolInHour.FirstPart : SchoolInHour.LastPart;
        }
        return SchoolInHour.None;
    }

    /// <summary>Without the cells wholly in school hours, which can't be downtime.</summary>
    public WeekGrid WithoutSchool(SchoolHours school) =>
        FromCells(Enumerable.Range(0, Cells).Where(c => this[c] && School(c, school) != SchoolInHour.Whole))!;

    /// <summary>Cells wholly in school hours (which parents can't choose).</summary>
    public bool TouchesSchool(SchoolHours school) => this != WithoutSchool(school);

    /// <summary>
    /// The windows lulld enforces: each run of downtime, round the week, outside
    /// <paramref name="school"/> hours (null for holidays). A run is one window
    /// starting where it starts; one of 24 hours or more is split at each midnight,
    /// as a window lasts at most a day. In week order.
    /// </summary>
    public IReadOnlyList<WeekWindow> Windows(SchoolHours? school)
    {
        var on = new bool[Week];
        for (int c = 0; c < Cells; c++)
            if (this[c]) Array.Fill(on, true, c * 60, 60);
        if (school is not null)
            foreach (var (start, end) in SchoolBlocks(school)) Array.Fill(on, false, start, end - start);

        if (on.All(m => m)) return Enumerable.Range(0, 7).Select(d => new WeekWindow(DayOf(d), TimeOnly.MinValue, TimeOnly.MinValue)).ToList();
        // Scan from a minute without downtime, so no run is split where the week wraps.
        int origin = Array.IndexOf(on, false);
        var windows = new List<(int Start, int Length)>();
        for (int i = 0; i < Week; i++)
        {
            int m = (origin + i) % Week;
            if (!on[m]) continue;
            if (i > 0 && on[(m + Week - 1) % Week]) windows[^1] = (windows[^1].Start, windows[^1].Length + 1);
            else windows.Add((m, 1));
        }
        return windows.SelectMany(Split).OrderBy(w => w.Start).Select(w => Window(w.Start, w.Length)).ToList();
    }

    /// <summary>A run under 24 hours as it is, a longer one in pieces at each midnight.</summary>
    static IEnumerable<(int Start, int Length)> Split((int Start, int Length) run)
    {
        if (run.Length < Day)
        {
            yield return run;
            yield break;
        }
        int at = run.Start, end = run.Start + run.Length;
        while (at < end)
        {
            int next = Math.Min(end, (at / Day + 1) * Day);
            yield return (at % Week, next - at);
            at = next;
        }
    }

    static WeekWindow Window(int start, int length) =>
        new(DayOf(start / Day), TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start % Day)),
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes((start + length) % Day)));

    /// <summary>
    /// "Sun–Thu 9:00 pm – 7:00 am; Sat all day", or "no downtime": the windows, those with the
    /// same times together. <paramref name="days"/> limits it to windows starting on those days.
    /// </summary>
    public string Describe(SchoolHours? school, IEnumerable<DayOfWeek>? days = null)
    {
        var only = days?.ToHashSet();
        var groups = Windows(school)
            .Where(w => only is null || only.Contains(w.Day))
            .GroupBy(w => (w.Start, w.End))
            .Select(g => (Days: g.Select(w => w.Day).ToList(), g.Key.Start, g.Key.End))
            .OrderBy(g => g.Days.Min(DayIndex)).ThenBy(g => g.Start)
            .Select(g => $"{SchoolSchedule.Days(g.Days.Select(d => DayNames[DayIndex(d)]))} " +
                (g.Start == g.End ? "all day" : new Window(g.Start, g.End).Friendly()))
            .ToList();
        return groups.Count == 0 ? "no downtime" : string.Join("; ", groups);
    }

    public override string ToString() => Text;
}
