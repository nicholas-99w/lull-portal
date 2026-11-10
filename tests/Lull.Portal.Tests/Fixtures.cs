using System.Text.Json;
using System.Text.Json.Nodes;
using Lull.Portal.Api;

namespace Lull.Portal.Tests;

/// <summary>
/// The device API's JSON fixtures (contract/fixtures). The Mac repository's
/// Swift tests decode copies of the same files (contract/PORTAL_API.md).
/// </summary>
public static class Fixtures
{
    /// <summary>The repository root: the folder holding Lull.Portal.slnx.</summary>
    public static string RepoRoot { get; } = FindRoot();

    public static string Directory { get; } = Path.Combine(RepoRoot, "contract", "fixtures");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lull.Portal.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Lull.Portal.slnx not found above " + AppContext.BaseDirectory);
    }

    public static string Text(string name) => File.ReadAllText(Path.Combine(Directory, name));

    public static JsonNode Node(string name) => JsonNode.Parse(Text(name))!;

    public static T Read<T>(string name) => JsonSerializer.Deserialize<T>(Text(name), Wire.Options)!;
}
