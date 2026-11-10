using Lull.Portal.Domain;

namespace Lull.Portal.Pages;

/// <summary>What <c>_Pager.cshtml</c> shows under an admin list: the range shown and links to the pages around it.</summary>
/// <param name="Noun">Plural, e.g. "Macs".</param>
/// <param name="Filter">The list's filter, in the links' <c>f</c>, or null for all.</param>
public sealed record Pager(string Path, string? Query, int Number, int Pages, int From, int To, int Total, string Noun, string? Filter = null)
{
    public static Pager For<T>(Paged<T> page, string path, string? query, string noun, string? filter = null) =>
        new(path, query, page.Number, page.Pages, page.From, page.To, page.Total, noun, filter);

    /// <summary>The link to page <paramref name="number"/>, keeping the search and the filter.</summary>
    public string Link(int number) => ListLink.To(Path, Query, Filter, number);
}

/// <summary>One view of an admin list, in <c>_Filters.cshtml</c>.</summary>
public sealed record FilterChip(string Label, int Count, string Href, bool Current);

public static class ListLink
{
    /// <summary>An admin list's address: <c>/Admin/Devices?q=smith&amp;f=offline&amp;p=2</c>, with only what's set.</summary>
    public static string To(string path, string? query, string? filter, int? page = null)
    {
        var parts = new List<string>();
        if (query is { Length: > 0 }) parts.Add($"q={Uri.EscapeDataString(query)}");
        if (filter is { Length: > 0 }) parts.Add($"f={Uri.EscapeDataString(filter)}");
        if (page is { } p) parts.Add($"p={p}");
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }
}
