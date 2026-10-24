using System.Text.Json;
using GitManager.Core;

namespace GitManager.Services;

public sealed record RepositoryContext(string Id, string Path, string Name, string Branch = "");
public sealed record ToolCall(string Method, string RepositoryId = "", string? FilePath = null, string? CommitId = null,
    int Limit = 20, int Skip = 0, bool RefreshRemote = false, bool Staged = false,
    string? Action = null, string? Target = null, string? PlanId = null, int Mainline = 0);
public sealed record ToolResponse(string? Result = null, string? Error = null);
public sealed record OperationPlan(string Id, string RepositoryId, string Action, OperationPreview Preview, string Fingerprint,
    int Mainline, DateTimeOffset Created, string Status = "preview", string? Result = null)
{
    public bool IsDestructive => Action is "reset_hard" or "sync_remote" or "sync_local";
    public bool CanRequestApproval => Status is "preview" or "awaiting_approval";
    public string Explanation => Action switch
    {
        "sync_remote" => "将当前本地分支及已跟踪文件对齐到预览中的远程 SHA。本地原提交、未提交文件和暂存状态先保留恢复点。忽略文件不清理。",
        "sync_local" => "将预览中的本地 HEAD 推送到上游，替换远程分支历史；不上传未提交文件。保留远程原提交，并以预览的远程 SHA 校验 force-with-lease。",
        "sync_merge" => "将预览中的远程版本合并到当前分支；分叉时可能创建合并提交。不额外推送。",
        "sync_rebase" or "rebase" => "将本地提交重放到目标版本之上，会改变被重放提交的 SHA。自动保存并恢复未提交修改；有冲突时需在原有冲突界面继续或中止。不额外推送。",
        "reset_soft" => "移动当前分支指针，保留工作区与暂存状态；原版本先保留恢复点。",
        "reset_mixed" => "移动当前分支指针并取消暂存，保留工作区文件；原版本先保留恢复点。",
        "reset_hard" => "移动当前分支指针，并将已跟踪文件对齐到目标版本。原提交与未提交修改先保留恢复点。",
        "revert" => "创建一个撤销目标修改的新提交，保留已有提交历史。有冲突时需继续处理。",
        "sync" when Preview.LocalCommits.Count > 0 && Preview.IncomingCommits.Count > 0 => "两边已分叉，不能直接同步。请取消并重新选择合并或变基。",
        "sync" when Preview.IncomingCommits.Count > 0 => "将当前分支快进到预览中的远程版本，不额外推送。",
        "sync" when Preview.LocalCommits.Count > 0 => "将当前本地已提交版本推送到上游，不包含未提交文件。",
        _ => "当前没有需要同步的独有提交。"
    };
    public string Description => $"{Preview.Title}\n分支：{Preview.Branch}\n当前：{Preview.Head}\n目标：{Preview.Target}\n未提交文件：{Preview.ChangedFiles}\n\n"
        + "本地独有提交：\n" + string.Join('\n', Preview.LocalCommits) + "\n\n目标独有提交：\n" + string.Join('\n', Preview.IncomingCommits)
        + "\n\n文件差异：\n" + Preview.Details;
}

public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
