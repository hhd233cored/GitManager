using System.Windows;
using GitManager.App.Dialogs;
using GitManager.App.ViewModels;
using GitManager.Services;

namespace GitManager.App;

public partial class MainWindow
{
    public AssistantViewModel Assistant { get; private set; } = null!;
    private GridLength _changesWidth;
    private GridLength _historyWidth;
    private GridLength _detailsWidth;
    private void InitializeAssistant(bool persist)
    {
        Assistant = new AssistantViewModel(ViewModel, persist);
        AssistantPanel.DataContext = Assistant;
        Assistant.SettingsRequested += () => new AssistantSettingsWindow(this, Assistant).ShowDialog();
        Assistant.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AssistantViewModel.IsOpen)) UpdateAssistantLayout(); };
        AssistantPanel.ReviewRequested += ReviewAssistantPlan;
    }
    private void ToggleAssistant_Click(object sender, RoutedEventArgs args) => Assistant.IsOpen = !Assistant.IsOpen;
    private void AssistantSettings_Click(object sender, RoutedEventArgs args) => new AssistantSettingsWindow(this, Assistant).ShowDialog();
    private void UpdateAssistantLayout()
    {
        if (Assistant.IsOpen)
        {
            WorkspaceColumn.MinWidth = 710;
            _changesWidth = ChangesColumn.Width; _historyWidth = HistoryColumn.Width; _detailsWidth = DetailsColumn.Width;
            ChangesColumn.MinWidth = 180; HistoryColumn.MinWidth = 220; DetailsColumn.MinWidth = 300;
            ChangesColumn.Width = new GridLength(.7, GridUnitType.Star);
            HistoryColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailsColumn.Width = new GridLength(1.2, GridUnitType.Star);
            AssistantColumn.Width = new GridLength(330); AssistantColumn.MinWidth = 300; AssistantColumn.MaxWidth = 550;
            AssistantSplitterColumn.Width = new GridLength(5);
            AssistantPanel.Visibility = AssistantSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            WorkspaceColumn.MinWidth = 0;
            AssistantPanel.Visibility = AssistantSplitter.Visibility = Visibility.Collapsed;
            AssistantColumn.MinWidth = 0; AssistantColumn.Width = new GridLength(0); AssistantSplitterColumn.Width = new GridLength(0);
            ChangesColumn.MinWidth = 225; HistoryColumn.MinWidth = 280; DetailsColumn.MinWidth = 390;
            ChangesColumn.Width = _changesWidth; HistoryColumn.Width = _historyWidth; DetailsColumn.Width = _detailsWidth;
        }
    }
    private async void ReviewAssistantPlan(OperationPlan plan)
    {
        if (ViewModel.IsBusy || ViewModel.RepositoryTools.Context?.Id != plan.RepositoryId) return;
        var choice = new ActionChoice("execute", plan.Preview.Title, plan.Explanation + "\n\n执行前会再次检查当前分支、暂存区和文件内容。若状态变化，需要重新生成预览。", plan.IsDestructive);
        var dialog = new OperationDialog(this, plan.Preview.Title, "这是助手提出的操作计划，请查看具体影响。", plan.Description, [choice], submitLabel: "确认执行");
        if (dialog.ShowDialog() != true) return;
        try { await ViewModel.ExecuteAssistantPlanAsync(plan); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
