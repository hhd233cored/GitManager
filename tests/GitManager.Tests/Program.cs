using System.Diagnostics;
using System.Text;
using GitManager.Core;

var testRoot = Path.Combine(Path.GetTempPath(), "GitManager.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var fixtureArgument = Array.IndexOf(args, "--fixture");
var fixture = fixtureArgument >= 0 && fixtureArgument + 1 < args.Length
    ? Path.GetFullPath(args[fixtureArgument + 1]) : Path.Combine(testRoot, "示例仓库 with spaces");
await SampleRepository.CreateAsync(fixture);
var git = new GitService();
var runner = new GitRunner();
var failures = new List<string>();
var passed = 0;

await Check("NUL-delimited status handles renames, Unicode, whitespace and literal names", () =>
{
    var parsed = GitService.ParseStatus("R  new 文件.txt\0old name.txt\0 M leading and trailing .txt\0?? line\nname.txt\0");
    Equal(3, parsed.Count);
    Equal("old name.txt", parsed[0].OldPath);
    Equal("new 文件.txt", parsed[0].Path);
    Equal("line\nname.txt", parsed[2].Path);
    Assert(parsed[0].HasStagedChanges && parsed[1].HasUnstagedChanges && parsed[2].IsUntracked);
    return Task.CompletedTask;
});

await Check("Diff parser preserves hunk line numbers, replacement alignment and marker-like text", () =>
{
    var document = DiffParser.Parse("diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -5,3 +5,4 @@\n same\n-old\n+new\n+++literal\n last\n\\ No newline at end of file\n");
    Equal(2, document.Additions);
    Equal(1, document.Deletions);
    Equal(5, document.Lines[1].OldNumber);
    Equal(6, document.Lines[2].OldNumber);
    Equal(7, document.Lines[4].NewNumber);
    Equal("++literal", document.Lines[4].Text);
    Assert(document.Pairs.Any(pair => pair.Left?.Text == "old" && pair.Right?.Text == "new"));
    Equal(0, DiffParser.Parse("").Lines.Count);
    return Task.CompletedTask;
});

await Check("Real history, merge/root diffs, branch scope, metadata and graph connectivity", async () =>
{
    var info = await git.GetRepositoryAsync(fixture);
    Assert(info.HasHead);
    Equal("main", info.Branch);
    var all = await git.GetHistoryAsync(fixture, true, 500);
    var current = await git.GetHistoryAsync(fixture, false, 500);
    Assert(all.Count >= 12 && all.Count > current.Count);
    Assert(all.Any(commit => commit.Body.Contains("元数据", StringComparison.Ordinal)));
    var merge = all.First(commit => commit.Parents.Count == 2);
    var mergeFiles = await git.GetCommitFilesAsync(fixture, merge);
    Assert(mergeFiles.Count > 0);
    Assert((await git.GetDiffAsync(fixture, mergeFiles[0], merge, false)).Length > 0);
    var root = all.Single(commit => commit.Parents.Count == 0);
    var rootFiles = await git.GetCommitFilesAsync(fixture, root);
    Assert(rootFiles.Count >= 3);
    var rootDiff = DiffParser.Parse(await git.GetDiffAsync(fixture, rootFiles[0], root, false));
    Assert(rootDiff.Additions > 0 && rootDiff.Deletions == 0);
    Assert((await git.GetBranchesAsync(fixture)).Any(branch => branch.Name == "feature/search"));
    VerifyGraph(all);
    VerifyGraph(current);
    // A page ending before a parent preserves the exact geometry of existing rows.
    var page = GraphLayout.Build(all.Take(4).ToArray());
    var whole = GraphLayout.Build(all);
    for (var i = 0; i < page.Rows.Count; i++)
    {
        Equal(whole.Rows[i].NodeLane, page.Rows[i].NodeLane);
        Assert(whole.Rows[i].Edges.SequenceEqual(page.Rows[i].Edges));
    }
});

await Check("Octopus merges, shared parents and disconnected roots remain connected", () =>
{
    CommitInfo C(string id, params string[] parents) => new(id, parents, "Tester", "t@example.test", DateTimeOffset.Now, id, "", "");
    VerifyGraph([C("merge", "main", "side-a", "side-b"), C("main", "base"), C("side-a", "base"), C("side-b", "base"), C("base"), C("unrelated")]);
    return Task.CompletedTask;
});

await Check("Stage/unstage/commit uses literal pathspecs and preserves mixed working changes", async () =>
{
    var path = await CloneForTest("working state");
    await File.WriteAllTextAsync(Path.Combine(path, "globs[1].txt"), "first\n");
    await File.WriteAllTextAsync(Path.Combine(path, "globs1.txt"), "must stay untracked\n");
    var file = (await git.GetStatusAsync(path)).Single(file => file.Path == "globs[1].txt");
    var untrackedDiff = await git.GetDiffAsync(path, file, null, false);
    Assert(DiffParser.Parse(untrackedDiff).Additions == 1);
    await git.StageAsync(path, file);
    var status = await git.GetStatusAsync(path);
    Assert(status.Single(file => file.Path == "globs[1].txt").HasStagedChanges);
    Assert(status.Single(file => file.Path == "globs1.txt").IsUntracked);
    await git.CommitAsync(path, "test: literal filename", "Commit body\nSecond line");
    await File.WriteAllTextAsync(Path.Combine(path, "globs[1].txt"), "staged edit\n");
    file = (await git.GetStatusAsync(path)).Single(file => file.Path == "globs[1].txt");
    await git.StageAsync(path, file);
    await File.WriteAllTextAsync(Path.Combine(path, "globs[1].txt"), "unstaged edit\n");
    file = (await git.GetStatusAsync(path)).Single(file => file.Path == "globs[1].txt");
    Assert(file.StageState is null);
    Assert((await git.GetDiffAsync(path, file, null, true)).Contains("+staged edit", StringComparison.Ordinal));
    Assert((await git.GetDiffAsync(path, file, null, false)).Contains("+unstaged edit", StringComparison.Ordinal));
    await git.UnstageAsync(path, file);
    Equal("unstaged edit\n", await File.ReadAllTextAsync(Path.Combine(path, file.Path)));
    Assert(!(await git.GetStatusAsync(path)).Single(candidate => candidate.Path == file.Path).HasStagedChanges);
});

await Check("Unborn repository and unstage retain files even after post-stage edits", async () =>
{
    var path = Path.Combine(testRoot, "unborn");
    Directory.CreateDirectory(path);
    await runner.RunAsync(path, ["init", "-b", "main"]);
    Assert(!(await git.GetRepositoryAsync(path)).HasHead);
    Equal(0, (await git.GetHistoryAsync(path, true, 20)).Count);
    await File.WriteAllTextAsync(Path.Combine(path, "初始 文件.txt"), "one\n");
    await git.StageAsync(path);
    await File.WriteAllTextAsync(Path.Combine(path, "初始 文件.txt"), "two\n");
    await git.UnstageAsync(path);
    Equal("two\n", await File.ReadAllTextAsync(Path.Combine(path, "初始 文件.txt")));
    Assert((await git.GetStatusAsync(path)).Single().IsUntracked);
});

await Check("Rename status, committed rename and detached HEAD work with spaces and Unicode", async () =>
{
    var path = await CloneForTest("rename");
    await runner.RunAsync(path, ["mv", "README.md", "使用说明 renamed.md"]);
    var file = (await git.GetStatusAsync(path)).Single();
    Equal("README.md", file.OldPath);
    await git.CommitAsync(path, "test: rename", "");
    var commit = (await git.GetHistoryAsync(path, false, 1)).Single();
    var changes = await git.GetCommitFilesAsync(path, commit);
    Equal("README.md", changes.Single().OldPath);
    Assert((await git.GetDiffAsync(path, changes[0], commit, false)).Contains("rename to 使用说明 renamed.md", StringComparison.Ordinal));
    await runner.RunAsync(path, ["switch", "--detach", commit.Id]);
    Assert((await git.GetRepositoryAsync(path)).Branch.StartsWith("分离的 HEAD", StringComparison.Ordinal));
    Assert((await git.GetHistoryAsync(path, false, 10)).Count > 0);
});

await Check("Local remotes: clone, publish branch, fetch, track, push and fast-forward-only pull", async () =>
{
    var remote = Path.Combine(testRoot, "remote.git");
    await runner.RunAsync(testRoot, ["clone", "--bare", "--", fixture, remote]);
    var local = Path.Combine(testRoot, "remote client");
    await git.CloneAsync(remote, local);
    await ConfigureIdentity(local);
    Equal("origin/main", (await git.GetRepositoryAsync(local)).Upstream);
    await git.CreateBranchAsync(local, "feature/publish");
    await File.WriteAllTextAsync(Path.Combine(local, "publish.txt"), "publish\n");
    await git.StageAsync(local);
    await git.CommitAsync(local, "test: publish", "");
    await git.PushAsync(local);
    Equal("origin/feature/publish", (await git.GetRepositoryAsync(local)).Upstream);
    var peer = Path.Combine(testRoot, "peer");
    await git.CloneAsync(remote, peer);
    await ConfigureIdentity(peer);
    await git.CreateBranchAsync(peer, "feature/remote-only");
    await File.WriteAllTextAsync(Path.Combine(peer, "remote-only.txt"), "initial\n");
    await git.StageAsync(peer);
    await git.CommitAsync(peer, "test: remote branch", "");
    await git.PushAsync(peer);
    await git.FetchAsync(local);
    await git.SwitchAsync(local, "origin/feature/remote-only", trackRemote: true);
    Equal("feature/remote-only", (await git.GetRepositoryAsync(local)).Branch);
    await File.WriteAllTextAsync(Path.Combine(peer, "peer.txt"), "advance remote\n");
    await git.StageAsync(peer);
    await git.CommitAsync(peer, "test: advance", "");
    await git.PushAsync(peer);
    await git.PullAsync(local);
    Assert(File.Exists(Path.Combine(local, "peer.txt")));
    await File.WriteAllTextAsync(Path.Combine(local, "local.txt"), "local divergent\n");
    await git.StageAsync(local);
    await git.CommitAsync(local, "test: local", "");
    await File.WriteAllTextAsync(Path.Combine(peer, "peer-2.txt"), "remote divergent\n");
    await git.StageAsync(peer);
    await git.CommitAsync(peer, "test: remote", "");
    await git.PushAsync(peer);
    var headBefore = (await git.GetHistoryAsync(local, false, 1)).Single().Id;
    await Throws<GitException>(() => git.PullAsync(local));
    Equal(headBefore, (await git.GetHistoryAsync(local, false, 1)).Single().Id);
});

await Check("Invalid repositories, invalid branches and cancellation are reported", async () =>
{
    var outside = Path.Combine(testRoot, "not a repository");
    Directory.CreateDirectory(outside);
    await Throws<GitException>(() => git.GetRepositoryAsync(outside));
    await Throws<GitException>(() => git.CreateBranchAsync(fixture, "invalid name"));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => runner.RunAsync(fixture, ["status"], cancellation.Token));
});

