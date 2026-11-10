using System.Text.RegularExpressions;

namespace Lull.Portal.Domain;

/// <summary>
/// Student usernames, as IT links them to a parent account (one by one or by
/// CSV import). Matched against a Mac's pinned primary user (its local short
/// username).
/// </summary>
public static partial class ChildList
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ValidUsername();

    /// <summary>
    /// Lowercases and de-duplicates. Accepts commas, semicolons, spaces or new
    /// lines between names, and strips an @domain so a UPN matches the Mac's
    /// short name.
    /// </summary>
    public static (IReadOnlyList<string> Usernames, IReadOnlyList<string> Invalid) Parse(string? input)
    {
        var names = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in (input ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Normalize(raw);
            if (!ValidUsername().IsMatch(name)) invalid.Add(raw);
            else if (!names.Contains(name)) names.Add(name);
        }
        return (names, invalid);
    }

    public static string Normalize(string username)
    {
        var s = username.Trim().ToLowerInvariant();
        var at = s.IndexOf('@');
        return at > 0 ? s[..at] : s;
    }

    /// <summary>Whether a parent may see and change a device.</summary>
    public static bool Owns(IReadOnlyCollection<string> children, string? primaryUsername) =>
        primaryUsername is not null && children.Contains(Normalize(primaryUsername));
}
