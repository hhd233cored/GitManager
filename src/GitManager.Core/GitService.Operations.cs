using System.Text;

namespace GitManager.Core;

public sealed partial class GitService
{
    private async Task EnsureCleanAsync(string path, CancellationToken ct)
    {
        if ((await GetStatusAsync(path, ct)).Count > 0) throw new GitException("请先提交或保存当前改动，再应用 Stash。");
    }
    private async Task VerifyPreviewAsync(string path, OperationPreview preview, CancellationToken ct)
    {
        await EnsureIdleAsync(path, ct);
        var repository = await GetRepositoryAsync(path, ct);
        if (!System.IO.Path.GetFullPath(path).Equals(System.IO.Path.GetFullPath(preview.RepositoryPath), StringComparison.OrdinalIgnoreCase)
            || repository.Branch != preview.Branch || await ResolveCommitAsync(path, "HEAD", ct) != preview.Head)
            throw new GitException("仓库或当前分支已发生变化，请重新预览操作。");
        if (await ResolveCommitAsync(path, preview.Target, ct) != preview.Target)
            throw new GitException("目标版本已发生变化，请重新预览操作。");
    }
    public async Task<OperationPreview> PreviewHistoryOperationAsync(string path, string target, string title, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        var info = await GetRepositoryAsync(path, ct);
        if (info.IsDetached) throw new GitException("请先切换到本地分支，再修改提交历史。");
        var head = await ResolveCommitAsync(path, "HEAD", ct);
        var resolved = await ResolveCommitAsync(path, target, ct);
        var local = await CommitSummariesAsync(path, resolved + ".." + head, ct);
        var incoming = await CommitSummariesAsync(path, head + ".." + resolved, ct);
        var stat = (await Run(path, ct, "diff", "--stat", head, resolved, "--")).Output;
        return new OperationPreview(path, info.Branch, head, resolved, title, stat, (await GetStatusAsync(path, ct)).Count, local, incoming);
    }
    private async Task<IReadOnlyList<string>> CommitSummariesAsync(string path, string range, CancellationToken ct) =>
        (await Run(path, ct, "log", "--no-show-signature", "--format=%h %s", "--max-count=100", range, "--")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    private async Task EnsureNoIgnoredObstructionsAsync(string path, string target, CancellationToken ct)
    {
        var tracked = (await Run(path, ct, "ls-files", "-z")).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var untracked = (await GetStatusAsync(path, ct)).Where(file => file.IsUntracked).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var incoming = (await Run(path, ct, "diff", "--name-only", "--diff-filter=ACMRT", "-z", "HEAD", target, "--")).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var relative in incoming)
        {
            var absolute = ValidateFilePath(path, relative);
            if (Directory.Exists(absolute)) throw new GitException($"目标文件 {relative} 在本地是目录，请先备份该目录。");
            for (var candidate = relative; candidate.Length > 0; candidate = System.IO.Path.GetDirectoryName(candidate)?.Replace('\\', '/') ?? "")
                if (File.Exists(ValidateFilePath(path, candidate)) && !tracked.Contains(candidate) && !untracked.Contains(candidate))
                    throw new GitException($"忽略文件 {candidate} 会被覆盖，请先手动备份或移走该文件。");
        }
    }

    public async Task ResetAsync(string path, OperationPreview preview, ResetMode mode, CancellationToken ct = default)
    {
        await VerifyPreviewAsync(path, preview, ct);
        if (mode == ResetMode.Hard) await EnsureNoIgnoredObstructionsAsync(path, preview.Target, ct);
        var backup = await CreateRecoveryAsync(path, $"重置到 {preview.Target[..8]}（{mode}）", true, ct);
        if (mode != ResetMode.Hard && backup.StashId is not null)
            await Run(path, ct, "stash", "apply", "--index", backup.StashId);
        await Run(path, ct, "reset", "--" + mode.ToString().ToLowerInvariant(), preview.Target);
    }

