namespace Lull.Portal.Domain;

/// <summary>
/// One page of an admin list (Devices, Users), searched and paged in the
/// database so a school's thousands of rows never load at once.
/// </summary>
/// <param name="Number">The page shown, from 1.</param>
/// <param name="Total">Rows matching the search, on every page.</param>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Number, int Total)
{
    public int Pages => Paging.PageCount(Total);
    /// <summary>1-based position of the first row shown (0 when there are none).</summary>
    public int From => Items.Count == 0 ? 0 : (Number - 1) * Paging.Size + 1;
    public int To => From == 0 ? 0 : From + Items.Count - 1;
}

public static class Paging
{
    public const int Size = 50;

    public static int PageCount(int total) => Math.Max(1, (total + Size - 1) / Size);

    /// <summary>A requested page outside 1..pages shows the nearest one (a stale link after rows were deleted).</summary>
    public static int Clamp(int? requested, int total) => Math.Clamp(requested ?? 1, 1, PageCount(total));

    public static int Offset(int number) => (number - 1) * Size;

    /// <summary>Search text as the stores match it: trimmed, lower-case, empty for none.</summary>
    public static string Normalize(string? query) => (query ?? "").Trim().ToLowerInvariant();
}
