using System.Security.Cryptography;
using System.Text;

namespace GitManager.Core;

public sealed partial class GitService
{
    public async Task<IReadOnlyList<CommitInfo>> GetHistorySliceAsync(string path, int skip, int limit, CancellationToken ct = default)
    {
        var info = await GetRepositoryAsync(path, ct);
        if (!info.HasHead) return [];
        return ParseHistory((await Run(path, ct, "log", "--topo-order", "--no-show-signature", "--decorate=short",
            "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00%D%x00", $"--skip={Math.Clamp(skip, 0, 100000)}",
            $"--max-count={Math.Clamp(limit, 1, 50)}", "HEAD", "--")).Output);
    }

    public async Task<string> GetWorkspaceFingerprintAsync(string path, CancellationToken ct = default)
    {
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => fingerprint.AppendData(Encoding.UTF8.GetBytes(text + '\0'));
        var info = await GetRepositoryAsync(path, ct);
        Add(info.Branch);
        Add(info.HasHead ? await ResolveCommitAsync(path, "HEAD", ct) : "unborn");
        var status = await GetStatusAsync(path, ct);
        Add((await Run(path, ct, "ls-files", "--stage", "-z")).Output);
        foreach (var file in status.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            Add($"{file.IndexStatus}{file.WorkTreeStatus}:{file.OldPath}:{file.Path}");
            var absolute = ValidateFilePath(path, file.Path);
            if (File.Exists(absolute))
            {
                if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) Add(new FileInfo(absolute).LinkTarget ?? "link");
                else
                {
                    await using var input = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
                    Add(Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
                }
            }
            else Add(Directory.Exists(absolute) ? "directory" : "missing");
        }
        return Convert.ToHexString(fingerprint.GetHashAndReset());
    }
}
