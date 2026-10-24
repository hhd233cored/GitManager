using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class ConcurrentHistoryChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<Window, string, Task> capture)
    {
        var vm = window.ViewModel;
        vm.ClearHistoryPath();
        vm.IsHistory = true;
        window.Width = 1440;
        window.Height = 900;
        await Wait(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "settle shared workspace");
        var changes = (Grid)window.FindName("ChangesPane");
        var history = (Grid)window.FindName("HistoryPane");
        var explorer = (Grid)window.FindName("ExplorerPane");
        var details = (Grid)window.FindName("DetailsPane");
        var list = (ListBox)window.FindName("CommitList");
        var diff = (DiffView)window.FindName("FileDiffView");
        window.UpdateLayout();
        Assert(Bounds(changes, window).Right < Bounds(history, window).Left
            && Bounds(history, window).Right < Bounds(details, window).Left, "Changes, History and Diff are not in three simultaneous columns.");
        Assert(Bounds(explorer, window).Top > Bounds(changes, window).Bottom, "Explorer must sit below Changes.");
        Assert(window.FindName("ChangesTab") is null && window.FindName("HistoryTab") is null, "The old page-switching tabs are still present.");

        var expanded = vm.Commits.Take(3).ToArray();
        foreach (var commit in expanded) await vm.ExpandCommitAsync(commit);
        vm.SelectedCommit = expanded[0];
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "load expanded commit files");
        list.ScrollIntoView(expanded[0]);
        window.UpdateLayout();
        Assert(expanded.All(item => item.Details.IsExpanded && item.Details.Files.Count > 0), "Multiple commits cannot remain expanded independently.");
        var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(expanded[0]);
        var rail = Descendants<GraphCell>(firstRow).Single(cell => cell.IsContinuation);
        Assert(firstRow.ActualHeight > CommitItem.RowHeight && rail.IsVisible && rail.ActualHeight >= 25, "Expanded files must extend the row and its graph rails.");
        var inlineFile = Descendants<Button>(firstRow).First(button => button.DataContext is FileItem);
        Assert(Descendants<FileStatusIcon>(inlineFile).Any(), "Inline files lost their + / minus / dot change indicators.");
        await capture(window, Path.Combine(output, "history-header-selection.png"));
        inlineFile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(() => !vm.IsLoadingDiff && vm.Diff is not null, "select inline historical file");
        Assert(vm.IsHistory && ReferenceEquals(vm.Diff, diff.Document) && vm.SelectedFile == inlineFile.DataContext, "Inline history files did not reach the shared Diff viewer.");
        await capture(window, Path.Combine(output, "history-inline-files.png"));

        var selected = (FileItem)inlineFile.DataContext;
        var commitId = expanded[0].Commit.Id;
        var patch = vm.Diff!.Patch;
        var git = new GitService();
        var expectedStats = await git.GetCommitStatsAsync(vm.Repository!.Path, expanded[0].Commit);
        await vm.EnsureCommitStatsAsync(expanded[0]);
        Assert(expectedStats == expanded[0].Details.Stats, "Hover statistics do not use the selected commit comparison.");
        Assert(expanded[0].AuthorDetails == expanded[0].Commit.Author && !expanded[0].AuthorDetails.Contains('<'), "Author details should contain only the user name.");

        if (vm.WorkingFiles.Count > 0)
        {
            var changeList = (ListBox)window.FindName("ChangesList");
            var work = vm.WorkingFiles.First();
            changeList.SelectedItem = work;
            await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "select working file in the shared Diff");
            Assert(vm.IsChanges && !vm.SelectedFile!.File.IsHistory && changes.IsVisible && history.IsVisible, "Selecting a working file hid History or kept historical Diff context.");
            Assert(ReferenceEquals(diff.Document, vm.Diff) && vm.DiffSourceLabel == "文件全部变更", "Working and history files must use the same Diff view.");
            Assert(expanded.All(item => item.Details.IsExpanded), "Changing Diff source collapsed history files.");
            var included = vm.IncludedCount;
            await vm.RefreshAsync();
            await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "refresh working Diff with expanded history");
            Assert(vm.IsChanges && vm.IncludedCount == included && vm.SelectedFile?.Path == work.Path, "Refresh changed working Diff context or commit inclusion.");
            await capture(window, Path.Combine(output, "changes-shared-diff.png"));
        }

        var restored = vm.Commits.Single(item => item.Commit.Id == commitId);
        Assert(ReferenceEquals(restored.Details, expanded[0].Details) && restored.Details.IsExpanded, "Refresh lost cached file expansions.");
        vm.SelectHistoryFile(restored, selected);
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "restore historical Diff");
        Assert(vm.IsHistory && vm.Diff!.Patch == patch && vm.SelectedWorkingFile is null, "Switching back loaded the wrong commit or kept the working selection highlighted.");
        // Exercise the actual expander button, including reopening cached details.
        list.ScrollIntoView(restored);
        window.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(restored);
        var expander = Descendants<ToggleButton>(row).Single();
        expander.IsChecked = false;
        expander.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.UpdateLayout();
        Assert(Math.Abs(row.ActualHeight - CommitItem.RowHeight) < .1, "Collapsing a commit did not restore compact row height.");
        expander.IsChecked = true;
        expander.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.UpdateLayout();
        Assert(row.ActualHeight > CommitItem.RowHeight, "Reopening cached files did not expand the row.");
        window.Width = 1100;
        window.Height = 720;
        await capture(window, Path.Combine(output, "history-three-columns-compact.png"));
        Assert(diff.ActualWidth >= 380 && changes.IsVisible && history.IsVisible, "The compact window clips a permanent panel.");
        var shaButton = (Button)window.FindName("CopyShaButton");
        Assert(Bounds(shaButton, details).Bottom <= ((RowDefinition)window.FindName("MetadataRow")).ActualHeight,
            "Commit SHA should remain visible above Diff in the compact layout.");
        window.Width = 1440;
        window.Height = 900;
        Console.WriteLine("PASS permanent Changes/History/explorer layout, independent inline expansions, graph continuations, hover statistics and shared working/history Diff selection.");
    }

    private static Rect Bounds(FrameworkElement item, Visual root) => item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
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
        await Task.Delay(100);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
