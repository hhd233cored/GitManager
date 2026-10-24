using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitManager.Agent;
using GitManager.Core;
using GitManager.Services;
using ModelContextProtocol.Client;

var root = Path.Combine(Path.GetFullPath("artifacts/agent-checks"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "repository");
Directory.CreateDirectory(path);
var git = new GitService();
var runner = new GitRunner();
await runner.RunAsync(path, ["init", "-b", "main"]);
await runner.RunAsync(path, ["config", "user.name", "Agent Check"]);
await runner.RunAsync(path, ["config", "user.email", "private-email@example.test"]);
await runner.RunAsync(path, ["config", "commit.gpgSign", "false"]);
await runner.RunAsync(path, ["config", "core.hooksPath", ".git/no-hooks"]);
await File.WriteAllTextAsync(Path.Combine(path, "sample.txt"), "first\n");
await git.StageAsync(path); await git.CommitAsync(path, "initial", "");
var first = await git.ResolveCommitAsync(path, "HEAD");
await File.WriteAllTextAsync(Path.Combine(path, "sample.txt"), "second\n");
await git.StageAsync(path); await git.CommitAsync(path, "second", "");
var head = await git.ResolveCommitAsync(path, "HEAD");
await File.WriteAllTextAsync(Path.Combine(path, "sample.txt"), "third!\n");
await File.WriteAllTextAsync(Path.Combine(path, ".env"), "SECRET_VALUE=do-not-send\n");
var coordinator = new RepositoryCoordinator();
var tools = new RepositoryTools(git, coordinator, Path.Combine(root, "audit.jsonl"));
tools.SetRepository(path, "main");
var context = tools.Context!;
var failures = new List<string>();
var passed = 0;
await Check("Scoped, bounded history and Diff omit email and reject sensitive paths", async () =>
{
    var history = await tools.InvokeAsync(new("get_commit_history", context.Id, Limit: 1));
    Assert(!history.Contains("private-email@") && Json(history).GetProperty("commits").GetArrayLength() == 1, "History scope/privacy");
    var diff = await tools.InvokeAsync(new("get_diff", context.Id, "sample.txt"));
    Assert(diff.Contains("third!"), "Working Diff missing");
    await Throws(() => tools.InvokeAsync(new("get_diff", context.Id, ".env")));
    await Throws(() => tools.InvokeAsync(new("get_diff", context.Id, "../sample.txt")));
    await Throws(() => tools.InvokeAsync(new("get_repository_status", "wrong-repository")));
    var details = await tools.InvokeAsync(new("get_commit_details", context.Id, CommitId: head));
    Assert(!details.Contains("private-email@"), "Email leaked through commit details");
    var error = ToolErrors.Redact("https://user:secret@example.test/repo?token=secret2 Bearer secret3");
    Assert(!error.Contains("secret"), "Credentials leaked through tool errors");
});
await Check("UI and agent operations share one cancellable queue", async () =>
{
    using var blocked = await coordinator.EnterAsync();
    using var cancel = new CancellationTokenSource();
    var query = tools.InvokeAsync(new("get_repository_status", context.Id), cancel.Token);
    await Task.Delay(60);
    Assert(!query.IsCompleted, "Agent bypassed coordinator");
    cancel.Cancel();
    await Throws(() => query);
    blocked!.Dispose();
    await tools.InvokeAsync(new("get_repository_status", context.Id));
});
await Check("Approval requests never execute; same-status content edits invalidate previews", async () =>
{
    var plan = Plan(await tools.InvokeAsync(new("create_operation_plan", context.Id, Action: "reset_soft", Target: first)));
    await tools.InvokeAsync(new("apply_operation_plan", context.Id, PlanId: plan.Id));
    await tools.InvokeAsync(new("apply_operation_plan", context.Id, PlanId: plan.Id));
    Assert(await git.ResolveCommitAsync(path, "HEAD") == head, "Approval request executed Git");
    await File.WriteAllTextAsync(Path.Combine(path, "sample.txt"), "fourth\n");
    var result = await tools.ExecuteApprovedPlanAsync(context.Id, plan.Id);
    Assert(result.Status == "stale" && await git.ResolveCommitAsync(path, "HEAD") == head, "Stale preview was applied");
});
await Check("Approved plan executes once, retains recovery and records metadata only", async () =>
{
    var plan = Plan(await tools.InvokeAsync(new("create_operation_plan", context.Id, Action: "reset_soft", Target: first)));
    var result = await tools.ExecuteApprovedPlanAsync(context.Id, plan.Id);
    Assert(result.Status == "completed", result.Result ?? result.Status);
    var backups = (await git.GetRecoveryPointsAsync(path)).Count;
    result = await tools.ExecuteApprovedPlanAsync(context.Id, plan.Id);
    await tools.InvokeAsync(new("apply_operation_plan", context.Id, PlanId: plan.Id));
    Assert(await git.ResolveCommitAsync(path, "HEAD") == first && (await git.GetRecoveryPointsAsync(path)).Count == backups, "Plan repeated");
    Assert(backups > 0, "No recovery point");
    Assert(!File.ReadAllText(Path.Combine(root, "audit.jsonl")).Contains("do-not-send"), "Audit stored file contents");
});
await Check("Index changes and selection generation invalidate prior contexts", async () =>
{
    var before = await tools.CaptureFingerprintAsync(context.Id);
    await git.StageAsync(path, (await git.GetStatusAsync(path)).First(file => file.Path == "sample.txt"));
    Assert(before != await tools.CaptureFingerprintAsync(context.Id), "Index was not fingerprinted");
    tools.SetRepository(path, "another-branch");
    await Throws(() => tools.InvokeAsync(new("get_repository_status", context.Id)));
    tools.SetRepository(path, "main"); context = tools.Context!;
});
await Check("OpenAI-compatible tool loop reads selected Diff and produces a reviewable draft", async () =>
{
    await using var endpoint = new MockModel(streaming: false);
    var options = new AssistantOptions(endpoint.BaseUrl, "offline-test-model", UseStreaming: false);
    using var model = AssistantClientFactory.Create(options, "offline-test-key");
    var result = await new AssistantSession(tools).RunAsync(model, options,
        new(context, AssistantTask.CommitDraft, "生成所选文件的提交草稿", ["sample.txt"]));
    Assert(result.Draft is { Summary: "fix: 更新示例内容" } && result.Draft.SelectedPaths.SequenceEqual(["sample.txt"]), "Missing draft");
    Assert(endpoint.RequestCount == 3 && endpoint.ReceivedDiff && !endpoint.SawSecret, "Tool loop did not use the real bounded Diff");
    Assert(result.Draft!.Fingerprint == await tools.CaptureFingerprintAsync(context.Id), "Draft snapshot changed");
});
await Check("Streaming compatible model performs tools and emits text", async () =>
{
    await using var endpoint = new MockModel(streaming: true);
    var options = new AssistantOptions(endpoint.BaseUrl, "offline-test-model");
    using var model = AssistantClientFactory.Create(options, "offline-test-key");
    var result = await new AssistantSession(tools).RunAsync(model, options,
        new(context, AssistantTask.CommitDraft, "生成草稿", ["sample.txt"]));
    Assert(result.Text.Contains("草稿") && result.Draft is not null && endpoint.ReceivedDiff, "Streaming tool loop failed");
});
await Check("Content sharing can be disabled without disabling metadata tools", async () =>
{
    await using var endpoint = new MockModel(streaming: false);
    var options = new AssistantOptions(endpoint.BaseUrl, "offline-test-model", IncludeDiff: false, UseStreaming: false);
    using var model = AssistantClientFactory.Create(options, "offline-test-key");
    await new AssistantSession(tools).RunAsync(model, options, new(context, AssistantTask.Chat, "解释文件", ["sample.txt"]));
    Assert(!endpoint.ReceivedDiff && !endpoint.SawSecret, "Content-sharing setting ignored");
});
await Check("Sync analysis Fetches before deciding and never pulls automatically", async () =>
{
    var remote = Path.Combine(root, "remote.git");
    await runner.RunAsync(root, ["clone", "--bare", path, remote]);
    await runner.RunAsync(path, ["remote", "add", "origin", remote]);
    await git.FetchAsync(path);
    await runner.RunAsync(path, ["branch", "--set-upstream-to=origin/main", "main"]);
    var peer = Path.Combine(root, "peer"); await git.CloneAsync(remote, peer);
    await runner.RunAsync(peer, ["config", "user.name", "Remote Check"]);
    await runner.RunAsync(peer, ["config", "user.email", "remote@example.test"]);
    await runner.RunAsync(peer, ["config", "commit.gpgSign", "false"]);
    await runner.RunAsync(peer, ["config", "core.hooksPath", ".git/no-hooks"]);
    await File.WriteAllTextAsync(Path.Combine(peer, "remote.txt"), "remote change\n");
    await git.StageAsync(peer); await git.CommitAsync(peer, "remote change", ""); await git.PushAsync(peer);
    var cached = Json(await tools.InvokeAsync(new("get_sync_analysis", context.Id)));
    Assert(cached.GetProperty("behind").GetInt32() == 0 && !cached.GetProperty("refreshed").GetBoolean(), "Cached information mislabeled");
    var fresh = Json(await tools.InvokeAsync(new("get_sync_analysis", context.Id, RefreshRemote: true)));
    Assert(fresh.GetProperty("behind").GetInt32() == 1 && fresh.GetProperty("refreshed").GetBoolean(), "Fetch not reflected");
    Assert(await git.ResolveCommitAsync(path, "HEAD") == first && !File.Exists(Path.Combine(path, "remote.txt")), "Analysis pulled changes");
});
await Check("Official MCP client discovers tools and calls real stdio server through scoped local bridge", async () =>
{
    var pipe = "gitmanager-test-" + Guid.NewGuid().ToString("N");
    await using var bridge = new LocalToolBridgeServer(tools.InvokeAsync, pipe);
    var configuration = typeof(Program).Assembly.Location.Contains("Release") ? "Release" : "Debug";
    var executable = Path.GetFullPath($"src/GitManager.Mcp/bin/{configuration}/net10.0/GitManager.Mcp.exe");
    var transport = new StdioClientTransport(new() { Name = "gitmanager-check", Command = executable, Arguments = ["--pipe", pipe] });
    await using var client = await McpClient.CreateAsync(transport);
    var available = await client.ListToolsAsync();
    Assert(available.Any(tool => tool.Name == "get_diff") && available.Any(tool => tool.Name == "apply_operation_plan"), "Missing tools");
    var active = await client.CallToolAsync("get_active_repository", new Dictionary<string, object?>());
    Assert(ToolJson.Serialize(active).Contains(context.Id), "MCP wrong active repository");
    var status = await client.CallToolAsync("get_repository_status", new Dictionary<string, object?> { ["repositoryId"] = context.Id });
    Assert(!status.IsError.GetValueOrDefault(), "MCP status failed");
    var invalid = await client.CallToolAsync("get_diff", new Dictionary<string, object?> { ["repositoryId"] = context.Id, ["filePath"] = "../outside" });
    Assert(invalid.IsError == true, "MCP did not reject traversal");
    tools.SetRepository(null);
    var revoked = await client.CallToolAsync("get_repository_status", new Dictionary<string, object?> { ["repositoryId"] = context.Id });
    Assert(revoked.IsError == true, "MCP scope revocation failed");
});
Console.WriteLine($"\n{passed} passed, {failures.Count} failed. Fixtures: {root}");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + "\n" + ex); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static OperationPlan Plan(string json) => JsonSerializer.Deserialize<OperationPlan>(json, ToolJson.Options)!;
static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Throws(Func<Task> action)
{
    try { await action(); } catch { return; }
    throw new Exception("Expected rejection");
}

