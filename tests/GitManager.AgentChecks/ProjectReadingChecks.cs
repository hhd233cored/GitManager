using System.Text;
using System.Text.Json;
using GitManager.Agent;
using GitManager.Core;
using GitManager.Services;
using Microsoft.Extensions.AI;

internal static class ProjectReadingChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        var path = Path.Combine(root, "project-reading"); Directory.CreateDirectory(path);
        var runner = new GitRunner(); var git = new GitService();
        await runner.RunAsync(path, ["init", "-b", "main"]);
        await runner.RunAsync(path, ["config", "core.autocrlf", "false"]);
        Directory.CreateDirectory(Path.Combine(path, "src"));
        var file = "src/登录 服务.cs";
        await File.WriteAllTextAsync(Path.Combine(path, file), "// Auth flow\nclass LoginService {\n  // 登录验证\n  void Login() {}\n}\n");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Sample project\nA login service.\n");
        await File.WriteAllTextAsync(Path.Combine(path, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), "ignored/\n");
        foreach (var directory in new[] { "private", "node_modules", "ignored", ".ssh" })
        {
            Directory.CreateDirectory(Path.Combine(path, directory));
            await File.WriteAllTextAsync(Path.Combine(path, directory, "hidden.txt"), "HIDDEN_SOURCE_SECRET");
        }
        await File.WriteAllTextAsync(Path.Combine(path, ".env"), "HIDDEN_SOURCE_SECRET");
        await File.WriteAllTextAsync(Path.Combine(path, "settings.local.json"), "HIDDEN_SOURCE_SECRET");
        await File.WriteAllBytesAsync(Path.Combine(path, "binary.bin"), [0, 1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(path, "large.txt"), new string('x', 1024 * 1024 + 1));
        await File.WriteAllTextAsync(Path.Combine(path, "long.txt"), new string('x', 6000) + "Needle\n" + string.Join('\n', Enumerable.Range(1, 250).Select(i => $"item {i}")));
        var tools = new RepositoryTools(git, new RepositoryCoordinator(), Path.Combine(root, "reading-audit.jsonl"));
        tools.SetRepository(path, "main"); var context = tools.Context!;
        tools.SetContentPolicy(new(ExcludedPaths: "private\n*.local.json"));

        await check("Project overview is static, cited, cached and invalidated by same-timestamp content edits", async () =>
        {
            var first = Json(await tools.InvokeAsync(new("get_project_overview", context.Id)));
            Assert(first.GetProperty("source").GetString() == "working_tree", "Source version missing");
            var overview = first.GetProperty("overview");
            Assert(overview.GetProperty("manifests").EnumerateArray().Any(item => item.GetString() == "Sample.csproj"), "Manifest missing");
            Assert(overview.GetProperty("documents")[0].GetProperty("citation").GetString()!.StartsWith("gitmanager-source://"), "Citation missing");
            Assert(Json(await tools.InvokeAsync(new("get_project_overview", context.Id))).GetProperty("cacheHit").GetBoolean(), "Repeated overview missed cache");
            var readme = Path.Combine(path, "README.md"); var timestamp = File.GetLastWriteTimeUtc(readme);
            await File.WriteAllTextAsync(readme, "# Changed project\nAn auth service.\n"); File.SetLastWriteTimeUtc(readme, timestamp);
            var changed = await tools.InvokeAsync(new("get_project_overview", context.Id));
            Assert(!Json(changed).GetProperty("cacheHit").GetBoolean() && changed.Contains("Changed project"), "Stale cached overview");
            var cache = Directory.GetFiles(Path.Combine(root, "project-cache"), "*.json").Select(File.ReadAllText);
            Assert(cache.Any() && cache.All(text => !text.Contains("Changed project") && !text.Contains("HIDDEN_SOURCE_SECRET")), "Persistent cache contains source text");
        });
        await check("Workspace file reads have exact line numbers, bounded ranges, hashes and safe Unicode paths", async () =>
        {
            var read = Json(await tools.InvokeAsync(new("read_file", context.Id, file, StartLine: 2, EndLine: 3)));
            Assert(read.GetProperty("lines").GetArrayLength() == 2 && read.GetProperty("lines")[0].GetProperty("line").GetInt32() == 2, "Line range incorrect");
            var hash = read.GetProperty("hash").GetString(); var timestamp = File.GetLastWriteTimeUtc(Path.Combine(path, file));
            await File.AppendAllTextAsync(Path.Combine(path, file), "// updated\n"); File.SetLastWriteTimeUtc(Path.Combine(path, file), timestamp);
            Assert(Json(await tools.InvokeAsync(new("read_file", context.Id, file))).GetProperty("hash").GetString() != hash, "Read cache ignored content change");
            var longRead = Json(await tools.InvokeAsync(new("read_file", context.Id, "long.txt", EndLine: int.MaxValue)));
            Assert(longRead.GetProperty("truncated").GetBoolean() && longRead.GetProperty("lines").GetArrayLength() <= 200, "Unbounded read");
            await Reject(() => tools.InvokeAsync(new("read_file", context.Id, file, StartLine: 10000)));
            await Reject(() => tools.InvokeAsync(new("read_file", context.Id, "binary.bin")));
            await Reject(() => tools.InvokeAsync(new("read_file", context.Id, "large.txt")));
        });
        await check("Project file pagination and sensitive/user-excluded paths cannot be bypassed", async () =>
        {
            var page = Json(await tools.InvokeAsync(new("list_files", context.Id, Limit: 1, Depth: 1)));
            Assert(page.GetProperty("entries").GetArrayLength() == 1 && page.GetProperty("nextSkip").GetInt32() == 1, "File pagination broken");
            var listed = await tools.InvokeAsync(new("list_files", context.Id, Limit: 200, Depth: 8));
            foreach (var excluded in new[] { ".env", "private/hidden.txt", "node_modules/hidden.txt", "ignored/hidden.txt", ".ssh/hidden.txt", "settings.local.json", "../README.md", ".git/config", "C:/Windows/win.ini", "src/../../README.md" })
                await Reject(() => tools.InvokeAsync(new("read_file", context.Id, excluded)));
            Assert(!listed.Contains("hidden.txt") && !listed.Contains("settings.local.json"), "Excluded names in project inventory");
            var search = await tools.InvokeAsync(new("search_code", context.Id, Query: "HIDDEN_SOURCE_SECRET"));
            Assert(Json(search).GetProperty("matches").GetArrayLength() == 0, "Search bypassed exclusions");
        });
        await check("Literal code search paginates, scopes paths and preserves matches beyond long-line prefixes", async () =>
        {
            var first = Json(await tools.InvokeAsync(new("search_code", context.Id, "src", Query: "login", Limit: 1)));
            Assert(first.GetProperty("matches").GetArrayLength() == 1 && first.GetProperty("nextSkip").GetInt32() == 1, "Search pagination missing");
            var second = Json(await tools.InvokeAsync(new("search_code", context.Id, "src", Query: "login", Limit: 1, Skip: 1)));
            Assert(second.GetProperty("matches")[0].GetProperty("line").GetInt32() == 4, "Incorrect continuation");
            Assert(Json(await tools.InvokeAsync(new("search_code", context.Id, file, Query: "login", MatchCase: true))).GetProperty("matches").GetArrayLength() == 0, "Case mode ignored");
            var longMatch = Json(await tools.InvokeAsync(new("search_code", context.Id, "long.txt", Query: "Needle")));
            Assert(longMatch.GetProperty("matches")[0].GetProperty("text").GetString()!.Contains("Needle"), "Truncated search excerpt lost match");
            var literal = await tools.InvokeAsync(new("search_code", context.Id, Query: "$(whoami);.*"));
            Assert(Json(literal).GetProperty("matches").GetArrayLength() == 0, "Query interpreted as regex or command");
        });
        await check("Shared service content revocation blocks source, search, overview and Diff for every caller", async () =>
        {
            tools.SetContentPolicy(new(false, false));
            foreach (var method in new[] { "get_project_overview", "list_files", "read_file", "search_code", "get_diff" })
                await Reject(() => tools.InvokeAsync(new(method, context.Id, file, Query: "Login")));
            await tools.InvokeAsync(new("get_repository_status", context.Id));
            tools.SetContentPolicy(new(ExcludedPaths: "private\n*.local.json"));
            await Reject(() => tools.InvokeAsync(new("read_file", context.Id, file), new RepositoryContentPolicy(false, false)));
            var pending = tools.InvokeAsync(new("search_code", context.Id, Query: "Login"));
            tools.SetContentPolicy(new(false, false));
            await Reject(() => pending);
            tools.SetContentPolicy(new(ExcludedPaths: "private\n*.local.json"));
            var priorId = context.Id;
            var priorQuery = tools.InvokeAsync(new("get_project_overview", priorId));
            tools.SetRepository(path, "other");
            await Reject(() => priorQuery);
            await Reject(() => tools.InvokeAsync(new("read_file", priorId, file)));
            tools.SetRepository(path, "main"); context = tools.Context!;
        });
        await check("Link directories cannot expose files from outside the selected repository", async () =>
        {
            var outside = Path.Combine(root, "outside-source"); Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), "OUTSIDE_SECRET");
            var link = Path.Combine(path, "linked");
            // Junction creation on Windows does not require Developer Mode or elevation.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert(process.ExitCode == 0, "Cannot create junction fixture");
            await Reject(() => tools.InvokeAsync(new("read_file", context.Id, "linked/outside.txt")));
            var search = await tools.InvokeAsync(new("search_code", context.Id, Query: "OUTSIDE_SECRET"));
            Assert(Json(search).GetProperty("matches").GetArrayLength() == 0, "Junction content leaked");
        });
        await check("Project reads release the Git queue, support cancellation and ignore inherited Git path overrides", async () =>
        {
            var coordinator = new RepositoryCoordinator();
            var isolated = new RepositoryTools(git, coordinator); isolated.SetRepository(path, "main");
            isolated.SetContentPolicy(new(ExcludedPaths: "private\n*.local.json"));
            var id = isolated.Context!.Id;
            var reading = isolated.InvokeAsync(new("search_code", id, Query: "Login"));
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (var lease = await coordinator.EnterAsync(cancellationToken: wait.Token))
                Assert(lease is not null, "Project read retained the Git queue");
            await reading;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject(() => isolated.InvokeAsync(new("read_file", id, file), cancelled.Token));
            var originalDir = Environment.GetEnvironmentVariable("GIT_DIR");
            try
            {
                Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(root, "nonexistent.git"));
                Assert(Json(await isolated.InvokeAsync(new("read_file", id, file))).GetProperty("file").GetString() == file, "Inherited environment changed repository scope");
            }
            finally { Environment.SetEnvironmentVariable("GIT_DIR", originalDir); }
        });
        await check("Agent discovers project structure, searches and reads actual source with tool progress", async () =>
        {
            using var model = new ProjectModel(file);
            var progress = new List<AssistantProgress>();
            var result = await new AssistantSession(tools).RunAsync(model, new("http://localhost/v1", "fixture", UseStreaming: false),
                new(context, AssistantTask.Chat, "解释登录流程", []), progress: new ImmediateProgress(progress));
            Assert(model.SawSource && result.Text.Contains("gitmanager-source://"), "Agent did not use source/citation");
            Assert(progress.Count(item => item.Kind == "tool") == 3, "Reading trace missing");
            using var denied = new ProjectModel(file);
            await new AssistantSession(tools).RunAsync(denied, new("http://localhost/v1", "fixture", IncludeDiff: false, UseStreaming: false), new(context, AssistantTask.Chat, "解释项目", []));
            Assert(!denied.SawSource, "Agent content toggle bypassed");
        });
    }

    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static void Assert(bool value, string error) { if (!value) throw new Exception(error); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected access rejection"); }
    private sealed class ImmediateProgress(List<AssistantProgress> values) : IProgress<AssistantProgress> { public void Report(AssistantProgress value) => values.Add(value); }
    private sealed class ProjectModel(string file) : IChatClient
    {
        private int _step;
        public bool SawSource { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? "").ToArray();
            SawSource |= results.Any(result => result.Contains("LoginService"));
            var step = _step++;
            if (step >= 3)
            {
                var source = results.Select(result => { try { return Json(result); } catch { return default; } })
                    .FirstOrDefault(result => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("citation", out _));
                var citation = source.ValueKind == JsonValueKind.Object ? source.GetProperty("citation").GetString() : "unavailable";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"登录服务：[源码]({citation})")));
            }
            var name = step switch { 0 => "get_project_overview", 1 => "search_code", _ => "read_file" };
            var arguments = step switch
            {
                0 => new Dictionary<string, object?>(),
                1 => new() { ["query"] = "LoginService", ["filePath"] = "src", ["limit"] = 20, ["skip"] = 0, ["matchCase"] = false },
                _ => new() { ["filePath"] = file, ["startLine"] = 1, ["endLine"] = 10 }
            };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("project-" + step, name, arguments)])));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
