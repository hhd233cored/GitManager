using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GitManager.App.Dialogs;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using GitManager.Core;
using Microsoft.Win32;

namespace GitManager.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(string? repositoryPath = null, bool persistSettings = true, SettingsStore? settingsStore = null)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(persistSettings, settingsStore);
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
        if (e.PropertyName == nameof(MainViewModel.SelectedCommit) && ViewModel.SelectedCommit is { } selected)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => CommitList.ScrollIntoView(selected));
    }

    private async void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "添加 Git 仓库（可多选）", Multiselect = true };
        if (picker.ShowDialog(this) == true) await ViewModel.AddRepositoriesAsync(picker.FolderNames);
    }
    private async void CloneRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CloneWindow(this);
        if (dialog.ShowDialog() == true) await ViewModel.CloneAsync(dialog.RepositoryUrl, dialog.Destination);
    }
    private void RepositorySelector_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, MinWidth = 330, MaxHeight = 560 };
        var open = new MenuItem { Header = "添加本地仓库…", InputGestureText = "Ctrl+O" };
        open.Click += OpenRepository_Click;
        menu.Items.Add(open);
        var clone = new MenuItem { Header = "克隆仓库…" };
        clone.Click += CloneRepository_Click;
        menu.Items.Add(clone);
        var manage = new MenuItem { Header = "管理仓库列表…", InputGestureText = "Ctrl+Shift+O" };
        manage.Click += ManageRepositories_Click;
        menu.Items.Add(manage);
        if (ViewModel.Repositories.Count > 0) menu.Items.Add(new Separator());
        foreach (var entry in ViewModel.Repositories)
        {
            var item = new MenuItem { Header = RepositoryMenuHeader(entry), ToolTip = entry.Path, IsCheckable = true, IsChecked = RepositoryEntry.SamePath(entry.Path, ViewModel.Repository?.Path) };
            item.Click += async (_, _) => await ViewModel.OpenRepositoryAsync(entry.Path);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private static StackPanel RepositoryMenuHeader(RepositoryEntry entry)
    {
        var panel = new StackPanel { MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = entry.Path, FontSize = 10, Opacity = .75, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }
    private void ManageRepositories_Click(object sender, RoutedEventArgs e) => new RepositoryManagerWindow(this, ViewModel).ShowDialog();
    private void SyncMenu_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var remote = ViewModel.SyncState.RemoteName;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = 225 };
        menu.Items.Add(new MenuItem { Header = $"Fetch {remote}", Command = ViewModel.FetchCommand, ToolTip = "获取远程更新，不修改工作区" });
        menu.Items.Add(new MenuItem { Header = $"Pull {remote}", Command = ViewModel.PullCommand, ToolTip = "拉取远程更新，仅允许快进" });
        menu.Items.Add(new MenuItem { Header = ViewModel.Repository?.Upstream is null ? "Publish branch" : $"Push {remote}", Command = ViewModel.PushCommand, ToolTip = "推送或发布当前分支" });
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void BranchSelector_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = 270, MaxHeight = 500 };
        var create = new MenuItem { Header = "+  新建分支…" };
        create.Click += NewBranch_Click;
        menu.Items.Add(create);
        menu.Items.Add(new Separator());
        foreach (var branch in ViewModel.Branches)
        {
            var item = new MenuItem { Header = branch.Name, IsChecked = branch.IsCurrent, IsCheckable = true, IsEnabled = !branch.IsCurrent,
                Tag = branch, ToolTip = branch.IsRemote ? "切换到对应的本地跟踪分支" : "切换到本地分支" };
            item.Click += async (_, _) => { if (!branch.IsCurrent) await ViewModel.SwitchBranchAsync(branch); };
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private async void RepositoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is RepositoryTreeItem item) await ViewModel.SelectHistoryPathAsync(item);
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
        foreach (var entry in ViewModel.Repositories)
        {
            var item = new MenuItem { Header = RepositoryMenuHeader(entry), ToolTip = entry.Path };
            item.Click += async (_, _) => await ViewModel.OpenRepositoryAsync(entry.Path);
            RecentMenu.Items.Add(item);
        }
    }
    private async void RecentRepository_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RepositoryEntry entry }) await ViewModel.OpenRepositoryAsync(entry.Path);
    }
    private void Changes_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsHistory = false;
        ChangesList.Focus();
    }
    private void History_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsHistory = true;
        CommitList.Focus();
    }
    private async void CommitHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommitItem item }) return;
        ViewModel.SelectedCommit = item;
        await ViewModel.ExpandCommitAsync(item);
        e.Handled = true;
    }
    private async void CommitExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommitItem item } || !item.Details.IsExpanded) return;
        await ViewModel.EnsureCommitFilesAsync(item);
    }
    private void HistoryFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CommitItem item, DataContext: FileItem file }) ViewModel.SelectHistoryFile(item, file);
        e.Handled = true;
    }
    private async void CommitToolTip_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ToolTip { DataContext: CommitItem item }) await ViewModel.EnsureCommitStatsAsync(item);
    }
    private void SplitDiff_Click(object sender, RoutedEventArgs e) => ViewModel.IsSplitDiff = true;
    private void UnifiedDiff_Click(object sender, RoutedEventArgs e) => ViewModel.IsSplitDiff = false;
    private void ChangesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is CheckBox || ViewModel.IsBusy) return;
        if (ChangesList.SelectedItem is not FileItem file) return;
        file.IsIncluded = !file.IsIncluded;
        e.Handled = true;
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
        if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.O) { ManageRepositories_Click(this, e); e.Handled = true; }
        else if (control && e.Key == Key.O) { OpenRepository_Click(this, e); e.Handled = true; }
        else if (control && e.Key == Key.D1) { Changes_Click(this, e); e.Handled = true; }
        else if (control && e.Key == Key.D2) { History_Click(this, e); e.Handled = true; }
        else if (control && e.Key == Key.Enter && ViewModel.CommitCommand.CanExecute(null)) { ViewModel.CommitCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F5 && ViewModel.RefreshCommand.CanExecute(null)) { ViewModel.RefreshCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && ViewModel.IsBusy) { ViewModel.CancelCommand.Execute(null); e.Handled = true; }
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            await ViewModel.AddRepositoriesAsync(paths.Where(Directory.Exists));
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Ctrl+O：添加本地仓库（可多选）\nCtrl+Shift+O：管理仓库列表\nCtrl+1 / Ctrl+2：聚焦 Changes / History\nCtrl+Enter：提交勾选文件\nSpace：切换当前文件的勾选\nF5：刷新\nEsc：取消正在执行的操作\n\n顶部同步按钮按状态显示 Fetch、Pull、Push 或 Publish，右侧下拉菜单可手动选择操作。\n仓库列表支持重命名、排序、更改位置与移除。移除会保留本地文件。\n当前分支菜单可切换本地分支，或从远程分支创建跟踪分支。\n三栏常驻：左侧 Changes，中间 History，右侧统一 Diff。点击历史提交旁的箭头可展开或收起文件，支持同时展开多个提交。点击工作区或历史文件可在右侧预览。\n悬停提交查看增删行数与完整信息；作者仅显示用户名。\n左下方资源管理器：选择文件或目录筛选历史，隐藏无关节点和文字，保留行占位与轨道线；目录包含子目录。点击“全部历史”或筛选条 × 可恢复。\n搜索框 Enter 定位下一个可见匹配项。\n拖动面板间分隔条可左右或上下调整尺寸；聚焦分隔条后可用方向键微调。\nChanges 默认勾选全部文件；总勾选框的半选状态表示仅选择了部分文件。\n勾选表示提交该文件的全部变更，刷新会保留选择。未勾选文件的修改与暂存内容会保留。\nPull 仅允许快进；分叉时保留现状并显示 Git 错误。",
        "GitManager 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "GitManager\n\nWPF + .NET 10\n本地 Git 管理器 · 提交历史与分支图\n\n需要已安装并配置好的 Git。", "关于 GitManager", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
