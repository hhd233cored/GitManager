using System.IO.Enumeration;

namespace GitManager.Services;

// One policy is enforced at the service boundary for both the local agent and MCP.
public sealed record RepositoryContentPolicy(bool AllowDiff = true, bool AllowProject = true, string ExcludedPaths = "")
{
    private static readonly string[] ExcludedDirectories = [".git", ".svn", ".hg", ".aws", ".ssh", ".azure", "node_modules", "bin", "obj", "artifacts", "dist", "build", ".venv", "venv", "__pycache__", ".next"];

    public bool Excludes(string relative)
    {
        relative = relative.Replace('\\', '/');
        var parts = relative.Split('/');
        if (parts.Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase))) return true;
        if (parts.Any(RepositoryTools.IsSensitivePath)) return true;
        foreach (var raw in ExcludedPaths.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Replace('\\', '/').Trim('/');
            if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase)
                || FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true)
                || !pattern.Contains('/') && parts.Any(part => FileSystemName.MatchesSimpleExpression(pattern, part, ignoreCase: true))) return true;
        }
        return false;
    }
}
