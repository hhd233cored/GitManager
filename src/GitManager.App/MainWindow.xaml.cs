using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GitManager.App.Dialogs;
using GitManager.App.ViewModels;
using GitManager.Core;
using Microsoft.Win32;

namespace GitManager.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private GridLength _historyFilesWidth = new(215);

    public MainWindow(string? repositoryPath = null, bool persistSettings = true)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(persistSettings);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += async (_, _) =>
        {
            var path = repositoryPath ?? ViewModel.LastRepository;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) await ViewModel.OpenRepositoryAsync(path);
        };
        Activated += async (_, _) =>
        {
            if (ViewModel.HasRepository && !ViewModel.IsBusy) await ViewModel.RefreshAsync(silent: true);
        };
        Closing += (_, _) => ViewModel.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RepositoryName)) Title = $"{ViewModel.RepositoryName} — GitManager";
        if (e.PropertyName == nameof(MainViewModel.IsHistory))
        {
            if (ViewModel.IsHistory)
            {
                FilesColumn.MinWidth = 150;
                FilesColumn.Width = _historyFilesWidth;
                FilesSplitterColumn.Width = new GridLength(1);
            }
            else
            {
                _historyFilesWidth = FilesColumn.Width;
                FilesColumn.MinWidth = 0;
                FilesColumn.Width = new GridLength(0);
                FilesSplitterColumn.Width = new GridLength(0);
            }
        }
        if (e.PropertyName == nameof(MainViewModel.SelectedCommit) && ViewModel.SelectedCommit is { } selected)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => CommitList.ScrollIntoView(selected));
    }

    private async void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var picker = new OpenFolderDialog { Title = "选择 Git 仓库文件夹" };
        if (picker.ShowDialog(this) == true) await ViewModel.OpenRepositoryAsync(picker.FolderName);
    }
    private async void CloneRepository_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new CloneWindow(this);
        if (dialog.ShowDialog() == true) await ViewModel.CloneAsync(dialog.RepositoryUrl, dialog.Destination);
    }
    private void RepositorySelector_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender, MinWidth = 295 };
        var open = new MenuItem { Header = "打开本地仓库…", InputGestureText = "Ctrl+O" };
        open.Click += OpenRepository_Click;
        menu.Items.Add(open);
        var clone = new MenuItem { Header = "克隆仓库…" };
        clone.Click += CloneRepository_Click;
        menu.Items.Add(clone);
        if (ViewModel.RecentRepositories.Count > 0) menu.Items.Add(new Separator());
        foreach (var path in ViewModel.RecentRepositories)
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, IsCheckable = true, IsChecked = path == ViewModel.Repository?.Path };
            item.Click += async (_, _) => await ViewModel.OpenRepositoryAsync(path);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void BranchSelector_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender, MinWidth = 270, MaxHeight = 500 };
        var create = new MenuItem { Header = "+  新建分支…" };
        create.Click += NewBranch_Click;
        menu.Items.Add(create);
        menu.Items.Add(new Separator());
        foreach (var branch in ViewModel.Branches)
        {
            var item = new MenuItem { Header = branch.Name, IsChecked = branch.IsCurrent, IsCheckable = true, ToolTip = branch.IsRemote ? "切换到对应的本地跟踪分支" : "切换到本地分支" };
            item.Click += async (_, _) => { if (!branch.IsCurrent) await ViewModel.SwitchBranchAsync(branch); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || !ViewModel.HasRepository) return;
        var prompt = new TextPromptWindow(this, "新建分支", "从当前 HEAD 创建并切换到新分支。");
        if (prompt.ShowDialog() == true) await ViewModel.CreateBranchAsync(prompt.Value);
    }
    private void RecentMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        foreach (var path in ViewModel.RecentRepositories)
        {
            var item = new MenuItem { Header = path, IsEnabled = !ViewModel.IsBusy };
            item.Click += async (_, _) => await ViewModel.OpenRepositoryAsync(path);
            RecentMenu.Items.Add(item);
        }
    }
    private async void RecentRepository_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string path } && !ViewModel.IsBusy) await ViewModel.OpenRepositoryAsync(path);
    }
    private void Changes_Click(object sender, RoutedEventArgs e) => ViewModel.IsHistory = false;
    private void History_Click(object sender, RoutedEventArgs e) => ViewModel.IsHistory = true;
    private void SplitDiff_Click(object sender, RoutedEventArgs e) => ViewModel.IsSplitDiff = true;
    private void UnifiedDiff_Click(object sender, RoutedEventArgs e) => ViewModel.IsSplitDiff = false;
    private async void StageFile_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: ChangedFile file } && !ViewModel.IsBusy) await ViewModel.ToggleStageAsync(file);
    }
    private void CopySha_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DetailSha.Length > 0) Clipboard.SetText(ViewModel.DetailSha);
    }
    private void CopyDiff_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Diff is not null) Clipboard.SetText(ViewModel.Diff.Patch);
    }
    private void CommitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitList.SelectedItem is { } selected) CommitList.ScrollIntoView(selected);
    }
    private void HistorySearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ViewModel.SelectNextMatch(); e.Handled = true; }
    }
    private void RevealRepository_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Repository is null) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(ViewModel.Repository.Path);
        Process.Start(start);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.O) { OpenRepository_Click(this, e); e.Handled = true; }
        else if (control && e.Key == Key.D1) { ViewModel.IsHistory = false; e.Handled = true; }
        else if (control && e.Key == Key.D2) { ViewModel.IsHistory = true; e.Handled = true; }
        else if (control && e.Key == Key.Enter && ViewModel.CommitCommand.CanExecute(null)) { ViewModel.CommitCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F5 && ViewModel.RefreshCommand.CanExecute(null)) { ViewModel.RefreshCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && ViewModel.IsBusy) { ViewModel.CancelCommand.Execute(null); e.Handled = true; }
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0]))
            await ViewModel.OpenRepositoryAsync(paths[0]);
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Ctrl+O：打开仓库\nCtrl+1 / Ctrl+2：Changes / History\nCtrl+Enter：提交已暂存的变更\nF5：刷新\nEsc：取消正在执行的操作\n\nHistory：左侧提交图与列表，右侧上方元数据、下方变更文件与 Diff。\n搜索框 Enter 定位下一个匹配项；搜索保留完整图结构。\n合并提交的 Diff 与第一父提交对比。\nChanges 中半选的文件同时包含已暂存与未暂存的内容。\nPull 仅允许快进；分叉时保留现状并显示 Git 错误。",
        "GitManager 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "GitManager\n\nWPF + .NET 10\n本地 Git 管理器 · 提交历史与分支图\n\n需要已安装并配置好的 Git。", "关于 GitManager", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