Console.WriteLine($"\n{passed} passed, {failures.Count} failed.");
Console.WriteLine($"FIXTURE_PATH={fixture}");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add($"FAIL {name}\n{ex}"); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
async Task<string> CloneForTest(string name)
{
    var path = Path.Combine(testRoot, name);
    await git.CloneAsync(fixture, path);
    await ConfigureIdentity(path);
    return path;
}
async Task ConfigureIdentity(string path)
{
    await runner.RunAsync(path, ["config", "user.name", "GitManager Tests"]);
    await runner.RunAsync(path, ["config", "user.email", "tests@example.test"]);
    await runner.RunAsync(path, ["config", "commit.gpgSign", "false"]);
    await runner.RunAsync(path, ["config", "core.hooksPath", ".git/no-hooks"]);
}
static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static void VerifyGraph(IReadOnlyList<CommitInfo> commits)
{
    var graph = GraphLayout.Build(commits);
    Equal(commits.Count, graph.Rows.Count);
    for (var i = 0; i < commits.Count; i++)
    {
        var row = graph.Rows[i];
        Assert(row.NodeLane >= 0 && row.NodeLane < graph.LaneCount);
        Equal(commits[i].Parents.Count, row.Edges.Count(edge => edge.FromY == .5));
        if (i + 1 < commits.Count)
        {
            var bottom = row.Edges.Where(edge => edge.ToY == 1).Select(edge => (edge.ToLane, edge.Color)).ToHashSet();
            var nextTop = graph.Rows[i + 1].Edges.Where(edge => edge.FromY == 0).Select(edge => (edge.FromLane, edge.Color)).ToHashSet();
            Assert(bottom.SetEquals(nextTop), $"Discontinuous graph boundary at {i}");
        }
        var outgoing = row.Edges.Where(edge => edge.FromY == .5).ToArray();
        for (var p = 0; p < commits[i].Parents.Count; p++)
        {
            var target = commits.Select((commit, index) => (commit, index)).FirstOrDefault(pair => pair.commit.Id == commits[i].Parents[p]);
            if (target.commit is null) continue;
            Assert(target.index > i, "History must be topologically ordered");
            var lane = outgoing[p].ToLane;
            for (var j = i + 1; j < target.index; j++)
                lane = graph.Rows[j].Edges.Single(edge => edge.FromY == 0 && edge.FromLane == lane && edge.ToY == 1).ToLane;
            Equal(lane, graph.Rows[target.index].NodeLane);
        }
    }
}

