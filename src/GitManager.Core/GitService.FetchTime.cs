namespace GitManager.Core;

public sealed partial class GitService
{
    public async Task<DateTimeOffset?> GetLastFetchTimeAsync(string path, CancellationToken ct = default)
    {
        var root = System.IO.Path.GetFullPath(path);
        var fetchHead = (await Run(root, ct, "rev-parse", "--git-path", "FETCH_HEAD")).Output.TrimEnd('\r', '\n');
        var absolute = System.IO.Path.GetFullPath(fetchHead, root);
        try { return File.Exists(absolute) ? new DateTimeOffset(File.GetLastWriteTimeUtc(absolute)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
