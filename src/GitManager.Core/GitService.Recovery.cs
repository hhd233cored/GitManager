using System.Text;
using System.Text.Json;

namespace GitManager.Core;

public sealed partial class GitService
{
    private const string RecoveryPrefix = "refs/gitmanager/recovery/";
    private async Task<string?> OptionalRevisionAsync(string path, string revision, CancellationToken ct)
    {
        var result = await _runner.RunAsync(path, ["rev-parse", "--verify", "--end-of-options", revision], ct, false);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }
    public async Task<string> ResolveCommitAsync(string path, string revision, CancellationToken ct = default) =>
        await OptionalRevisionAsync(path, revision + "^{commit}", ct) ?? throw new GitException("找不到目标提交，请刷新后重试。");

    private async Task<string> GitPathAsync(string path, string name, CancellationToken ct) =>
        System.IO.Path.GetFullPath((await Run(path, ct, "rev-parse", "--git-path", name)).Output.TrimEnd('\r', '\n'), path);

    public async Task<IReadOnlyList<StashEntry>> GetStashesAsync(string path, CancellationToken ct = default)
    {
        var result = await Run(path, ct, "stash", "list", "--format=%H%x00%gd%x00%gs%x00%aI%x00");
        var parts = result.Output.Split('\0');
        var entries = new List<StashEntry>();
        for (var i = 0; i + 3 < parts.Length; i += 4)
            if (DateTimeOffset.TryParse(parts[i + 3], out var date))
                entries.Add(new StashEntry(parts[i].TrimStart('\r', '\n'), parts[i + 1], parts[i + 2], date));
        return entries;
    }

    public async Task<string> CreateStashAsync(string path, string message, bool includeUntracked = true,
        IReadOnlyList<string>? selectedPaths = null, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        if (selectedPaths is { Count: 0 }) throw new GitException("请至少选择一个要保存的文件。");
        return await PushStashAsync(path, message, includeUntracked, selectedPaths, ct)
            ?? throw new GitException("没有可保存的变更。");
    }

    private async Task<string?> PushStashAsync(string path, string message, bool includeUntracked,
        IReadOnlyList<string>? selectedPaths, CancellationToken ct)
    {
        var status = await GetStatusAsync(path, ct);
        if (!status.Any(file => (includeUntracked || !file.IsUntracked)
            && (selectedPaths is null || selectedPaths.Contains(file.Path) || file.OldPath is not null && selectedPaths.Contains(file.OldPath)))) return null;
        if (status.Any(file => file.IsConflicted)) throw new GitException("请先解决当前冲突，再保存 Stash。");
        await ResolveCommitAsync(path, "HEAD", ct);
        var previous = await OptionalRevisionAsync(path, "refs/stash", ct);
        var args = new List<string> { "stash", "push", "--message", message.Length > 0 ? message : "GitManager 保存的改动" };
        // Full stashes use Git's internal :/ pathspec when cleaning untracked files.
        // Global literal-pathspec mode would prevent that cleanup from taking effect.
        if (selectedPaths is null) args.Insert(0, "--no-literal-pathspecs");
        if (includeUntracked) args.Add("--include-untracked");
        string? input = null;
        if (selectedPaths is not null)
        {
            foreach (var file in selectedPaths) ValidateFilePath(path, file);
            args.AddRange(["--pathspec-from-file=-", "--pathspec-file-nul"]);
            input = string.Join('\0', selectedPaths) + '\0';
        }
        await _runner.RunAsync(path, args, ct, standardInput: input);
        var created = await OptionalRevisionAsync(path, "refs/stash", ct);
        return created != previous ? created : null;
    }

    public async Task DropStashAsync(string path, string id, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        await DropStashByIdAsync(path, id, ct);
    }
    private async Task DropStashByIdAsync(string path, string id, CancellationToken ct)
    {
        // Re-resolve the selector immediately: stash@{n} changes whenever entries are added/removed.
        var entry = (await GetStashesAsync(path, ct)).FirstOrDefault(entry => entry.Id == id);
        if (entry is not null) await Run(path, ct, "stash", "drop", entry.Selector);
    }
    public async Task<IReadOnlyList<ChangedFile>> GetStashFilesAsync(string path, string id, CancellationToken ct = default) =>
        ParseCommitFiles((await Run(path, ct, "stash", "show", "--include-untracked", "--name-status", "-z", id)).Output);
    public async Task<string> GetStashDiffAsync(string path, string id, ChangedFile file, CancellationToken ct = default)
    {
        var parent = await ResolveCommitAsync(path, id + "^1", ct);
        var third = await OptionalRevisionAsync(path, id + "^3", ct);
        if (third is not null && (await _runner.RunAsync(path, ["cat-file", "-e", $"{third}:{file.Path}"], ct, false)).ExitCode == 0)
            return await GetDiffAsync(path, file, new CommitInfo(third, [], "", "", DateTimeOffset.MinValue, "", "", ""), false, ct);
        return await GetDiffAsync(path, file, new CommitInfo(id, [parent], "", "", DateTimeOffset.MinValue, "", "", ""), false, ct);
    }

