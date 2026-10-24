using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.App.Dialogs;
using GitManager.App.ViewModels;
using GitManager.Core;

namespace GitManager.App;

public partial class MainWindow
{
    private void Stashes_Click(object sender, RoutedEventArgs e) { if (ViewModel.HasRepository) new StashWindow(this, ViewModel).Show(); }
    private void Recovery_Click(object sender, RoutedEventArgs e) { if (ViewModel.HasRepository) new RecoveryWindow(this, ViewModel).Show(); }
    private async void SaveStash_Click(object sender, RoutedEventArgs e) => await OperationUi.SaveStashAsync(this, ViewModel);
    private async void Rebase_Click(object sender, RoutedEventArgs e) => await OperationUi.RebaseAsync(this, ViewModel);
    private async void Synchronize_Click(object sender, RoutedEventArgs e) => await OperationUi.SynchronizeAsync(this, ViewModel);
    private async void ContinueOperation_Click(object sender, RoutedEventArgs e) => await ViewModel.ContinueOperationAsync();
    private async void AbortOperation_Click(object sender, RoutedEventArgs e)
    {
        var restorePending = ViewModel.CurrentOperation.Kind == RepositoryOperationKind.RestoreChanges;
        var dialog = new OperationDialog(this, ViewModel.AbortOperationLabel, ViewModel.OperationLabel, "",
            [new ActionChoice(true, ViewModel.AbortOperationLabel, restorePending ? "保存的改动继续留在 Stash 中，当前已完成的提交操作保持不变。" : "丢弃本次冲突处理中的修改，返回操作前的状态。原 Stash 与恢复点保留。", !restorePending)]);
        if (dialog.ShowDialog() == true) await ViewModel.AbortOperationAsync();
    }
    private async void ResolveConflict_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanResolveSelectedFile)
        {
            ViewModel.IsHistory = false;
            ViewModel.SelectedWorkingFile = ViewModel.WorkingFiles.FirstOrDefault(file => file.File.IsConflicted);
        }
        if (ViewModel.CanResolveSelectedFile) await OperationUi.ResolveConflictAsync(this, ViewModel);
    }
    private void CommitList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent;
        if (element is not ListBoxItem { DataContext: CommitItem { IsVisible: true } item } row) return;
        var mouseX = Math.Clamp(e.GetPosition(row).X, 0, row.ActualWidth);
        ViewModel.SelectedCommit = item;
        var menu = new ContextMenu();
        var enabled = ViewModel.CanStartOperation && ViewModel.Repository is { HasHead: true, IsDetached: false };
        void Add(string name, Func<Task> action)
        {
            var entry = new MenuItem { Header = name, IsEnabled = enabled };
            entry.Click += async (_, _) => await action(); menu.Items.Add(entry);
        }
        Add("撤销此提交（Revert）…", () => OperationUi.RevertAsync(this, ViewModel, item));
        Add("重置当前分支到此处…", () => OperationUi.ResetAsync(this, ViewModel, item.Commit.Id));
        Add("将当前分支变基到此处…", () => OperationUi.RebaseAsync(this, ViewModel, item.Commit.Id));
        if (item.Graph.IsHead && item.Commit.Parents.Count > 0)
            Add("撤销最近一次提交，保留修改…", () => OperationUi.ResetAsync(this, ViewModel, item.Commit.Parents[0], undo: true));
        menu.Items.Add(new Separator());
        var explain = new MenuItem { Header = "让助手解释此提交", IsEnabled = !Assistant.IsBusy };
        explain.Click += async (_, _) => await Assistant.RunAsync(GitManager.Agent.AssistantTask.ExplainCommit);
        menu.Items.Add(explain);
        e.Handled = true;
        OpenMenuBelow(row, menu, new Rect(mouseX, 0, 0, CommitItem.RowHeight));
    }
}
