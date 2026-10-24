namespace GitManager.Core;

public sealed partial class GitService
{
    public async Task<OperationPreview> PreviewSynchronizationAsync(string path, CancellationToken ct = default)
    {
        await EnsureIdleAsync(path, ct);
        var info = await GetRepositoryAsync(path, ct);
        if (info.IsDetached || info.Upstream is null) throw new GitException("请先切换到已有上游的本地分支；未发布的分支可以使用 Publish。");
        await FetchAsync(path, ct);
        info = await GetRepositoryAsync(path, ct);
        var remote = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.remote")).Output.Trim();
        var reference = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.merge")).Output.Trim();
        var remotes = (await Run(path, ct, "remote")).Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!remotes.Contains(remote) || !reference.StartsWith("refs/heads/", StringComparison.Ordinal)) throw new GitException("当前分支的上游没有配置为远程分支。");
        var preview = await PreviewHistoryOperationAsync(path, info.Upstream!, "同步 " + info.Branch + " 与 " + info.Upstream, ct);
        return preview with { Remote = remote, RemoteReference = reference };
    }

    public async Task SynchronizeAsync(string path, OperationPreview preview, SynchronizationMode mode, CancellationToken ct = default)
    {
        await VerifyPreviewAsync(path, preview, ct);
        var info = await GetRepositoryAsync(path, ct);
        var remote = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.remote")).Output.Trim();
        var reference = (await Run(path, ct, "config", "--get", $"branch.{info.Branch}.merge")).Output.Trim();
        if (remote != preview.Remote || reference != preview.RemoteReference || info.Upstream is null
            || await ResolveCommitAsync(path, info.Upstream, ct) != preview.Target) throw new GitException("上游配置或远程跟踪版本已变化，请重新预览同步。");
        if (mode == SynchronizationMode.Remote)
        {
            await EnsureNoIgnoredObstructionsAsync(path, preview.Target, ct);
            await CreateRecoveryAsync(path, "采用远程版本前", true, ct);
            await Run(path, ct, "reset", "--hard", preview.Target);
        }
        else if (mode == SynchronizationMode.Local)
        {
            await CreateRecoveryAsync(path, "远程覆盖前的版本", false, ct, preview.Target);
            await Run(path, ct, "push", $"--force-with-lease={reference}:{preview.Target}", "--", remote, $"{preview.Head}:{reference}");
        }
        else if (mode == SynchronizationMode.Merge) await MergeAsync(path, preview, ct);
        else if (mode == SynchronizationMode.Rebase) await RebaseAsync(path, preview, ct);
        else
        {
            var localAhead = (await Run(path, ct, "rev-list", "--count", preview.Target + ".." + preview.Head)).Output.Trim() != "0";
            var remoteAhead = (await Run(path, ct, "rev-list", "--count", preview.Head + ".." + preview.Target)).Output.Trim() != "0";
            if (localAhead && remoteAhead) throw new GitException("两边都有独有提交，请选择合并、变基或明确采用其中一端的版本。");
            if (remoteAhead)
                await StartSequencedOperationAsync(path, preview, RepositoryOperationKind.Merge, ["merge", "--ff-only", preview.Target], ct);
            else if (localAhead) await PushAsync(path, ct);
        }
    }
}