internal sealed class MockModel : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public string BaseUrl { get; }
    public int RequestCount { get; private set; }
    public bool ReceivedDiff { get; private set; }
    public bool SawSecret { get; private set; }
    public MockModel(bool streaming)
    {
        var portPicker = new TcpListener(IPAddress.Loopback, 0); portPicker.Start();
        var port = ((IPEndPoint)portPicker.LocalEndpoint).Port; portPicker.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start();
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    var request = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                    RequestCount++;
                    ReceivedDiff |= request.Contains("fourth");
                    SawSecret |= request.Contains("do-not-send") || request.Contains("private-email@example");
                    var name = RequestCount == 1 ? "get_diff" : "propose_commit_message";
                    var arguments = RequestCount == 1 ? ToolJson.Serialize(new { filePath = "sample.txt", commitId = (string?)null, staged = false })
                        : ToolJson.Serialize(new { summary = "fix: 更新示例内容", description = "更新所选示例文件。" });
                    var finished = RequestCount >= 3;
                    string json;
                    if (streaming)
                    {
                        context.Response.ContentType = "text/event-stream";
                        var delta = finished ? (object)new { role = "assistant", content = "草稿已生成，请查看后填入提交区。" }
                            : new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_" + RequestCount, type = "function", function = new { name, arguments } } } };
                        json = "data: " + ToolJson.Serialize(new { id = "offline-" + RequestCount, @object = "chat.completion.chunk", created = 100L, model = "offline-test-model",
                            choices = new[] { new { index = 0, delta, finish_reason = (string?)null } } }) + "\n\n";
                        json += "data: " + ToolJson.Serialize(new { id = "offline-" + RequestCount, @object = "chat.completion.chunk", created = 100L, model = "offline-test-model",
                            choices = new[] { new { index = 0, delta = new { }, finish_reason = finished ? "stop" : "tool_calls" } } }) + "\n\ndata: [DONE]\n\n";
                    }
                    else
                    {
                        context.Response.ContentType = "application/json";
                        var message = finished ? (object)new { role = "assistant", content = "草稿已生成，请查看后填入提交区。" }
                            : new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_" + RequestCount, type = "function", function = new { name, arguments } } } };
                        json = ToolJson.Serialize(new { id = "offline-" + RequestCount, @object = "chat.completion", created = 100L, model = "offline-test-model",
                            choices = new[] { new { index = 0, message, finish_reason = finished ? "stop" : "tool_calls" } } });
                    }
                    var bytes = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        });
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Close(); await _loop; _stop.Dispose();
    }
}
