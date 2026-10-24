using System.Windows;
using GitManager.App.ViewModels;
using GitManager.Core;

namespace GitManager.App.Dialogs;

public partial class RecoveryWindow : Window
{
    private readonly MainViewModel _vm;
    public RecoveryWindow(Window owner, MainViewModel vm) { InitializeComponent(); Owner = owner; _vm = vm; DataContext = vm; }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not RecoveryPoint point) return;
        var dialog = new OperationDialog(this, "恢复所选备份", $"分支：{point.Branch}\n{point.Reason}", $"目标提交：{point.Head}\n保存时间：{point.Date.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" + (point.StashId is null ? "此备份仅包含已提交历史。" : "此备份同时包含当时的未提交文件与暂存状态。"),
            [new ActionChoice(true, "恢复到此备份", "当前状态会先保存为新的恢复点，然后恢复所选版本；不会自动推送远程。", true)]);
        if (dialog.ShowDialog() == true) await _vm.RestoreRecoveryAsync(point);
    }
}
