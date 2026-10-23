using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class AppearanceChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<Window, string, Task> capture)
    {
        var vm = window.ViewModel;
        vm.ClearHistoryPath();
        vm.IsHistory = true;
        window.Width = 1440;
        window.Height = 900;
        await Wait(() => !vm.IsBusy && !vm.IsFilteringHistory && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "load compact history");
        foreach (var commit in vm.Commits) commit.Details.IsExpanded = false;
        window.UpdateLayout();
        var list = (ListBox)window.FindName("CommitList");
        var rows = Descendants<ListBoxItem>(list).ToArray();
        Assert(rows.Length > 0 && rows.All(row => row.ActualHeight is >= 20 and <= 24), "History rows must be compact single lines.");
        Assert(ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled, "History must truncate rather than scroll sideways.");
        foreach (var row in rows)
        {
            var graph = Descendants<GraphCell>(row).Single(cell => !cell.IsContinuation);
            var text = Descendants<TextBlock>(row).Single(block => block.Name == "CommitSummaryText");
            var graphRight = graph.TransformToAncestor(row).Transform(new Point(graph.ActualWidth, 0)).X;
            var textLeft = text.TransformToAncestor(row).Transform(new Point()).X;
            Assert(textLeft >= graphRight && textLeft - graphRight <= 20, "Commit text and its expander should closely follow the occupied graph columns.");
            Assert(text.TextTrimming == TextTrimming.CharacterEllipsis && text.TextWrapping == TextWrapping.NoWrap, "Commit messages must truncate on one line.");
        }
        Assert(vm.Commits.Select(item => item.GraphWidth).Distinct().Count() > 1, "Graph reservation should follow each row's lanes instead of the widest row.");
        Assert(!Descendants<TextBlock>(list).Any(block => block.Text.Contains("v0.1.0", StringComparison.Ordinal)), "Tag badges should not be shown in history rows.");
        await capture(window, Path.Combine(output, "history-dense.png"));

        var tag = vm.Commits.First(item => item.Commit.Decorations.Contains("tag:", StringComparison.Ordinal));
        list.ScrollIntoView(tag);
        window.UpdateLayout();
        var tagRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(tag);
        var tooltipOwner = Descendants<FrameworkElement>(tagRow).Single(element => element.ToolTip is ToolTip);
        var tooltip = (ToolTip)tooltipOwner.ToolTip;
        tooltip.PlacementTarget = tooltipOwner;
        tooltip.IsOpen = true;
        await Wait(() => tag.Details.HasStats && !tag.Details.IsLoadingStats, "load tooltip change statistics");
        await window.Dispatcher.InvokeAsync(tooltip.UpdateLayout, DispatcherPriority.Render);
        await Task.Delay(80);
        var details = Descendants<TextBlock>(tooltip).Select(block => block.Text).ToArray();
        Assert(details.Contains(tag.Commit.Id) && details.Contains(tag.Commit.Subject) && details.Contains(tag.AuthorDetails)
            && details.Contains(tag.Timestamp) && details.Any(text => text.Contains("tag:", StringComparison.Ordinal)), "Hover details must include the full message, author, timestamp, SHA and references.");
        Assert(!details.Any(text => text.Contains(tag.Commit.Email, StringComparison.Ordinal)), "Author emails should not appear in commit details.");
        Assert(details.Contains($"+{tag.Details.Stats!.Additions}") && details.Contains($"−{tag.Details.Stats.Deletions}"), "Hover details must show added and removed lines.");
        var shaText = Descendants<TextBlock>(tooltip).Single(block => block.Text == tag.Commit.Id);
        var statsText = Descendants<TextBlock>(tooltip).First(block => block.Text == $"+{tag.Details.Stats.Additions}");
        Assert(statsText.TranslatePoint(new Point(), tooltip).Y < shaText.TranslatePoint(new Point(), tooltip).Y, "Change statistics must appear above the SHA.");
        CaptureElement(tooltip, Path.Combine(output, "history-hover-details.png"));
        tooltip.IsOpen = false;

        var originalSource = list.ItemsSource;
        var originalSourceBinding = BindingOperations.GetBindingBase(list, ItemsControl.ItemsSourceProperty);
        BindingOperations.ClearBinding(list, Selector.SelectedItemProperty);
        var longItem = tag with { Commit = tag.Commit with { Subject = string.Concat(Enumerable.Repeat("完整提交说明与很长的路径内容 ", 30)), Author = "A long author name" } };
        list.ItemsSource = new[] { longItem };
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var listScroll = Descendants<ScrollViewer>(list).First();
        Assert(listScroll.ScrollableWidth == 0, "A very long commit message caused horizontal overflow.");
        await capture(window, Path.Combine(output, "history-truncated.png"));
        list.ItemsSource = Enumerable.Range(0, 600).Select(index => tag with { Commit = tag.Commit with { Id = index.ToString("x40"), Subject = $"Scroll check {index}" } }).ToArray();
        window.UpdateLayout();
        CheckNarrowScrollBars(list);
        Assert(Descendants<GraphCell>(list).Count() < 100, "Compact history lost virtualization.");
        if (originalSourceBinding is not null) list.SetBinding(ItemsControl.ItemsSourceProperty, originalSourceBinding); else list.ItemsSource = originalSource;
        list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedCommit)) { Mode = BindingMode.TwoWay });
        var tree = (TreeView)window.FindName("RepositoryTreeView");
        var treeSource = tree.ItemsSource;
        var treeBinding = BindingOperations.GetBindingBase(tree, ItemsControl.ItemsSourceProperty);
        var root = new RepositoryTreeItem(new string('W', 200), "", true) { IsExpanded = true };
        for (var index = 0; index < 100; index++) root.Children.Add(new RepositoryTreeItem($"file-{index}.cs", $"file-{index}.cs", false));
        tree.ItemsSource = new[] { root };
        window.UpdateLayout();
        CheckNarrowScrollBars(tree);
        Assert(Descendants<ScrollBar>(tree).Any(bar => bar.IsVisible && bar.Orientation == Orientation.Horizontal), "Explorer horizontal scrollbar was not exercised.");
        if (treeBinding is not null) tree.SetBinding(ItemsControl.ItemsSourceProperty, treeBinding); else tree.ItemsSource = treeSource;
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "restore history selection");
        Console.WriteLine("PASS dense history, per-row graph spacing, truncation, hidden tags and complete hover details.");

        var view = new DiffView();
        var scroll = new ScrollViewer { Content = view, Background = Brushes.White, CanContentScroll = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = new Window { Owner = window, Content = scroll, Width = 680, Height = 400, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Title = "Diff wrapping checks" };
        try
        {
            host.Show();
            var oldLong = "public string Message = \"" + string.Concat(Enumerable.Repeat("完整的旧版内容 OldValue_12345，", 12)) + "\";";
            var newLong = "return \"" + string.Concat(Enumerable.Repeat("新的内容与 Unicode 😀 NewValue_98765，", 11)) + "\";";
            var patch = $"diff --git a/code.cs b/code.cs\n--- a/code.cs\n+++ b/code.cs\n@@ -1,4 +1,4 @@\n-{oldLong}\n+short replacement\n unchanged middle\n-old short\n+{newLong}\n after wrapped rows\n";
            var document = DiffParser.Parse(patch);
            view.Document = document;
            await capture(host, Path.Combine(output, "diff-wrapped-split.png"));
            Assert(view.ExtentHeight > document.Pairs.Count * 23 + 100, "Long Diff lines did not grow their rows.");
            Assert(view.RowAtOffset(23 * 4) == 1, "Continuation text should hit the same original row.");
            Assert(view.SelectionText(1, 1) == "short replacement", "Split selection should copy original source lines.");
            Assert(view.SelectionText(2, 4).Contains(newLong, StringComparison.Ordinal), "Wrapped selection lost source text.");
            Assert(scroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed && Math.Abs(view.ExtentWidth - view.ViewportWidth) < .1, "Diff must not expose a horizontal scrollbar.");
            view.SetHorizontalOffset(10000);
            Assert(view.HorizontalOffset == 0, "Diff wrapping must disable horizontal offset.");
            var narrowHeight = view.ExtentHeight;
            host.Width = 1000;
            host.UpdateLayout();
            Assert(view.ExtentHeight < narrowHeight, "Widening the panel did not reflow wrapped text.");
            host.Width = 680;
            view.IsSplit = false;
            await capture(host, Path.Combine(output, "diff-wrapped-unified.png"));
            Assert(view.ExtentHeight > document.Lines.Count * 23 && view.SelectionText(1, 1) == oldLong, "Unified wrapping or copying changed the source line.");

            view.IsSplit = true;
            DiffDocument LongDocument(int length) => DiffParser.Parse("@@ -1 +1 @@\n-" + new string('X', length) + "\n+short\n final row\n");
            view.Document = LongDocument(5000);
            host.UpdateLayout();
            var before = view.ExtentHeight;
            view.Document = LongDocument(6000);
            host.UpdateLayout();
            Assert(view.ExtentHeight > before * 1.17, "Unbroken lines beyond 5,000 characters were truncated instead of wrapped.");
            Assert(view.RowAtOffset(view.ExtentHeight - 1) == view.Document.Pairs.Count - 1, "The final row is unreachable after wrapping.");
            view.SetVerticalOffset(double.PositiveInfinity);
            Assert(Math.Abs(view.VerticalOffset + view.ViewportHeight - view.ExtentHeight) < 1, "Wrapped scrolling did not reach the bottom.");
            await capture(host, Path.Combine(output, "diff-wrapped-long-bottom.png"));
            var bar = Descendants<ScrollBar>(scroll).Single(item => item.Orientation == Orientation.Vertical);
            Assert(bar.ActualWidth <= 6.1, "Vertical scrollbars must be extremely narrow.");
            var thumb = Descendants<Thumb>(bar).Single();
            var capsule = (Border)thumb.Template.FindName("Capsule", thumb);
            Assert(capsule.ActualWidth <= 4 && capsule.CornerRadius.TopLeft >= 2, "Scrollbar thumb should be a thin capsule.");
            view.SetVerticalOffset(0);
            ((RepeatButton)bar.Template.FindName("IncreasePage", bar)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ScrollBar.PageDownCommand.Execute(null, bar);
            await host.Dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.Render);
            Assert(view.VerticalOffset > 0, "Capsule scrollbar track paging no longer scrolls.");
            view.SetVerticalOffset(0);
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(0, 30) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 30, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await host.Dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.Render);
            Assert(view.VerticalOffset > 0, "Capsule scrollbar thumb dragging no longer scrolls.");

            var timer = Stopwatch.StartNew();
            view.Document = DiffParser.Parse("@@ -1,12000 +1,12000 @@\n" + string.Join('\n', Enumerable.Range(0, 12000).Select(index => " public string Row_" + index + " = \"a wrapped source line to keep\";")) + "\n");
            host.UpdateLayout();
            await capture(host, Path.Combine(output, "diff-wrapped-large.png"));
            Assert(timer.Elapsed < TimeSpan.FromSeconds(15), "Large wrapped Diff layout is unexpectedly slow.");
            view.SetVerticalOffset(view.ExtentHeight / 2);
            await host.Dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.Render);
            Assert(view.CachedTextCount < 100 && VisualTreeHelper.GetChildrenCount(view) == 0, "Wrapped Diff should retain only visible text layouts.");
            Console.WriteLine($"PASS split/unified wrapping, Unicode, unbroken long lines, original selection text, viewport reflow and 12,000-row drawing ({timer.Elapsed.TotalSeconds:F2}s).");
            Console.WriteLine("PASS capsule scrollbar dimensions, paging and thumb dragging.");
        }
        finally { host.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void CheckNarrowScrollBars(DependencyObject parent)
    {
        var bars = Descendants<ScrollBar>(parent).Where(bar => bar.IsVisible).ToArray();
        Assert(bars.Length > 0, "The scrollbar check needs overflowing content.");
        foreach (var bar in bars)
        {
            var thumb = Descendants<Thumb>(bar).Single();
            var capsule = thumb.Template.FindName("Capsule", thumb) as Border ?? throw new Exception("The control did not inherit capsule scrollbars.");
            Assert(capsule.CornerRadius.TopLeft >= 2, "The scrollbar does not have rounded ends.");
            Assert(bar.Orientation == Orientation.Vertical ? bar.ActualWidth <= 6.1 && capsule.ActualWidth <= 4
                : bar.ActualHeight <= 6.1 && capsule.ActualHeight <= 4, "The scrollbar is too wide.");
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
    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
