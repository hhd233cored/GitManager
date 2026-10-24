using System.Security.Cryptography;
using System.Text;
using GitManager.Core;

namespace GitManager.Services;

public sealed class RepositoryTools(GitService git, RepositoryCoordinator coordinator, string? auditPath = null)
{
    private RepositoryContext? _context;
    private readonly object _plansLock = new();
    private readonly Dictionary<string, OperationPlan> _plans = [];
    public RepositoryContext? Context => Volatile.Read(ref _context);
    public event Action<OperationPlan>? PlanChanged;
    public event Action<string, bool>? RepositoryUpdated;

    public void SetRepository(string? path, string branch = "")
    {
        var previous = Context;
        if (path is not null) path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(previous?.Path, path, StringComparison.OrdinalIgnoreCase) && previous?.Branch == branch) return;
        // An opaque selection ID prevents a running conversation from silently retargeting another repository.
        Volatile.Write(ref _context, path is null ? null : new RepositoryContext(Guid.NewGuid().ToString("N"), path, Path.GetFileName(path), branch));
    }

    public RepositoryContext RequireContext(string id)
    {
        var context = Context;
        if (context is null || context.Id != id) throw new InvalidOperationException("仓库选择已变化，请重新读取当前仓库后再操作。");
        return context;
    }

    public async Task<string> CaptureFingerprintAsync(string repositoryId, CancellationToken ct = default)
    {
        using var lease = await coordinator.EnterAsync(cancellationToken: ct);
        return await git.GetWorkspaceFingerprintAsync(RequireContext(repositoryId).Path, ct);
    }

    public async Task<string> InvokeAsync(ToolCall call, CancellationToken ct = default)
    {
        using var lease = await coordinator.EnterAsync(cancellationToken: ct);
        if (call.Method == "get_active_repository") return ToolJson.Serialize(Context);
        var context = RequireContext(call.RepositoryId);
        var path = context.Path;
        switch (call.Method)
        {
            case "get_repository_status":
                var info = await git.GetRepositoryAsync(path, ct);
                var status = await git.GetStatusAsync(path, ct);
                return ToolJson.Serialize(new { repository = context, info.Branch, info.Upstream, info.Ahead, info.Behind, info.HasHead, info.HasRemote,
                    head = info.HasHead ? await git.ResolveCommitAsync(path, "HEAD", ct) : null,
                    operation = await git.GetOperationAsync(path, ct), totalFiles = status.Count,
                    files = status.Take(200).Select(file => new { file.Path, type = file.Status.ToString(), file.HasStagedChanges }), truncated = status.Count > 200,
                    remoteInformation = "本地缓存；需要最新信息时使用 get_sync_analysis(refreshRemote=true)" });
            case "get_commit_history":
                var commits = await git.GetHistorySliceAsync(path, call.Skip, call.Limit, ct);
                return ToolJson.Serialize(new { skip = Math.Clamp(call.Skip, 0, 100000), commits = commits.Select(DescribeCommit), nextSkip = Math.Clamp(call.Skip, 0, 100000) + commits.Count });
            case "get_commit_details":
                var commit = await ReadCommitAsync(path, call.CommitId, ct);
                var files = await git.GetCommitFilesAsync(path, commit, ct);
                return ToolJson.Serialize(new { commit = DescribeCommit(commit), stats = await git.GetCommitStatsAsync(path, commit, ct),
                    files = files.Take(200).Select(file => new { file.Path, type = file.Status.ToString() }), truncated = files.Count > 200 });
            case "get_diff":
                return await ReadDiffAsync(path, call, ct);
            case "get_sync_analysis":
                return await AnalyzeSyncAsync(context, call.RefreshRemote, ct);
            case "create_operation_plan":
                return ToolJson.Serialize(await CreatePlanAsync(context, call, ct));
            case "apply_operation_plan":
                return ToolJson.Serialize(RequestApproval(context.Id, call.PlanId));
            case "get_operation_status":
                return ToolJson.Serialize(GetPlan(context.Id, call.PlanId));
            default: throw new ArgumentException("不支持的工具。" );
        }
    }

    private static object DescribeCommit(CommitInfo commit) => new { commit.Id, commit.Parents, commit.Author, commit.Date,
        subject = Clip(commit.Subject, 500), body = Clip(commit.Body, 1500) };
    public static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "\n[内容已截断]";
    private async Task<CommitInfo> ReadCommitAsync(string path, string? id, CancellationToken ct)
    {
        if (id is null || id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit)) throw new ArgumentException("请提供历史列表中的完整提交 SHA。" );
        return await git.GetHistoryByIdAsync(path, id, ct);
    }

    private async Task<string> ReadDiffAsync(string path, ToolCall call, CancellationToken ct)
    {
        var relative = NormalizeFilePath(call.FilePath);
        if (IsSensitivePath(relative)) throw new InvalidOperationException("该路径可能包含密钥或本机配置，助手不读取其文件内容。");
        CommitInfo? commit = call.CommitId is null ? null : await ReadCommitAsync(path, call.CommitId, ct);
        var files = commit is null ? await git.GetStatusAsync(path, ct) : await git.GetCommitFilesAsync(path, commit, ct);
        var file = files.FirstOrDefault(file => file.Path == relative) ?? throw new ArgumentException("该文件不属于当前变更或指定提交。" );
        if (IsSensitivePath(file.OldPath ?? relative)) throw new InvalidOperationException("重命名前的文件可能包含密钥，助手不读取其内容。");
        if (commit is null)
        {
            var absolute = Path.Combine(path, relative);
            for (var entry = absolute; entry.Length > path.Length; entry = Path.GetDirectoryName(entry)!)
                if (File.Exists(entry) || Directory.Exists(entry))
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("助手不读取链接文件或链接目录中的工作区内容。");
            if (File.Exists(absolute) && new FileInfo(absolute).Length > 1024 * 1024) throw new InvalidOperationException("该文件超过 1 MB，请缩小分析范围。");
        }
        var patch = await git.GetDiffAsync(path, file, commit, call.Staged, ct, includeAllChanges: commit is null && !call.Staged);
        return ToolJson.Serialize(new { file = relative, commit = commit?.Id, patch = Clip(patch, 18000), truncated = patch.Length > 18000 });
    }

    public static string NormalizeFilePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new ArgumentException("请选择一个文件。" );
        relative = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is ".." or ".git" or "" || part.Contains(':')))
            throw new ArgumentException("只允许仓库内的相对文件路径。" );
        return relative;
    }
    public static bool IsSensitivePath(string relative)
    {
        var name = Path.GetFileName(relative.Replace('\\', '/')).ToLowerInvariant();
        return (name == ".env" || name.StartsWith(".env.") && name is not (".env.example" or ".env.sample" or ".env.template"))
            || name is "id_rsa" or "id_ed25519" or "credentials" or ".npmrc" or ".netrc"
            || new[] { ".pem", ".key", ".pfx", ".p12" }.Any(name.EndsWith);
    }

    private async Task<string> AnalyzeSyncAsync(RepositoryContext context, bool refresh, CancellationToken ct)
    {
        var info = await git.GetRepositoryAsync(context.Path, ct);
        if (refresh && info.HasRemote)
        {
            await git.FetchAsync(context.Path, ct);
            RepositoryUpdated?.Invoke(context.Id, true);
            info = await git.GetRepositoryAsync(context.Path, ct);
        }
        OperationPreview? comparison = null;
        var operation = await git.GetOperationAsync(context.Path, ct);
        if (info.Upstream is not null && info.HasHead && !info.IsDetached && !operation.IsActive && !operation.HasConflicts)
            comparison = await git.PreviewHistoryOperationAsync(context.Path, info.Upstream, "同步比较", ct);
        var suggestion = !info.HasRemote ? "尚未配置远程仓库" : info.IsDetached ? "先切换到本地分支"
            : info.Upstream is null ? "当前分支尚未设置上游" : info.Ahead > 0 && info.Behind > 0 ? "分支已分叉，可预览 merge 或 rebase"
            : info.Behind > 0 ? "可仅快进拉取" : info.Ahead > 0 ? "可推送本地提交" : "本地与上游提交一致";
        return ToolJson.Serialize(new { info.Branch, info.Upstream, info.Ahead, info.Behind, refreshed = refresh && info.HasRemote,
            remoteInformation = refresh && info.HasRemote ? "本次已 Fetch" : "本地缓存，尚未验证远程最新状态", suggestion,
            localCommits = comparison?.LocalCommits, incomingCommits = comparison?.IncomingCommits });
    }

    private async Task<OperationPlan> CreatePlanAsync(RepositoryContext context, ToolCall call, CancellationToken ct)
    {
        var action = call.Action ?? "";
        var title = action switch { "sync" => "同步当前分支", "sync_merge" => "合并远程更新", "sync_rebase" => "变基到远程",
            "sync_remote" => "采用远程版本", "sync_local" => "采用本地已提交版本", "rebase" => "变基当前分支",
            "reset_soft" => "重置分支，保留工作区和暂存区", "reset_mixed" => "重置分支，保留文件修改", "reset_hard" => "重置分支和已跟踪文件",
            "revert" => "创建撤销提交", _ => throw new ArgumentException("请选择支持的同步、变基、重置或撤销操作。") };
        var sync = action.StartsWith("sync", StringComparison.Ordinal);
        var preview = sync ? await git.PreviewSynchronizationAsync(context.Path, ct)
            : await git.PreviewHistoryOperationAsync(context.Path, call.Target ?? throw new ArgumentException("缺少目标提交或分支。"), title, ct);
        if (sync) RepositoryUpdated?.Invoke(context.Id, true);
        preview = preview with { Title = title };
        if (action == "revert")
        {
            var commit = await git.GetHistoryByIdAsync(context.Path, preview.Target, ct);
            if (commit.Parents.Count > 1 && (call.Mainline < 1 || call.Mainline > commit.Parents.Count))
                throw new ArgumentException("撤销合并提交需要选择主线父提交编号。" );
        }
        var plan = new OperationPlan(Guid.NewGuid().ToString("N"), context.Id, action, preview,
            await git.GetWorkspaceFingerprintAsync(context.Path, ct), call.Mainline, DateTimeOffset.UtcNow);
        lock (_plansLock)
        {
            foreach (var old in _plans.Values.Where(item => item.Status != "running").OrderBy(item => item.Created).Take(Math.Max(0, _plans.Count - 39)).ToArray()) _plans.Remove(old.Id);
            _plans.Add(plan.Id, plan);
        }
        Publish(plan);
        return plan;
    }

    public OperationPlan GetPlan(string repositoryId, string? id)
    {
        RequireContext(repositoryId);
        lock (_plansLock)
        {
            if (id is null || !_plans.TryGetValue(id, out var plan) || plan.RepositoryId != repositoryId) throw new ArgumentException("找不到此操作计划，请重新生成。" );
            return plan;
        }
    }
    private OperationPlan RequestApproval(string repositoryId, string? id)
    {
        var plan = GetPlan(repositoryId, id);
        if (plan.Status != "preview") return plan;
        if (DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(15)) throw new InvalidOperationException("操作预览已过期，请重新生成。" );
        plan = plan with { Status = "awaiting_approval", Result = "等待用户在 GitManager 界面查看预览并执行；此调用没有修改仓库。" };
        SavePlan(plan);
        return plan;
    }
    public void DeclinePlan(string repositoryId, string id)
    {
        var plan = GetPlan(repositoryId, id);
        if (plan.CanRequestApproval) SavePlan(plan with { Status = "declined", Result = "用户已取消" });
    }

    // Intentionally absent from InvokeAsync and MCP: only a local, explicit UI approval can call this.
    public async Task<OperationPlan> ExecuteApprovedPlanAsync(string repositoryId, string id, CancellationToken ct = default)
    {
        using var lease = await coordinator.EnterAsync(cancellationToken: ct);
        var plan = GetPlan(repositoryId, id);
        if (!plan.CanRequestApproval) return plan;
        if (DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(15)
            || await git.GetWorkspaceFingerprintAsync(plan.Preview.RepositoryPath, ct) != plan.Fingerprint)
        {
            plan = plan with { Status = "stale", Result = "仓库、暂存区或文件内容已变化，请重新生成预览。" };
            SavePlan(plan);
            return plan;
        }
        plan = plan with { Status = "running", Result = null };
        SavePlan(plan);
        try
        {
            var path = plan.Preview.RepositoryPath;
            switch (plan.Action)
            {
                case "sync": await git.SynchronizeAsync(path, plan.Preview, SynchronizationMode.Normal, ct); break;
                case "sync_merge": await git.SynchronizeAsync(path, plan.Preview, SynchronizationMode.Merge, ct); break;
                case "sync_rebase": await git.SynchronizeAsync(path, plan.Preview, SynchronizationMode.Rebase, ct); break;
                case "sync_remote": await git.SynchronizeAsync(path, plan.Preview, SynchronizationMode.Remote, ct); break;
                case "sync_local": await git.SynchronizeAsync(path, plan.Preview, SynchronizationMode.Local, ct); break;
                case "rebase": await git.RebaseAsync(path, plan.Preview, ct); break;
                case "reset_soft": await git.ResetAsync(path, plan.Preview, ResetMode.Soft, ct); break;
                case "reset_mixed": await git.ResetAsync(path, plan.Preview, ResetMode.Mixed, ct); break;
                case "reset_hard": await git.ResetAsync(path, plan.Preview, ResetMode.Hard, ct); break;
                case "revert": await git.RevertAsync(path, plan.Preview, plan.Mainline, ct); break;
                default: throw new InvalidOperationException("不支持的操作。" );
            }
            var operation = await git.GetOperationAsync(path, ct);
            plan = plan with { Status = operation.IsActive || operation.HasConflicts ? "needs_attention" : "completed",
                Result = operation.IsActive || operation.HasConflicts ? "需要在冲突处理界面继续操作。" : "操作已完成，原版本按现有恢复机制保留。" };
        }
        catch (OperationCanceledException) { plan = plan with { Status = "cancelled", Result = "操作已取消，请查看仓库实际状态；此计划不会自动重试。" }; }
        catch (Exception ex) { plan = plan with { Status = "failed", Result = ToolErrors.Message(ex) }; }
        SavePlan(plan);
        RepositoryUpdated?.Invoke(repositoryId, false);
        return plan;
    }

    private void SavePlan(OperationPlan plan) { lock (_plansLock) _plans[plan.Id] = plan; Publish(plan); }
    private void Publish(OperationPlan plan)
    {
        if (auditPath is not null)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
                // Store operation metadata only: never prompts, file contents, or model credentials.
                File.AppendAllText(auditPath, ToolJson.Serialize(new { timestamp = DateTimeOffset.UtcNow, plan.Id, plan.RepositoryId, plan.Action, plan.Status,
                    head = plan.Preview.Head, target = plan.Preview.Target }) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        PlanChanged?.Invoke(plan);
    }
}