    private async Task<RecoveryPoint> CreateRecoveryAsync(string path, string reason, bool saveChanges, CancellationToken ct,
        string? headOverride = null)
    {
        var info = await GetRepositoryAsync(path, ct);
        var head = headOverride ?? await ResolveCommitAsync(path, "HEAD", ct);
        var stash = saveChanges ? await PushStashAsync(path, "自动备份 · " + reason, true, null, ct) : null;
        var now = DateTimeOffset.UtcNow;
        var id = now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var point = new RecoveryPoint(id, RecoveryPrefix + id, info.Branch, head, stash, reason, now);
        // Annotated objects retain readable metadata; a separate ref anchors any stash snapshot.
        var contents = $"object {head}\ntype commit\ntag gitmanager-{id}\ntagger GitManager <recovery@localhost> {now.ToUnixTimeSeconds()} +0000\n\n{JsonSerializer.Serialize(point)}\n";
        var tag = (await _runner.RunAsync(path, ["mktag"], ct, standardInput: contents)).Output.Trim();
        if (stash is not null) await Run(path, ct, "update-ref", "refs/gitmanager/snapshots/" + id, stash);
        await Run(path, ct, "update-ref", point.Reference, tag);
        return point;
    }

    public async Task<IReadOnlyList<RecoveryPoint>> GetRecoveryPointsAsync(string path, CancellationToken ct = default)
    {
        var result = await Run(path, ct, "for-each-ref", "--format=%(refname)%00%(contents)%00", RecoveryPrefix);
        var parts = result.Output.Split('\0');
        var entries = new List<RecoveryPoint>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            try
            {
                var point = JsonSerializer.Deserialize<RecoveryPoint>(parts[i + 1]);
                if (point is not null && point.Reference == parts[i].TrimStart('\r', '\n')) entries.Add(point);
            }
            catch (JsonException) { }
        }
        return entries.OrderByDescending(entry => entry.Date).ToArray();
    }
    private async Task ValidateRecoveryAsync(string path, RecoveryPoint point, CancellationToken ct)
    {
        if (point.Reference != RecoveryPrefix + point.Id || await ResolveCommitAsync(path, point.Reference, ct) != point.Head)
            throw new GitException("恢复点的提交记录不完整，请先核对备份。");
        if (point.StashId is not null && await ResolveCommitAsync(path, "refs/gitmanager/snapshots/" + point.Id, ct) != point.StashId)
            throw new GitException("恢复点的文件快照不完整，请先核对备份。");
    }
    private async Task WriteSessionAsync(string path, OperationSession session, CancellationToken ct)
    {
        var target = await GitPathAsync(path, "gitmanager-operation.json", ct);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session), new UTF8Encoding(false), ct);
        File.Move(temporary, target, true);
    }
    private async Task<OperationSession?> ReadSessionAsync(string path, CancellationToken ct)
    {
        var target = await GitPathAsync(path, "gitmanager-operation.json", ct);
        if (!File.Exists(target)) return null;
        try { return JsonSerializer.Deserialize<OperationSession>(await File.ReadAllTextAsync(target, ct)); }
        catch (JsonException) { return null; }
    }
    private async Task ClearSessionAsync(string path, CancellationToken ct) => File.Delete(await GitPathAsync(path, "gitmanager-operation.json", ct));

    public async Task<RepositoryOperation> GetOperationAsync(string path, CancellationToken ct = default)
    {
        var session = await ReadSessionAsync(path, ct);
        var gitDirectory = (await Run(path, ct, "rev-parse", "--absolute-git-dir")).Output.TrimEnd('\r', '\n');
        var kind = RepositoryOperationKind.None;
        foreach (var marker in new[] { ("rebase-merge", RepositoryOperationKind.Rebase), ("rebase-apply", RepositoryOperationKind.Rebase),
            ("MERGE_HEAD", RepositoryOperationKind.Merge), ("REVERT_HEAD", RepositoryOperationKind.Revert), ("CHERRY_PICK_HEAD", RepositoryOperationKind.CherryPick) })
        {
            var location = System.IO.Path.Combine(gitDirectory, marker.Item1);
            if (File.Exists(location) || Directory.Exists(location)) { kind = marker.Item2; break; }
        }
        var conflicts = (await GetStatusAsync(path, ct)).Where(file => file.IsConflicted).Select(file => file.Path).ToArray();
        if (kind == RepositoryOperationKind.None && session is not null)
            kind = session.Kind == RepositoryOperationKind.StashApply ? RepositoryOperationKind.StashApply : RepositoryOperationKind.RestoreChanges;
        return new RepositoryOperation(kind, conflicts, session?.RecoveryReference);
    }
    private async Task EnsureIdleAsync(string path, CancellationToken ct)
    {
        var state = await GetOperationAsync(path, ct);
        if (state.IsActive || state.HasConflicts) throw new GitException("请先完成或中止当前操作，再执行其他修改仓库的操作。");
    }

    private static string ValidateFilePath(string repository, string relative)
    {
        var root = System.IO.Path.GetFullPath(repository).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var file = System.IO.Path.GetFullPath(relative, root);
        if (System.IO.Path.IsPathRooted(relative) || !file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || relative.Replace('\\', '/').Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new GitException("文件路径不在当前仓库工作区内。");
        return file;
    }
}
