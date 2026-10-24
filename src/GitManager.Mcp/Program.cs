using System.ComponentModel;
using GitManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var pipeArgument = Array.IndexOf(args, "--pipe");
if (pipeArgument >= 0 && pipeArgument + 1 >= args.Length) throw new ArgumentException("--pipe requires a pipe name.");
builder.Services.AddSingleton(new LocalToolBridgeClient(pipeArgument >= 0 ? args[pipeArgument + 1] : null));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<GitTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class GitTools(LocalToolBridgeClient bridge)
{
    [McpServerTool(Name = "get_active_repository", ReadOnly = true), Description("Get the repository explicitly shared by the running GitManager application. Use its opaque ID for subsequent tools. No arbitrary filesystem paths are accepted.")]
    public Task<string> Active(CancellationToken cancellationToken) => bridge.InvokeAsync(new("get_active_repository"), cancellationToken);

    [McpServerTool(Name = "get_repository_status", ReadOnly = true), Description("Read branch, cached upstream counts, changed files and active conflicts in the shared repository.")]
    public Task<string> Status(string repositoryId, CancellationToken cancellationToken) => bridge.InvokeAsync(new("get_repository_status", repositoryId), cancellationToken);

    [McpServerTool(Name = "get_commit_history", ReadOnly = true), Description("Read paged current-branch history, without author email. Maximum 50 commits per page.")]
    public Task<string> History(string repositoryId, CancellationToken cancellationToken, int limit = 20, int skip = 0) =>
        bridge.InvokeAsync(new("get_commit_history", repositoryId, Limit: limit, Skip: skip), cancellationToken);

    [McpServerTool(Name = "get_commit_details", ReadOnly = true), Description("Read a commit's message, author name, change statistics and file list by its full immutable SHA.")]
    public Task<string> Commit(string repositoryId, string commitId, CancellationToken cancellationToken) =>
        bridge.InvokeAsync(new("get_commit_details", repositoryId, CommitId: commitId), cancellationToken);

    [McpServerTool(Name = "get_diff", ReadOnly = true), Description("Read a bounded Diff for one changed file or one file in a specified commit. Paths are relative to the shared repository. Sensitive credential paths and linked working files are excluded.")]
    public Task<string> Diff(string repositoryId, string filePath, CancellationToken cancellationToken, string? commitId = null, bool staged = false) =>
        bridge.InvokeAsync(new("get_diff", repositoryId, filePath, commitId, Staged: staged), cancellationToken);

    [McpServerTool(Name = "get_sync_analysis"), Description("Compare local and upstream commits. Set refreshRemote=true to Fetch first; this updates remote-tracking refs but never pulls or pushes. Otherwise results describe cached remote information.")]
    public Task<string> Sync(string repositoryId, CancellationToken cancellationToken, bool refreshRemote = false) =>
        bridge.InvokeAsync(new("get_sync_analysis", repositoryId, RefreshRemote: refreshRemote), cancellationToken);

    [McpServerTool(Name = "create_operation_plan"), Description("Create an immutable, expiring Git preview. Actions: sync, sync_merge, sync_rebase, sync_remote (replace local tracked files/history), sync_local (force-with-lease push), rebase, reset_soft, reset_mixed, reset_hard, revert. Sync actions Fetch first; other actions require target. Merge Revert requires mainline. This tool never applies the plan.")]
    public Task<string> Plan(string repositoryId, string action, CancellationToken cancellationToken, string? target = null, int mainline = 0) =>
        bridge.InvokeAsync(new("create_operation_plan", repositoryId, Action: action, Target: target, Mainline: mainline), cancellationToken);

    [McpServerTool(Name = "apply_operation_plan"), Description("Request local user approval for an existing plan. Returns awaiting_approval, NOT success. Actual execution requires the user to review and approve in the GitManager UI. Repeating the request never repeats a Git operation.")]
    public Task<string> Apply(string repositoryId, string planId, CancellationToken cancellationToken) =>
        bridge.InvokeAsync(new("apply_operation_plan", repositoryId, PlanId: planId), cancellationToken);

    [McpServerTool(Name = "get_operation_status", ReadOnly = true), Description("Read a plan's execution/approval status. Failed, stale, cancelled or completed plans are never automatically retried.")]
    public Task<string> Operation(string repositoryId, string planId, CancellationToken cancellationToken) =>
        bridge.InvokeAsync(new("get_operation_status", repositoryId, PlanId: planId), cancellationToken);
}
