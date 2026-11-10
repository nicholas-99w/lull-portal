using Lull.Portal.Domain;

namespace Lull.Portal.Pages;

/// <summary>
/// What <c>_WeekGrid.cshtml</c> shows: one advanced schedule as a checkbox per hour, posted as
/// <paramref name="Name"/>. <paramref name="School"/> hours (null for holidays) can't be chosen,
/// and hours partly in them are drawn half and half.
/// </summary>
public sealed record WeekGridView(string Name, WeekGrid Grid, SchoolHours? School)
{
    /// <summary>"12 am", "9 am", "3 pm".</summary>
    public static string Hour(int hour) => hour switch
    {
        0 => "12 am",
        < 12 => $"{hour} am",
        12 => "12 pm",
        _ => $"{hour - 12} pm",
    };

    /// <summary>What a screen reader says for a cell: "Mon 3 pm, from 3:30 pm".</summary>
    public string Label(int day, int hour, SchoolInHour part) => $"{WeekGrid.DayNames[day]} {Hour(hour)}" + part switch
    {
        SchoolInHour.FirstPart => $", from {Window.Friendly(School!.End)}",
        SchoolInHour.LastPart => $", until {Window.Friendly(School!.Start)}",
        _ => "",
    };

    /// <summary>Whether any hour is partly school hours, for the key.</summary>
    public bool HasPartHours => School is { } s && (s.Start.Minute != 0 || s.End.Minute != 0);
}
