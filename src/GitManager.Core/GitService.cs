using System.Globalization;
using System.Text;

namespace GitManager.Core;

public sealed class GitService(GitRunner? runner = null)
{
    private readonly GitRunner _runner = runner ?? new GitRunner();

    private Task<GitResult> Run(string path, CancellationToken ct, params string[] args) => _runner.RunAsync(path, args, ct);

    public async Task<RepositoryInfo> GetRepositoryAsync(string path, CancellationToken ct = default)
    {
        var root = (await Run(path, ct, "rev-parse", "--show-toplevel")).Output.TrimEnd('\r', '\n');
        var head = await _runner.RunAsync(root, ["rev-parse", "--verify", "HEAD"], ct, false);
        var branchResult = await _runner.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, false);
        var branch = branchResult.ExitCode == 0 ? branchResult.Output.Trim() : $"分离的 HEAD · {head.Output.Trim()[..Math.Min(8, head.Output.Trim().Length)]}";
        var upstream = await _runner.RunAsync(root, ["rev-parse", "--abbrev-ref", "@{upstream}"], ct, false);
        var ahead = 0;
        var behind = 0;
        if (upstream.ExitCode == 0)
        {
            var counts = (await Run(root, ct, "rev-list", "--left-right", "--count", "HEAD...@{upstream}")).Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length == 2) { int.TryParse(counts[0], out ahead); int.TryParse(counts[1], out behind); }
        }
        var remotes = (await Run(root, ct, "remote")).Output;
        return new RepositoryInfo(root, branch, upstream.ExitCode == 0 ? upstream.Output.Trim() : null,
            ahead, behind, head.ExitCode == 0, !string.IsNullOrWhiteSpace(remotes));
    }

    public async Task<IReadOnlyList<CommitInfo>> GetHistoryAsync(string path, bool allBranches, int limit,
        CancellationToken ct = default)
    {
        var head = await _runner.RunAsync(path, ["rev-parse", "--verify", "HEAD"], ct, false);
        var args = new List<string> { "log", "--topo-order", $"--max-count={limit}", "--no-show-signature",
            "--decorate=short", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00%D%x00" };
        if (allBranches) args.Add("--all");
        if (head.ExitCode == 0) args.Add("HEAD");
        else if (!allBranches) return [];
        var result = await _runner.RunAsync(path, args, ct, false);
        if (result.ExitCode != 0)
        {
            if (head.ExitCode != 0) return [];
            throw new GitException(result.Error.Trim(), result.ExitCode);
        }
        return ParseHistory(result.Output);
    }

    public static IReadOnlyList<CommitInfo> ParseHistory(string output)
    {
        var tokens = output.Split('\0');
        var commits = new List<CommitInfo>();
        for (var i = 0; i + 7 < tokens.Length; i += 8)
        {
            var id = tokens[i].TrimStart('\r', '\n');
            if (id.Length == 0) continue;
            commits.Add(new CommitInfo(id, tokens[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                tokens[i + 2], tokens[i + 3], DateTimeOffset.Parse(tokens[i + 4], CultureInfo.InvariantCulture),
                tokens[i + 5], tokens[i + 6].TrimEnd('\r', '\n'), tokens[i + 7]));
        }
        return commits;
    }

    public async Task<IReadOnlyList<ChangedFile>> GetStatusAsync(string path, CancellationToken ct = default) =>
        ParseStatus((await Run(path, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all")).Output);

    public static IReadOnlyList<ChangedFile> ParseStatus(string output)
    {
        var entries = output.Split('\0');
        var files = new List<ChangedFile>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            string? oldPath = null;
            if ((entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < entries.Length)
                oldPath = entries[++i];
            files.Add(new ChangedFile(entry[3..], oldPath, entry[0], entry[1]));
        }
        return files;
    }

    public async Task<IReadOnlyList<ChangedFile>> GetCommitFilesAsync(string path, CommitInfo commit, CancellationToken ct = default)
    {
        var args = new List<string> { "diff-tree", "--no-commit-id", "--name-status", "-r", "-z", "-M" };
        if (commit.Parents.Count > 0) { args.Add(commit.Parents[0]); args.Add(commit.Id); }
        else { args.Add("--root"); args.Add(commit.Id); }
        return ParseCommitFiles((await _runner.RunAsync(path, args, ct)).Output);
    }

    public static IReadOnlyList<ChangedFile> ParseCommitFiles(string output)
    {
        var parts = output.Split('\0');
        var files = new List<ChangedFile>();
        for (var i = 0; i + 1 < parts.Length;)
        {
            var status = parts[i++];
            if (status.Length == 0) break;
            var firstPath = parts[i++];
            if (status[0] is 'R' or 'C' && i < parts.Length)
                files.Add(new ChangedFile(parts[i++], firstPath, status[0], ' ', true));
            else files.Add(new ChangedFile(firstPath, null, status[0], ' ', true));
        }
        return files;
    }

    public async Task<string> GetDiffAsync(string path, ChangedFile file, CommitInfo? commit,
        bool staged, CancellationToken ct = default)
    {
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=4", "-M" };
        if (commit is not null)
        {
            if (commit.Parents.Count > 0) { args.Add(commit.Parents[0]); args.Add(commit.Id); }
            else
            {
                args = ["show", "--format=", "--no-show-signature", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=4", commit.Id];
            }
        }
        else if (file.IsUntracked)
        {
            args.Add("--no-index");
            args.Add("--");
            args.Add("/dev/null");
            args.Add(file.Path);
            var untracked = await _runner.RunAsync(path, args, ct, false);
            if (untracked.ExitCode > 1) throw new GitException(untracked.Error.Trim(), untracked.ExitCode);
            return untracked.Output;
        }
        else if (staged) args.Add("--cached");
        args.Add("--");
        if (file.OldPath is not null) args.Add(file.OldPath);
        args.Add(file.Path);
        return (await _runner.RunAsync(path, args, ct)).Output;
    }

    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(string path, CancellationToken ct = default)
    {
        var result = await Run(path, ct, "for-each-ref", "--format=%(refname:short)%00%(HEAD)%00%(refname)", "refs/heads", "refs/remotes");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\0'))
            .Where(parts => parts.Length == 3 && !parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(parts => new BranchInfo(parts[0], parts[1] == "*", parts[2].StartsWith("refs/remotes/", StringComparison.Ordinal)))
            .OrderByDescending(branch => branch.IsCurrent).ThenBy(branch => branch.IsRemote).ThenBy(branch => branch.Name).ToArray();
    }

    public Task StageAsync(string path, ChangedFile? file = null, CancellationToken ct = default)
    {
        var args = new List<string> { "add", "--all", "--" };
        if (file is not null)
        {
            if (file.OldPath is not null) args.Add(file.OldPath);
            args.Add(file.Path);
        }
        else args.Add(".");
        return _runner.RunAsync(path, args, ct);
    }

    public async Task UnstageAsync(string path, ChangedFile? file = null, CancellationToken ct = default)
    {
        var head = await _runner.RunAsync(path, ["rev-parse", "--verify", "HEAD"], ct, false);
        var args = head.ExitCode == 0 ? new List<string> { "restore", "--staged", "--" } : ["rm", "--cached", "-r", "-f", "--"];
        if (file is not null)
        {
            if (file.OldPath is not null) args.Add(file.OldPath);
            args.Add(file.Path);
        }
        else args.Add(".");
        await _runner.RunAsync(path, args, ct);
    }

    public Task CommitAsync(string path, string summary, string description, CancellationToken ct = default)
    {
        var args = new List<string> { "commit", "-m", summary };
        if (!string.IsNullOrWhiteSpace(description)) { args.Add("-m"); args.Add(description); }
        return _runner.RunAsync(path, args, ct);
    }

    public Task FetchAsync(string path, CancellationToken ct = default) => Run(path, ct, "fetch", "--all", "--prune");
    public Task PullAsync(string path, CancellationToken ct = default) => Run(path, ct, "pull", "--ff-only");
    public async Task PushAsync(string path, CancellationToken ct = default)
    {
        var info = await GetRepositoryAsync(path, ct);
        if (info.Upstream is not null)
        {
            var remote = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.remote")).Output.Trim();
            var target = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.merge")).Output.Trim();
            await Run(path, ct, "push", remote, $"HEAD:{target}");
            return;
        }
        if (info.Branch.StartsWith("分离的 HEAD", StringComparison.Ordinal)) throw new GitException("请先从当前提交创建本地分支，再发布到远程。");
        var remotes = (await Run(path, ct, "remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(remote => remote.Trim()).ToArray();
        var remoteName = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault() ?? throw new GitException("当前仓库没有配置远程地址。");
        await Run(path, ct, "push", "--set-upstream", remoteName, $"HEAD:refs/heads/{info.Branch}");
    }
    public Task SwitchAsync(string path, string branch, CancellationToken ct = default, bool trackRemote = false) =>
        trackRemote ? Run(path, ct, "switch", "--track", branch) : Run(path, ct, "switch", "--", branch);
    public async Task CreateBranchAsync(string path, string name, CancellationToken ct = default)
    {
        await Run(path, ct, "check-ref-format", "--branch", name);
        await Run(path, ct, "switch", "-c", name);
    }
    public Task CloneAsync(string url, string destination, CancellationToken ct = default) =>
        _runner.RunAsync(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!, ["clone", "--", url, destination], ct);

    public async Task<string> ReadBlobAsync(string path, string revision, string filePath, CancellationToken ct = default)
    {
        var output = await Run(path, ct, "show", $"{revision}:{filePath}");
        return output.Output;
    }
}