internal static class SampleRepository
{
    private static int _clock;
    public static async Task CreateAsync(string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git"))) return;
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidOperationException("Fixture destination must be empty.");
        Directory.CreateDirectory(path);
        await Git(path, "init", "-b", "main");
        await Git(path, "config", "user.name", "Lin Chen");
        await Git(path, "config", "user.email", "lin@example.test");
        await Git(path, "config", "commit.gpgSign", "false");
        await Git(path, "config", "core.autocrlf", "false");
        await Git(path, "config", "core.hooksPath", ".git/no-hooks");
        Write(path, "README.md", "# GitManager\n\n一个清晰、轻巧的本地 Git 工作空间。\n\n这是自动生成的界面验证仓库。\n");
        Write(path, "src/Views/HistoryView.xaml", HistoryView(false));
        Write(path, "src/Services/GitService.cs", "namespace GitManager.Services;\n\npublic sealed class GitService\n{\n    public string CurrentBranch { get; set; } = \"main\";\n}\n");
        await Commit(path, "chore: 初始化项目与基础目录");
        Write(path, "src/Services/RepositoryStore.cs", "public sealed record Repository(string Name, string Path);\n");
        await Commit(path, "feat: 添加仓库与分支切换");
        await Git(path, "switch", "-c", "feature/commit-graph");
        Write(path, "src/Graph/GraphLayout.cs", "namespace GitManager.Graph;\n\npublic sealed class GraphLayout\n{\n    public double LaneSpacing => 16;\n    public double RowHeight => 48;\n}\n");
        await Commit(path, "feat: 实现提交图轨道分配");
        Write(path, "src/Graph/GraphGeometry.cs", "namespace GitManager.Graph;\n\npublic sealed record GraphNode(int Lane, string Color);\npublic sealed record GraphEdge(int From, int To);\n");
        await Commit(path, "feat: 绘制平滑分叉与合并曲线");
        await Git(path, "switch", "main");
        Write(path, "src/Services/GitService.cs", "namespace GitManager.Services;\n\npublic sealed class GitService\n{\n    public string CurrentBranch { get; set; } = \"main\";\n    public bool HasRemote { get; set; }\n\n    public async Task FetchAsync()\n    {\n        await RunAsync(\"fetch\", \"--all\");\n    }\n}\n");
        await Commit(path, "feat: 接入远程仓库与 Fetch 操作");
        await Git(path, "merge", "--no-ff", "feature/commit-graph", "-m", "Merge branch 'feature/commit-graph'");
        await Git(path, "switch", "-c", "feature/diff-view");
        Write(path, "src/Diff/DiffRenderer.cs", "namespace GitManager.Diff;\n\npublic sealed class DiffRenderer\n{\n    public bool IsSplit { get; set; } = true;\n    public string AddedColor => \"#E6FFEC\";\n    public string RemovedColor => \"#FFEBE9\";\n}\n");
        await Commit(path, "feat: 添加并排 Diff 与行号显示");
        Write(path, "src/Diff/DiffParser.cs", "namespace GitManager.Diff;\n\npublic sealed record DiffLine(int Number, string Text);\n");
        await Commit(path, "fix: 保留 Diff 中的空行与行号");
        await Git(path, "switch", "main");
        Write(path, "README.md", "# GitManager\n\n一个清晰、轻巧的本地 Git 工作空间。\n\n## 功能\n- 分支提交图\n- 并排 Diff\n- 仓库切换\n\n这是自动生成的界面验证仓库。\n");
        await Commit(path, "docs: 补充功能说明与快捷键");
        await Git(path, "merge", "--no-ff", "feature/diff-view", "-m", "Merge branch 'feature/diff-view'");
        await Git(path, "tag", "v0.1.0");
        await Git(path, "switch", "-c", "feature/search");
        Write(path, "src/History/CommitSearch.cs", "namespace GitManager.History;\n\npublic sealed class CommitSearch\n{\n    public string Query { get; set; } = string.Empty;\n    public bool PreserveTopology => true;\n}\n");
        await Commit(path, "feat: 搜索提交并保留图结构");
        await Git(path, "switch", "main");
        Write(path, "src/Views/HistoryView.xaml", HistoryView(true));
        Write(path, "src/Views/CommitDetails.xaml", "<StackPanel>\n    <TextBlock Text=\"{Binding Author}\" />\n    <TextBlock Text=\"{Binding CommitDate}\" />\n    <TextBlock Text=\"{Binding ShortSha}\" />\n</StackPanel>\n");
        await Commit(path, "feat: 优化 History 分栏布局与提交详情", "将作者、时间与 SHA 元数据放在右侧顶部。\n变更文件与 Diff 并排展示，提交图随列表滚动。");
        Write(path, "src/Services/GitService.cs", (await File.ReadAllTextAsync(Path.Combine(path, "src/Services/GitService.cs"))).Replace("--all", "--all\", \"--prune", StringComparison.Ordinal));
        Write(path, "src/Views/SettingsView.xaml", "<StackPanel>\n    <TextBlock Text=\"偏好设置\" />\n    <CheckBox Content=\"显示所有分支\" IsChecked=\"True\" />\n</StackPanel>\n");
    }
    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    private static string HistoryView(bool updated) => updated ? """
        <UserControl x:Class="GitManager.Views.HistoryView"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid Background="#FFFFFF">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="430" />
                    <ColumnDefinition Width="1" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <!-- Commit list and graph share the same scroll position. -->
                <CommitList Grid.Column="0"
                            ItemsSource="{Binding Commits}"
                            ShowGraph="True" />

                <Grid Grid.Column="2">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>

                    <CommitDetails Author="{Binding Author}"
                                   Date="{Binding CommitDate}"
                                   Sha="{Binding ShortSha}" />

                    <Grid Grid.Row="1">
                        <ChangedFiles Width="215" />
                        <DiffViewer IsSplit="True" />
                    </Grid>
                </Grid>
            </Grid>
        </UserControl>
        """ : """
        <UserControl x:Class="GitManager.Views.HistoryView"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid Background="#FFFFFF">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*" />
                    <RowDefinition Height="280" />
                </Grid.RowDefinitions>

                <CommitList ItemsSource="{Binding Commits}" />

                <Grid Grid.Row="1">
                    <ChangedFiles Width="200" />
                    <DiffViewer IsSplit="False" />
                </Grid>
            </Grid>
        </UserControl>
        """;
    private static async Task Commit(string path, string subject, string? body = null)
    {
        await Git(path, "add", "--all");
        var args = new List<string> { "commit", "-m", subject };
        if (body is not null) { args.Add("-m"); args.Add(body); }
        await Git(path, args.ToArray());
    }
    private static async Task Git(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var stamp = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(8)).AddMinutes(_clock++ * 37).ToString("o");
        start.Environment["GIT_AUTHOR_DATE"] = stamp;
        start.Environment["GIT_COMMITTER_DATE"] = stamp;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        if (process.ExitCode != 0) throw new Exception(await error);
    }
}
