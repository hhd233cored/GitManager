using GitManager.Core;

internal static class RepositoryOperationsChecks
{
    private static readonly GitService Git = new();
    private static readonly GitRunner Runner = new();
    private static async Task<string> Repository(string root, string name)
    {
        var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        await Runner.RunAsync(path, ["init", "-b", "main"]);
        await Identity(path);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(path, "b.txt"), "original b\n");
        await Commit(path, "root");
        return path;
    }
    private static async Task Identity(string path)
    {
        foreach (var pair in new[] { ("user.name", "Operations Tests"), ("user.email", "ops@example.test"),
            ("commit.gpgSign", "false"), ("core.autocrlf", "false"), ("core.hooksPath", ".git/no-hooks") })
            await Runner.RunAsync(path, ["config", pair.Item1, pair.Item2]);
    }
    private static async Task Commit(string path, string message) { await Git.StageAsync(path); await Git.CommitAsync(path, message, ""); }
    private static Task Write(string path, string file, string text) => File.WriteAllTextAsync(Path.Combine(path, file), text);
    private static Task<string> Head(string path) => Git.ResolveCommitAsync(path, "HEAD");
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (GitException) { return; }
        throw new Exception("The operation should have been rejected.");
    }
    public static async Task StashAsync(string root)
    {
        var path = await Repository(root, "stash-selection");
        var head = await Head(path);
        await Write(path, "a.txt", "index a\n");
        await Git.StageAsync(path, new ChangedFile("a.txt", null, ' ', 'M'));
        await Write(path, "a.txt", "index a\nworking a\n");
        await Write(path, "b.txt", "excluded b\n");
        await Write(path, "new[1] 文件.txt", "new file\n");
        var id = await Git.CreateStashAsync(path, "选择保存", true, ["a.txt", "new[1] 文件.txt"]);
        Assert((await Git.GetStatusAsync(path)).Single().Path == "b.txt", "Selected stash touched excluded changes.");
        Assert((await Git.GetStashFilesAsync(path, id)).Select(file => file.Path).ToHashSet().SetEquals(["a.txt", "new[1] 文件.txt"]), "Stash files missed selected/new paths.");
        var newFile = (await Git.GetStashFilesAsync(path, id)).Single(file => file.Path.StartsWith("new", StringComparison.Ordinal));
        Assert((await Git.GetStashDiffAsync(path, id, newFile)).Contains("+new file", StringComparison.Ordinal), "Untracked stash Diff is missing.");
        await Git.CreateStashAsync(path, "保存剩余", true);
        await Git.ApplyStashAsync(path, id, true);
        var status = await Git.GetStatusAsync(path);
        Assert(status.Single(file => file.Path == "a.txt").StageState is null, "Applying stash lost mixed index/working state.");
        Assert(File.Exists(Path.Combine(path, "new[1] 文件.txt")), "Applying stash lost the new file.");
        Assert((await Git.GetStashesAsync(path)).All(entry => entry.Id != id), "Pop removed the wrong stash after indices shifted.");
        Assert((await Git.GetStashesAsync(path)).Count == 1 && await Head(path) == head, "Stash changed commit history.");
        Assert((await Git.GetHistoryPageAsync(path, true, 100)).Commits.Count == 1, "Stash/recovery objects polluted ordinary History.");
    }
    public static async Task ResetAndRecoveryAsync(string root)
    {
        var path = await Repository(root, "reset-recovery");
        var first = await Head(path);
        await Write(path, "a.txt", "committed a\n"); await Commit(path, "second");
        var second = await Head(path);
        await Write(path, "a.txt", "staged a\n"); await Git.StageAsync(path, new ChangedFile("a.txt", null, ' ', 'M'));
        await Write(path, "a.txt", "staged a\nworking a\n"); await Write(path, "new.txt", "unsaved new\n");
        var cached = (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output;
        var plan = await Git.PreviewHistoryOperationAsync(path, first, "reset test");
        await Git.ResetAsync(path, plan, ResetMode.Hard);
        Assert(await Head(path) == first && (await Git.GetStatusAsync(path)).Count == 0, "Hard reset did not adopt target state.");
        var point = (await Git.GetRecoveryPointsAsync(path)).First();
        Assert(point.Head == second && point.StashId is not null, "Reset backup missed history or dirty files.");
        await Git.DropStashAsync(path, point.StashId!);
        await new GitService().RestoreRecoveryAsync(path, point);
        Assert(await Head(path) == second && await File.ReadAllTextAsync(Path.Combine(path, "a.txt")) == "staged a\nworking a\n", "Recovery did not restore original content.");
        Assert(File.Exists(Path.Combine(path, "new.txt")) && cached == (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output, "Recovery lost new files or index contents.");
        plan = await Git.PreviewHistoryOperationAsync(path, first, "soft test");
        var indexBefore = (await Runner.RunAsync(path, ["write-tree"])).Output;
        await Git.ResetAsync(path, plan, ResetMode.Soft);
        Assert(await Head(path) == first && indexBefore == (await Runner.RunAsync(path, ["write-tree"])).Output, "Soft reset changed index contents.");
        await Git.RestoreRecoveryAsync(path, (await Git.GetRecoveryPointsAsync(path)).First());
        Assert(await Head(path) == second && cached == (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output, "Soft reset backup did not retain the original dirty state.");
        plan = await Git.PreviewHistoryOperationAsync(path, first, "mixed test");
        await Git.ResetAsync(path, plan, ResetMode.Mixed);
        Assert((await Runner.RunAsync(path, ["diff", "--cached"])).Output.Length == 0 && (await File.ReadAllTextAsync(Path.Combine(path, "a.txt"))).Contains("working a", StringComparison.Ordinal), "Mixed reset discarded working changes or kept staging.");
        await Reject(() => Git.ResetAsync(path, plan, ResetMode.Hard));
    }
    private static async Task<(string Path, string Feature)> ConflictRepository(string root, string name)
    {
        var path = await Repository(root, name); var first = await Head(path);
        await Write(path, "a.txt", "main change\n"); await Commit(path, "main change");
        await Runner.RunAsync(path, ["switch", "-c", "feature", first]);
        await Write(path, "a.txt", "feature change\n"); await Commit(path, "feature change");
        return (path, await Head(path));
    }
    public static async Task RebaseAsync(string root)
    {
        var (path, feature) = await ConflictRepository(root, "rebase-conflict");
        await Write(path, "b.txt", "dirty staged b\n"); await Git.StageAsync(path, new ChangedFile("b.txt", null, ' ', 'M'));
        var cached = (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output;
        var plan = await Git.PreviewHistoryOperationAsync(path, "main", "rebase preview");
        await Reject(() => Git.RebaseAsync(path, plan));
        var operation = await new GitService().GetOperationAsync(path);
        Assert(operation.Kind == RepositoryOperationKind.Rebase && operation.Conflicts.SequenceEqual(["a.txt"]), "Rebase conflict/restart state was not detected.");
        await Reject(() => Git.SwitchAsync(path, "main"));
        await Git.AbortOperationAsync(path);
        Assert(await Head(path) == feature && cached == (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output, "Aborting rebase lost original branch/index state.");
        plan = await Git.PreviewHistoryOperationAsync(path, "main", "rebase again");
        await Reject(() => Git.RebaseAsync(path, plan));
        var file = await Git.GetConflictAsync(path, "a.txt");
        Assert(file.Current == "main change\n" && file.Incoming == "feature change\n", "Rebase conflict sides are incorrect.");
        await Reject(() => Git.ResolveConflictAsync(path, "a.txt", ConflictResolution.Edited, file.Result));
        await Git.ResolveConflictAsync(path, "a.txt", ConflictResolution.Edited, "merged change\n");
        await new GitService().ContinueOperationAsync(path);
        Assert(!(await Git.GetOperationAsync(path)).IsActive && await Head(path) != feature, "Rebase did not complete with new commit IDs.");
        Assert(cached == (await Runner.RunAsync(path, ["diff", "--cached", "--binary"])).Output, "Rebase completion did not restore original staged changes.");
        Assert((await Git.GetRepositoryAsync(path)).Branch == "feature", "Rebase changed the wrong branch.");
    }
    public static async Task MergeAndRevertAsync(string root)
    {
        var (path, feature) = await ConflictRepository(root, "merge-conflict");
        var plan = await Git.PreviewHistoryOperationAsync(path, "main", "merge preview");
        await Reject(() => Git.MergeAsync(path, plan));
        await Git.ResolveConflictAsync(path, "a.txt", ConflictResolution.Incoming);
        await Git.ContinueOperationAsync(path);
        var merge = await Git.GetHistoryByIdAsync(path, "HEAD");
        Assert(merge.Parents.Count == 2 && await File.ReadAllTextAsync(Path.Combine(path, "a.txt")) == "main change\n", "Merge conflict was not resolved to incoming side.");
        plan = await Git.PreviewHistoryOperationAsync(path, merge.Id, "revert merge");
        await Reject(() => Git.RevertAsync(path, plan));
        await Git.RevertAsync(path, plan, 1);
        Assert(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")) == "feature change\n", "Revert merge did not use the selected mainline.");
        var reverted = await Git.GetHistoryByIdAsync(path, "HEAD");
        Assert(reverted.Parents.Single() == merge.Id && reverted.Id != feature, "Revert rewrote history instead of adding a commit.");
    }
    public static async Task StashConflictAsync(string root)
    {
        var path = await Repository(root, "stash-conflict");
        await Write(path, "a.txt", "stashed version\n"); await Write(path, "u.txt", "stashed new file\n");
        var id = await Git.CreateStashAsync(path, "conflicting stash");
        await Write(path, "a.txt", "new base\n"); await Commit(path, "new base");
        var head = await Head(path);
        await Reject(() => Git.ApplyStashAsync(path, id, true, false));
        Assert((await Git.GetOperationAsync(path)).Kind == RepositoryOperationKind.StashApply, "Stash apply conflict state is missing.");
        await Write(path, "keep.txt", "created after apply\n");
        await Git.AbortOperationAsync(path);
        Assert(await Head(path) == head && !File.Exists(Path.Combine(path, "u.txt")) && File.Exists(Path.Combine(path, "keep.txt")), "Aborting stash apply removed unrelated files or kept introduced files.");
        await Git.CreateStashAsync(path, "save keep file");
        await Reject(() => Git.ApplyStashAsync(path, id, true, false));
        await Git.ResolveConflictAsync(path, "a.txt", ConflictResolution.Incoming);
        await Git.ContinueOperationAsync(path);
        Assert((await Git.GetStashesAsync(path)).All(entry => entry.Id != id) && !(await Git.GetOperationAsync(path)).IsActive, "Finishing stash pop did not drop the correct entry.");
    }
    public static async Task SynchronizationAsync(string root)
    {
        var seed = await Repository(root, "sync-seed");
        var remote = Path.Combine(root, "sync-remote-" + Guid.NewGuid().ToString("N") + ".git");
        await Runner.RunAsync(root, ["clone", "--bare", "--", seed, remote]);
        var local = remote + "-local"; var peer = remote + "-peer";
        await Git.CloneAsync(remote, local); await Git.CloneAsync(remote, peer); await Identity(local); await Identity(peer);
        await Write(local, "local.txt", "local\n"); await Commit(local, "local only");
        var localHead = await Head(local);
        await Write(peer, "peer.txt", "peer\n"); await Commit(peer, "peer only"); await Git.PushAsync(peer);
        var remoteHead = await Head(peer);
        await Write(local, "b.txt", "uncommitted\n");
        var preview = await Git.PreviewSynchronizationAsync(local);
        await Reject(() => Git.SynchronizeAsync(local, preview, SynchronizationMode.Normal));
        await Git.SynchronizeAsync(local, preview, SynchronizationMode.Remote);
        Assert(await Head(local) == remoteHead && (await Git.GetStatusAsync(local)).Count == 0, "Adopting remote did not align local files/history.");
        var backup = (await Git.GetRecoveryPointsAsync(local)).First(point => point.Reason == "采用远程版本前");
        await Git.RestoreRecoveryAsync(local, backup);
        Assert(await Head(local) == localHead && await File.ReadAllTextAsync(Path.Combine(local, "b.txt")) == "uncommitted\n", "Remote adoption backup did not restore local changes.");
        preview = await Git.PreviewSynchronizationAsync(local);
        await Write(peer, "later.txt", "advanced after preview\n"); await Commit(peer, "later remote"); await Git.PushAsync(peer);
        var later = await Head(peer);
        await Reject(() => Git.SynchronizeAsync(local, preview, SynchronizationMode.Local));
        Assert((await Runner.RunAsync(remote, ["rev-parse", "refs/heads/main"])).Output.Trim() == later, "Lease failure overwrote newer remote commits.");
        preview = await Git.PreviewSynchronizationAsync(local);
        await Git.SynchronizeAsync(local, preview, SynchronizationMode.Local);
        Assert((await Runner.RunAsync(remote, ["rev-parse", "refs/heads/main"])).Output.Trim() == localHead, "Adopting local did not push the selected committed HEAD.");
        Assert((await Git.GetRecoveryPointsAsync(local)).Any(point => point.Head == later && point.Reason == "远程覆盖前的版本"), "Remote overwrite did not retain a remote recovery snapshot.");
        Assert(await File.ReadAllTextAsync(Path.Combine(local, "b.txt")) == "uncommitted\n", "Adopting local changed uncommitted files.");
    }
}
