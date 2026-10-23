using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class WorkspaceChecks
{
    public static async Task RunAsync(MainWindow window, string fixture, string output, Func<Window, string, Task> capture)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitManager.WorkspaceChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        var git = new GitService();
        var runner = new GitRunner();
        await git.CloneAsync(fixture, root);
        await runner.RunAsync(root, ["config", "user.name", "Workspace Checks"]);
        await runner.RunAsync(root, ["config", "user.email", "workspace@example.test"]);
        await runner.RunAsync(root, ["config", "commit.gpgSign", "false"]);
        await runner.RunAsync(root, ["config", "core.hooksPath", ".git/no-hooks"]);
        var vm = window.ViewModel;
        await vm.OpenRepositoryAsync(root);
        vm.IsHistory = true;
        window.Width = 1440;
        window.Height = 900;
        await Wait(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "open workspace fixture");

        var passive = vm.RefreshAsync(silent: true);
        Assert(!vm.IsBusy, "Passive refresh should not disable explicit user actions.");
        var branchButton = (Button)window.FindName("BranchSelectorButton");
        branchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var menu = branchButton.ContextMenu;
        Assert(menu is not null && menu.Items.OfType<MenuItem>().Any(item => item.Tag is BranchInfo), "Branch menu was lost during automatic refresh.");
        menu!.IsOpen = false;
        ClickBranch(menu, "origin/feature/commit-graph");
        await passive;
        await Wait(() => !vm.IsBusy && vm.BranchName == "feature/commit-graph", "switch through the remote branch menu");
        Assert(!vm.HasError && vm.Repository?.Upstream == "origin/feature/commit-graph", "Remote branch tracking failed.");
        var servicePath = Path.Combine(root, "src/Services/GitService.cs");
        var original = await File.ReadAllTextAsync(servicePath);
        await File.AppendAllTextAsync(servicePath, "\n// Preserve this edit if switching is blocked.\n");
        branchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        branchButton.ContextMenu!.IsOpen = false;
        ClickBranch(branchButton.ContextMenu, "main");
        await Wait(() => !vm.IsBusy && vm.HasError, "report checkout conflict");
        Assert(vm.BranchName == "feature/commit-graph" && (await File.ReadAllTextAsync(servicePath)).Contains("Preserve this edit", StringComparison.Ordinal), "A blocked switch discarded local changes.");
        await File.WriteAllTextAsync(servicePath, original);
        await vm.RefreshAsync();
        branchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        branchButton.ContextMenu!.IsOpen = false;
        ClickBranch(branchButton.ContextMenu, "main");
        await Wait(() => !vm.IsBusy && vm.BranchName == "main", "return to the local branch");
        Assert(!vm.HasError, vm.Error);
        Console.WriteLine("PASS branch menu during passive refresh, remote tracking, local switching and preservation of conflicting edits.");

        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "load branch history");
        var ids = vm.Commits.Select(item => item.Commit.Id).ToArray();
        var graph = vm.Commits.Select(item => item.Graph).ToArray();
        var readme = Find(vm, "README.md");
        readme.IsSelected = true;
        await Wait(() => vm.HistoryPath == "README.md" && !vm.IsFilteringHistory && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "select a file through the explorer");
        Assert(vm.IsHistory && vm.VisibleCommitCount > 0 && vm.VisibleCommitCount < vm.CommitCount, "Explorer selection did not mask unrelated commits.");
        Assert(ids.SequenceEqual(vm.Commits.Select(item => item.Commit.Id)), "Path filtering removed or reordered history rows.");
        Assert(graph.SequenceEqual(vm.Commits.Select(item => item.Graph)), "Path filtering changed graph geometry.");
        Assert(vm.Commits.Where(item => !item.IsVisible).All(item => item.Subject.Length == 0 && !item.HasDecorations), "Hidden records still have text or refs.");
        Assert(vm.SelectedFile?.Path == "README.md", "File filtering should open the corresponding Diff.");
        window.UpdateLayout();
        var list = (ListBox)window.FindName("CommitList");
        Assert(Descendants<GraphCell>(list).Any(cell => !cell.IsNodeVisible), "Unrelated graph nodes are still visible.");
        foreach (var container in Descendants<ListBoxItem>(list).Where(row => row.DataContext is CommitItem { IsVisible: false }))
            Assert(Math.Abs(container.ActualHeight - CommitItem.RowHeight) < 1, "Masked history rows lost their placeholder height.");
        await capture(window, Path.Combine(output, "workspace-file-history.png"));

        Find(vm, "src").IsExpanded = true;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Find(vm, "src/Diff").IsSelected = true;
        await Wait(() => vm.HistoryPath == "src/Diff" && !vm.IsFilteringHistory && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "select a nested directory through the explorer");
        var expected = await git.GetPathCommitIdsAsync(root, vm.Commits.Select(item => item.Commit).ToArray(), "src/Diff");
        Assert(expected.SetEquals(vm.Commits.Where(item => item.IsVisible).Select(item => item.Commit.Id)), "Directory filtering missed child changes.");
        Assert(vm.SelectedFile?.Path.StartsWith("src/Diff/", StringComparison.Ordinal) == true, "Directory selection did not open a matching file.");
        await capture(window, Path.Combine(output, "workspace-directory-history.png"));
        var lastFile = vm.SelectHistoryPathAsync(Find(vm, "README.md"));
        var lastFolder = vm.SelectHistoryPathAsync(Find(vm, "src/Diff"));
        await Task.WhenAll(lastFile, lastFolder);
        await Wait(() => vm.HistoryPath == "src/Diff" && !vm.IsFilteringHistory, "settle the latest selection and any background refresh");
        Assert(expected.SetEquals(vm.Commits.Where(item => item.IsVisible).Select(item => item.Commit.Id)), "A stale filter response replaced the latest selection.");
        await File.WriteAllTextAsync(Path.Combine(root, "new-untracked.txt"), "No commits yet.\n");
        await vm.RefreshAsync();
        Assert(Find(vm, "src").IsExpanded, "Refreshing the explorer lost directory expansion.");
        Find(vm, "new-untracked.txt").IsSelected = true;
        await Wait(() => vm.HistoryPath == "new-untracked.txt" && !vm.IsFilteringHistory && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "select an untracked file");
        Assert(vm.VisibleCommitCount == 0 && vm.SelectedCommit is null && vm.Diff is null && vm.Commits.Count == ids.Length, "A file without history should retain blank rows and clear commit details.");
        vm.ClearHistoryPathCommand.Execute(null);
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "restore all history");
        Assert(!vm.HasHistoryPathFilter && vm.Commits.All(item => item.IsVisible), "Clearing the filter did not restore history.");
        Console.WriteLine("PASS explorer file/folder selection, empty history, placeholders, graph geometry, latest-selection cancellation and refresh expansion.");

        window.UpdateLayout();
        CheckResize(window, "ChangesSplitter", "ChangesColumnPane", Key.Right, Key.Left, element => element.ActualWidth);
        CheckResize(window, "HistorySplitter", "HistoryPane", Key.Right, Key.Left, element => element.ActualWidth);
        CheckResize(window, "MetadataSplitter", "DiffPane", Key.Down, Key.Up, element => element.ActualHeight);
        CheckResize(window, "ExplorerSplitter", "ExplorerPane", Key.Up, Key.Down, element => element.ActualHeight);
        vm.IsHistory = false;
        window.UpdateLayout();
        var formRow = (RowDefinition)window.FindName("CommitFormRow");
        var initialFormHeight = formRow.ActualHeight;
        Resize((GridSplitter)window.FindName("CommitFormSplitter"), Key.Up);
        window.UpdateLayout();
        Assert(Math.Abs(formRow.ActualHeight - initialFormHeight) > 5, "The Changes commit form is not resizable.");
        Resize((GridSplitter)window.FindName("CommitFormSplitter"), Key.Down);
        window.UpdateLayout();
        var upperRow = (RowDefinition)window.FindName("ChangesBodyRow");
        var explorerRow = (RowDefinition)window.FindName("ExplorerRow");
        var upperHeight = upperRow.Height;
        var explorerHeight = explorerRow.Height;
        window.Width = 1100;
        window.Height = 680;
        window.UpdateLayout();
        for (var i = 0; i < 40 && upperRow.ActualHeight > upperRow.MinHeight + 1; i++)
        {
            Resize((GridSplitter)window.FindName("ExplorerSplitter"), Key.Up);
            window.UpdateLayout();
        }
        Assert(upperRow.ActualHeight <= upperRow.MinHeight + 1, "The upper panel could not reach its minimum height.");
        var commitButton = (Button)window.FindName("CommitButton");
        var upperPane = (FrameworkElement)window.FindName("ChangesPane");
        var buttonBottom = commitButton.TransformToAncestor(upperPane).Transform(new Point(0, commitButton.ActualHeight)).Y;
        Assert(buttonBottom <= upperPane.ActualHeight, "The compact Changes form overlaps the explorer at the minimum pane height.");
        await capture(window, Path.Combine(output, "workspace-changes-compact.png"));
        upperRow.Height = upperHeight;
        explorerRow.Height = explorerHeight;
        window.Width = 1440;
        window.Height = 900;
        vm.IsHistory = true;
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "restore History after resizing");
        await capture(window, Path.Combine(output, "workspace-panels.png"));
        Console.WriteLine("PASS horizontal and vertical splitter resizing for History, explorer, metadata, file list, Diff and Changes form.");
    }

    private static RepositoryTreeItem Find(MainViewModel vm, string path) => vm.RepositoryTree.SelectMany(root => root.DescendantsAndSelf()).Single(item => item.RelativePath == path);
    private static void ClickBranch(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Tag is BranchInfo branch && branch.Name == name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void CheckResize(MainWindow window, string splitterName, string paneName, Key forward, Key backward, Func<FrameworkElement, double> size)
    {
        var pane = (FrameworkElement)window.FindName(paneName);
        var splitter = (GridSplitter)window.FindName(splitterName);
        var before = size(pane);
        Resize(splitter, forward);
        window.UpdateLayout();
        Assert(Math.Abs(size(pane) - before) > 5, $"{splitterName} did not resize its adjacent panels.");
        Resize(splitter, backward);
        window.UpdateLayout();
        Assert(Math.Abs(size(pane) - before) < 1, $"{splitterName} could not restore its size.");
    }
    private static void Resize(GridSplitter splitter, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(splitter), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        splitter.RaiseEvent(args);
        Assert(args.Handled, "The splitter did not handle keyboard resizing.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Wait(Func<bool> condition, string action)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"Timed out trying to {action}.");
            await Task.Delay(60);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
