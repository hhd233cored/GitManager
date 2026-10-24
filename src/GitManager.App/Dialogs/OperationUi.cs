using System.Windows;
using GitManager.App.ViewModels;
using GitManager.Core;

namespace GitManager.App.Dialogs;

public static class OperationUi
{
    private static string Describe(OperationPreview plan) => $"当前分支：{plan.Branch}\n当前提交：{plan.Head}\n目标提交：{plan.Target}\n未提交文件：{plan.ChangedFiles}\n\n"
        + "当前分支独有提交（最多 100 条）：\n" + string.Join('\n', plan.LocalCommits) + "\n\n目标独有提交（最多 100 条）：\n" + string.Join('\n', plan.IncomingCommits) + "\n\n文件差异：\n" + plan.Details;
    public static async Task SaveStashAsync(Window owner, MainViewModel vm)
    {
        if (!vm.HasRepository || vm.IsBusy || vm.HasActiveOperation) return;
        var dialog = new SaveStashDialog(owner, vm.ChangeCount, vm.IncludedCount);
        if (dialog.ShowDialog() == true) await vm.SaveStashAsync(dialog.Message, dialog.IncludeUntracked, dialog.SelectedOnly);
    }
    public static async Task ResetAsync(Window owner, MainViewModel vm, string target, bool undo = false)
    {
        var plan = await vm.PreviewOperationAsync(target, undo ? "撤销最近提交，保留修改" : "重置当前分支");
        if (plan is null) return;
        var choices = new[] {
            new ActionChoice(ResetMode.Soft, "保留工作区和暂存区（Soft）", "只移动分支指针。文件及暂存状态保持不变，可重新整理提交。"),
            new ActionChoice(ResetMode.Mixed, "保留文件修改，取消暂存（Mixed）", "移动分支指针并重置暂存区，工作区文件保留。"),
            new ActionChoice(ResetMode.Hard, "采用目标版本，保存当前改动到备份（Hard）", "当前分支和已跟踪文件将对齐到目标提交。原提交与未提交改动会保留恢复点。", true) };
        var dialog = new OperationDialog(owner, plan.Title, "操作只影响当前本地分支，不会自动推送。执行前会创建恢复点。", Describe(plan), choices, undo ? 0 : 1);
        if (dialog.ShowDialog() == true) await vm.ResetAsync(plan, (ResetMode)dialog.SelectedValue);
    }
    public static async Task RebaseAsync(Window owner, MainViewModel vm, string? target = null)
    {
        if (!vm.HasRepository || vm.IsBusy || vm.HasActiveOperation) return;
        if (target is null)
        {
            var options = vm.Branches.Where(branch => !branch.IsCurrent).Select(branch => new ActionChoice(branch.Name, branch.Name, "将当前分支的独有提交重放到该分支之上。下一步会显示具体影响。")).ToArray();
            if (options.Length == 0) return;
            var pick = new OperationDialog(owner, "选择变基目标", "当前分支：" + vm.BranchName, "", options, submitLabel: "预览");
            if (pick.ShowDialog() != true) return;
            target = (string)pick.SelectedValue;
        }
        var plan = await vm.PreviewOperationAsync(target, "将当前分支变基到目标版本");
        if (plan is null) return;
        var dialog = new OperationDialog(owner, plan.Title, "原提交会保留恢复点，未提交改动会先保存，成功后恢复。变基后的提交 SHA 会变化。", Describe(plan),
            [new ActionChoice(true, "开始变基", "遇到冲突时可以逐个处理，然后继续或中止。")]);
        if (dialog.ShowDialog() == true) await vm.RebaseAsync(plan);
    }
    public static async Task RevertAsync(Window owner, MainViewModel vm, CommitItem item)
    {
        var mainline = 0;
        if (item.Commit.Parents.Count > 1)
        {
            var choices = item.Commit.Parents.Select((id, index) => new ActionChoice(index + 1, $"第 {index + 1} 个父提交 · {id[..8]}", "将相对此父提交引入的变化反向撤销。")).ToArray();
            var parent = new OperationDialog(owner, "选择撤销合并的主线", item.Commit.Subject, "撤销合并会影响后续再次合并时的行为，请核对主线。", choices, submitLabel: "预览");
            if (parent.ShowDialog() != true) return;
            mainline = (int)parent.SelectedValue;
        }
        var plan = await vm.PreviewOperationAsync(item.Commit.Id, "撤销此提交（Revert）");
        if (plan is null) return;
        var dialog = new OperationDialog(owner, plan.Title, item.Commit.Subject + "\n创建新的反向提交，保留原有历史。当前改动会先保存。",
            $"当前分支：{plan.Branch}\n撤销提交：{item.Commit.Id}\n" + (mainline > 0 ? $"主线父提交：{mainline}\n" : "") + $"待保存文件：{plan.ChangedFiles}",
            [new ActionChoice(true, "创建撤销提交", "执行后可在 History 中查看新的反向提交；冲突时可以继续或中止。")]);
        if (dialog.ShowDialog() == true) await vm.RevertAsync(plan, mainline);
    }
    public static async Task SynchronizeAsync(Window owner, MainViewModel vm, SynchronizationMode initial = SynchronizationMode.Normal)
    {
        var plan = await vm.PreviewSynchronizationAsync();
        if (plan is null) return;
        var choices = new[] {
            new ActionChoice(SynchronizationMode.Normal, "常规同步", "单方面领先时快进拉取或普通推送；双方分叉时不会自动覆盖。"),
            new ActionChoice(SynchronizationMode.Merge, "合并远程提交", "保留两边的历史；必要时创建合并提交。"),
            new ActionChoice(SynchronizationMode.Rebase, "变基到远程提交", "将本地独有提交重放到远程版本之上。"),
            new ActionChoice(SynchronizationMode.Remote, "采用远程版本", "将当前本地分支及已跟踪文件对齐到本次获取的远程 SHA。原提交和未提交文件保存到恢复点，不额外清理忽略文件。", true),
            new ActionChoice(SynchronizationMode.Local, "采用本地已提交版本", "将本地 HEAD 推送到上游，替换远程分支历史。未提交文件不会上传。保存远程原版本，并校验远程仍是预览时的 SHA；分支保护仍生效。", true) };
        var dialog = new OperationDialog(owner, plan.Title, "比较对象为当前分支及其上游。覆盖前会保留恢复点。", Describe(plan), choices, Array.FindIndex(choices, choice => (SynchronizationMode)choice.Value == initial));
        if (dialog.ShowDialog() == true) await vm.SynchronizeAsync(plan, (SynchronizationMode)dialog.SelectedValue);
    }
    public static async Task ResolveConflictAsync(Window owner, MainViewModel vm)
    {
        var content = await vm.ReadConflictAsync();
        if (content is null) return;
        var dialog = new ConflictWindow(owner, content, vm.CurrentOperation.Kind == RepositoryOperationKind.Rebase);
        if (dialog.ShowDialog() == true) await vm.ResolveConflictAsync(content.Path, dialog.Resolution, dialog.ResultText);
    }
}
