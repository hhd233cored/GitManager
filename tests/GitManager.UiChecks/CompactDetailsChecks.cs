using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.App;
using GitManager.App.ViewModels;

internal static class CompactDetailsChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<Window, string, Task> capture, bool reviewRepository = false)
    {
        var vm = window.ViewModel;
        window.Width = 1440;
        window.Height = 900;
        vm.ClearHistoryPath();
        vm.IsHistory = true;
        await Wait(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "settle compact details");
        var list = (ListBox)window.FindName("CommitList");
        if (reviewRepository)
        {
            foreach (var commit in vm.Commits) commit.Details.IsExpanded = false;
            var backup = vm.Commits.Single(item => item.Commit.Id.StartsWith("aced39d", StringComparison.Ordinal));
            var mcp = vm.Commits.Single(item => item.Commit.Id.StartsWith("aa306db", StringComparison.Ordinal));
            vm.SelectedCommit = backup;
            await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "select SkillTrust backup");
            Assert(backup.Graph.NodeLane > mcp.Graph.NodeLane, "The newer backup branch must start to the right of MCP.");
            var afterBackup = vm.Commits.SkipWhile(item => item.Commit.Id != backup.Commit.Id).Skip(1)
                .TakeWhile(item => item.Commit.Id != backup.Commit.Parents.Single());
            Assert(afterBackup.All(item => item.Graph.Edges.Where(edge => edge.Color == backup.Graph.Color)
                .All(edge => edge.FromLane == backup.Graph.NodeLane && edge.ToLane == backup.Graph.NodeLane)), "The actual backup rail still changes columns.");
            list.ScrollIntoView(backup);
            await capture(window, Path.Combine(output, "skilltrust-stable-graph.png"));
        }
        var description = reviewRepository
            ? vm.Commits.Single(item => item.Commit.Id.StartsWith("25fe5d5", StringComparison.Ordinal))
            : vm.Commits.FirstOrDefault(item => item.Commit.Body.Length > 0) ?? vm.Commits[0];
        vm.SelectedCommit = description;
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "display commit description");
        window.UpdateLayout();
        var workspace = (Grid)window.FindName("RepositoryWorkspace");
        var scroll = (ScrollViewer)window.FindName("CommitDetailsScrollViewer");
        var body = (TextBox)window.FindName("CommitBodyText");
        var author = (TextBlock)window.FindName("CommitAuthorText");
        var date = (TextBlock)window.FindName("CommitDateText");
        var summary = (TextBlock)window.FindName("CommitSubjectText");
        scroll.ScrollToTop();
        window.UpdateLayout();
        Assert(author.Text == description.Commit.Author && !author.Text.Contains('<'), "Commit details should show the username without email.");
        Assert(Top(author, scroll) < Top(summary, scroll) && Top(date, scroll) < Top(summary, scroll), "Username and time must appear above the commit summary.");
        Assert(!Descendants<TextBlock>(scroll).Any(text => text.Text == vm.DetailInitials), "Commit avatars should be removed.");
        Assert(double.IsPositiveInfinity(body.MaxHeight) && body.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "The commit body must use the description area's single scrollbar without a separate height limit.");
        Assert(Descendants<ScrollViewer>(scroll).All(child => child.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled), "The description still has a nested vertical scroller.");
        var unwanted = new[] { "Changes", "History", "创建提交" };
        Assert(!Descendants<TextBlock>(workspace).Where(text => text.IsVisible).Any(text => unwanted.Contains(text.Text)
            || text.Text.StartsWith("本次提交包含", StringComparison.Ordinal) || text.Text.StartsWith("已选", StringComparison.Ordinal)), "Redundant panel/form labels are still visible.");
        Assert(((TextBox)window.FindName("CommitSummaryBox")).ActualHeight <= 28
            && ((Button)window.FindName("CommitButton")).ActualHeight <= 30
            && ((RowDefinition)window.FindName("CommitFormRow")).ActualHeight <= 150, "The commit form is still too tall.");
        await capture(window, Path.Combine(output, reviewRepository ? "skilltrust-compact-details.png" : "compact-commit-details.png"));
        if (scroll.ScrollableHeight > 0)
        {
            scroll.ScrollToEnd();
            window.UpdateLayout();
            Assert(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "The complete commit description cannot be reached.");
            scroll.ScrollToTop();
        }
        window.Width = 1100;
        window.Height = 680;
        await capture(window, Path.Combine(output, reviewRepository ? "skilltrust-compact-window.png" : "compact-details-small-window.png"));
        var form = (Grid)window.FindName("ChangesPane");
        var submit = (Button)window.FindName("CommitButton");
        Assert(submit.TransformToAncestor(form).Transform(new Point(0, submit.ActualHeight)).Y <= form.ActualHeight,
            "The compact form overlaps the explorer in a small window.");
        window.Width = 1440;
        window.Height = 900;
        Console.WriteLine("PASS stable SkillTrust rails, username/time above Summary, one description scroller, removed labels/avatars and compact commit form.");
    }

    private static double Top(FrameworkElement element, Visual parent) => element.TransformToAncestor(parent).Transform(new Point()).Y;
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