    public async Task RebaseAsync(string path, OperationPreview preview, CancellationToken ct = default) =>
        await StartSequencedOperationAsync(path, preview, RepositoryOperationKind.Rebase, ["rebase", preview.Target], ct);
    public async Task MergeAsync(string path, OperationPreview preview, CancellationToken ct = default) =>
        await StartSequencedOperationAsync(path, preview, RepositoryOperationKind.Merge, ["merge", "--no-edit", preview.Target], ct);
    public async Task RevertAsync(string path, OperationPreview preview, int mainline = 0, CancellationToken ct = default)
    {
        var commit = (await GetHistoryByIdAsync(path, preview.Target, ct));
        if (commit.Parents.Count > 1 && (mainline < 1 || mainline > commit.Parents.Count))
            throw new GitException("撤销合并提交时，请选择作为主线的父提交。");
        var args = new List<string> { "revert", "--no-edit" };
        if (commit.Parents.Count > 1) args.AddRange(["--mainline", mainline.ToString()]);
        args.Add(preview.Target);
        await StartSequencedOperationAsync(path, preview, RepositoryOperationKind.Revert, args, ct);
    }
    public async Task<CommitInfo> GetHistoryByIdAsync(string path, string id, CancellationToken ct = default) =>
        ParseHistory((await Run(path, ct, "log", "--no-walk", "--no-show-signature", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00%D%x00", await ResolveCommitAsync(path, id, ct))).Output).Single();

    private async Task StartSequencedOperationAsync(string path, OperationPreview preview, RepositoryOperationKind kind,
        IEnumerable<string> arguments, CancellationToken ct)
    {
        await VerifyPreviewAsync(path, preview, ct);
        if (kind is RepositoryOperationKind.Rebase or RepositoryOperationKind.Merge) await EnsureNoIgnoredObstructionsAsync(path, preview.Target, ct);
        var backup = await CreateRecoveryAsync(path, preview.Title, true, ct);
        var session = new OperationSession(kind, backup.Reference, backup.StashId);
        await WriteSessionAsync(path, session, ct);
        var result = await _runner.RunAsync(path, ["-c", "core.editor=true", .. arguments], ct, false);
        var state = await GetOperationAsync(path, ct);
        if (result.ExitCode != 0)
        {
            if (state.Kind is RepositoryOperationKind.RestoreChanges) await FinishSessionAsync(path, session, ct);
            throw new GitException(result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim(), result.ExitCode);
        }
        await FinishSessionAsync(path, session, ct);
    }

    private async Task FinishSessionAsync(string path, OperationSession session, CancellationToken ct)
    {
        // Retain the original session until stash application has persisted its own state.
        // A failed preflight or cancellation can then be resumed after reopening the app.
        if (session.StashId is not null) await ApplyStashInternalAsync(path, session.StashId, false, true, ct);
        else await ClearSessionAsync(path, ct);
    }
    public async Task ApplyStashAsync(string path, string id, bool drop, bool restoreIndex = true, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        await ApplyStashInternalAsync(path, id, drop, restoreIndex, ct);
    }
    private async Task ApplyStashInternalAsync(string path, string id, bool drop, bool restoreIndex, CancellationToken ct)
    {
        await EnsureCleanAsync(path, ct);
        id = await ResolveCommitAsync(path, id, ct);
        var backup = await CreateRecoveryAsync(path, "应用 Stash 前", false, ct);
        var third = await OptionalRevisionAsync(path, id + "^3", ct);
        var introduced = third is null ? [] : (await Run(path, ct, "ls-tree", "-r", "--name-only", "-z", third)).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var file in introduced)
        {
            if (File.Exists(ValidateFilePath(path, file)) || Directory.Exists(ValidateFilePath(path, file)))
                throw new GitException($"恢复的新增文件 {file} 已存在，请先保存或移走它。");
        }
        var session = new OperationSession(RepositoryOperationKind.StashApply, backup.Reference, null, id, drop, introduced);
        await WriteSessionAsync(path, session, ct);
        var args = new List<string> { "stash", "apply" };
        if (restoreIndex) args.Add("--index");
        args.Add(id);
        var result = await _runner.RunAsync(path, args, ct, false);
        if (result.ExitCode != 0)
        {
            if ((await GetStatusAsync(path, ct)).Any(file => file.IsConflicted))
                throw new GitException("Stash 恢复遇到冲突，请解决后点击“完成”，或中止恢复。Stash 已保留。");
            await WriteSessionAsync(path, session with { DropStash = false }, ct);
            throw new GitException(result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim(), result.ExitCode);
        }
        if (drop) await DropStashByIdAsync(path, id, ct);
        await ClearSessionAsync(path, ct);
    }

    public async Task ContinueOperationAsync(string path, CancellationToken ct = default)
    {
        var state = await GetOperationAsync(path, ct);
        if (state.HasConflicts) throw new GitException("请先解决并标记所有冲突文件。");
        var session = await ReadSessionAsync(path, ct);
        if (state.Kind == RepositoryOperationKind.StashApply)
        {
            if (session?.DropStash == true && session.AppliedStash is not null) await DropStashByIdAsync(path, session.AppliedStash, ct);
            await ClearSessionAsync(path, ct);
            return;
        }
        if (state.Kind == RepositoryOperationKind.RestoreChanges && session is not null) { await FinishSessionAsync(path, session, ct); return; }
        var command = state.Kind switch { RepositoryOperationKind.Rebase => "rebase", RepositoryOperationKind.Merge => "merge",
            RepositoryOperationKind.Revert => "revert", RepositoryOperationKind.CherryPick => "cherry-pick", _ => throw new GitException("没有进行中的操作。") };
        await Run(path, ct, "-c", "core.editor=true", command, "--continue");
        if (session is not null && (await GetOperationAsync(path, ct)).Kind == RepositoryOperationKind.RestoreChanges)
            await FinishSessionAsync(path, session, ct);
    }
    public async Task AbortOperationAsync(string path, CancellationToken ct = default)
    {
        var state = await GetOperationAsync(path, ct);
        var session = await ReadSessionAsync(path, ct);
        if (state.Kind == RepositoryOperationKind.RestoreChanges) { await ClearSessionAsync(path, ct); return; }
        if (state.Kind == RepositoryOperationKind.StashApply && session is not null)
        {
            var backup = (await GetRecoveryPointsAsync(path, ct)).Single(point => point.Reference == session.RecoveryReference);
            await ValidateRecoveryAsync(path, backup, ct);
            await Run(path, ct, "reset", "--hard", backup.Head);
            foreach (var file in session.IntroducedFiles ?? [])
            {
                ValidateFilePath(path, file);
                await Run(path, ct, "clean", "-f", "-x", "--", file);
            }
            await ClearSessionAsync(path, ct);
            return;
        }
        var command = state.Kind switch { RepositoryOperationKind.Rebase => "rebase", RepositoryOperationKind.Merge => "merge",
            RepositoryOperationKind.Revert => "revert", RepositoryOperationKind.CherryPick => "cherry-pick", _ => throw new GitException("没有进行中的操作。") };
        await Run(path, ct, command, "--abort");
        if (session is not null) await FinishSessionAsync(path, session, ct);
    }

    public async Task RestoreRecoveryAsync(string path, RecoveryPoint point, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        var stored = (await GetRecoveryPointsAsync(path, ct)).SingleOrDefault(entry => entry.Reference == point.Reference)
            ?? throw new GitException("该恢复点已不存在。");
        await ValidateRecoveryAsync(path, stored, ct);
        var info = await GetRepositoryAsync(path, ct);
        if (info.Branch != stored.Branch) throw new GitException($"请先切换到 {stored.Branch} 分支，再恢复此备份。");
        await EnsureNoIgnoredObstructionsAsync(path, stored.Head, ct);
        await CreateRecoveryAsync(path, "恢复备份前", true, ct);
        await Run(path, ct, "reset", "--hard", stored.Head);
        if (stored.StashId is not null) await ApplyStashInternalAsync(path, stored.StashId, false, true, ct);
    }

    public async Task<ConflictContent> GetConflictAsync(string path, string relative, CancellationToken ct = default)
    {
        var absolute = ValidateFilePath(path, relative);
        if (Directory.Exists(absolute)) throw new GitException("子模块或目录冲突请先通过外部 Git 工具处理。");
        if (!(await GetStatusAsync(path, ct)).Any(file => file.Path == relative && file.IsConflicted)) throw new GitException("该文件已不处于冲突状态。");
        async Task<string?> ReadStage(int stage)
        {
            var result = await _runner.RunAsync(path, ["show", $":{stage}:{relative}"], ct, false);
            return result.ExitCode == 0 ? result.Output : null;
        }
        var current = await ReadStage(2);
        var incoming = await ReadStage(3);
        var result = File.Exists(ValidateFilePath(path, relative)) ? await File.ReadAllTextAsync(ValidateFilePath(path, relative), ct) : "";
        var binary = new[] { current, incoming, result }.Any(text => text?.Contains('\0') == true || text?.Contains('\ufffd') == true);
        return new ConflictContent(relative, current, incoming, result, binary);
    }
    public async Task ResolveConflictAsync(string path, string relative, ConflictResolution resolution, string? edited = null, CancellationToken ct = default)
    {
        var content = await GetConflictAsync(path, relative, ct);
        if (resolution == ConflictResolution.Edited)
        {
            if (content.IsBinary) throw new GitException("二进制或非 UTF-8 文件请采用当前版本或传入版本。");
            if (edited is null || edited.Split('\n').Any(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                || line.StartsWith("=======", StringComparison.Ordinal) || line.StartsWith(">>>>>>>", StringComparison.Ordinal)))
                throw new GitException("文件仍包含冲突标记，请处理后再保存。");
            var absolute = ValidateFilePath(path, relative);
            var previous = File.Exists(absolute) ? await File.ReadAllBytesAsync(absolute, ct) : [];
            var withBom = previous.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
            var newline = content.Result.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = edited.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (newline == "\r\n") normalized = normalized.Replace("\n", newline, StringComparison.Ordinal);
            await File.WriteAllTextAsync(absolute, normalized, new UTF8Encoding(withBom), ct);
            await Run(path, ct, "add", "--", relative);
        }
        else if ((resolution == ConflictResolution.Current ? content.Current : content.Incoming) is null)
            await Run(path, ct, "rm", "-f", "--ignore-unmatch", "--", relative);
        else
        {
            await Run(path, ct, "checkout", resolution == ConflictResolution.Current ? "--ours" : "--theirs", "--", relative);
            await Run(path, ct, "add", "--", relative);
        }
    }
}
