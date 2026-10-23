using System.IO;
using System.Text.Json.Serialization;

namespace GitManager.App.Infrastructure;

public sealed record RepositoryEntry(string Path, string? DisplayName = null)
{
    [JsonIgnore]
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? DefaultName : DisplayName;
    [JsonIgnore]
    public string DefaultName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;

    public static string NormalizePath(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public static bool SamePath(string? left, string? right) => left is not null && right is not null
        && string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}
