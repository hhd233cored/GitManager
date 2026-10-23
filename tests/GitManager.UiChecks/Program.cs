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

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: GitManager.UiChecks <repository> <output-directory>"); return 2; }
        var repository = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var app = new GitManager.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var bindings = new BindingTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(repository, persistSettings: false) { Width = 1440, Height = 900 };
                window.Show();
                var vm = window.ViewModel;
                await Until(() => !vm.IsBusy && vm.HasRepository && !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "load initial repository");
                Assert(vm.Error.Length == 0, vm.Error);
                Assert(vm.SelectedCommit is not null && vm.Files.Count > 0 && vm.Commits.Count >= 12, "History did not load real Git data.");
                await Render(window, Path.Combine(output, "history.png"));
                var commits = (ListBox)window.FindName("CommitList");
                var details = (Grid)window.FindName("DetailsPane");
                var files = (Grid)window.FindName("HistoryFilesPane");
                var diff = (Grid)window.FindName("DiffPane");
                var diffView = (DiffView)window.FindName("FileDiffView");
                var commitBounds = Bounds(commits, window);
                var detailBounds = Bounds(details, window);
                var fileBounds = Bounds(files, window);
                var diffBounds = Bounds(diff, window);
                Assert(commitBounds.Right <= detailBounds.Left + 2, "Commit list must stay on the left.");
                Assert(fileBounds.Right <= diffBounds.Left + 2, "Changed files must be left of Diff within the right pane.");
                Assert(fileBounds.Top > detailBounds.Top + 60, "Metadata must be above the changed files and Diff.");
                Assert(diffView.ActualWidth > 300 && diffView.ActualHeight > 300, "Diff viewport is too small.");
                Assert(Descendants<GraphCell>(commits).Any(), "Graph column is not rendered.");

                var allBranchCount = vm.Commits.Count;
                vm.AllBranches = false;
                await Until(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "filter current branch");
                Assert(vm.Commits.Count < allBranchCount, "Current branch filter did not narrow history.");
                vm.AllBranches = true;
                await Until(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "restore all branches");
                Assert(vm.Commits.Count == allBranchCount, "All-branches history was not restored.");

                var originalCount = vm.Commits.Count;
                vm.HistorySearch = "轨道";
                await Until(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff, "search selection");
                Assert(vm.SearchMatchCount > 0 && vm.SelectedCommit?.Subject.Contains("轨道", StringComparison.Ordinal) == true, "Search must locate a matching commit.");
                Assert(vm.Commits.Count == originalCount, "Search must preserve graph topology.");
                vm.HistorySearch = "";
                var selected = vm.Commits.First(item => item.Subject.Contains("优化 History", StringComparison.Ordinal));
                vm.SelectedCommit = selected;
                await Until(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "select History layout commit");
                Assert(vm.DetailAuthor.Length > 0 && vm.DetailSha.Length >= 40 && vm.DetailDate.Length > 0, "Metadata is missing.");
                vm.SelectedFile = vm.Files.First(file => file.Path.EndsWith("HistoryView.xaml", StringComparison.Ordinal));
                await Until(() => !vm.IsLoadingDiff && vm.Diff is not null, "load view diff");
                await Render(window, Path.Combine(output, "history.png"));
                ((ToggleButton)window.FindName("UnifiedDiffButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(!vm.IsSplitDiff, "Unified diff switch did not update.");
                await Render(window, Path.Combine(output, "history-unified.png"));
                ((ToggleButton)window.FindName("SplitDiffButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((ToggleButton)window.FindName("ChangesTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => !vm.IsLoadingDiff, "Changes navigation");
                Assert(vm.IsChanges, "Changes tab did not activate.");
                Assert(((ColumnDefinition)window.FindName("FilesColumn")).Width.Value == 0, "History file column must collapse in Changes.");
                Assert(vm.ChangeCount > 0, "Fixture should include working tree changes.");
                await Render(window, Path.Combine(output, "changes.png"));
                ((ToggleButton)window.FindName("HistoryTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "return to History");
                Assert(vm.IsHistory && vm.Files.Count > 0, "Returning to History lost file selection.");
                var tracked = vm.SelectedCommit;
                var trackedFile = vm.SelectedFile?.Path;
                await vm.RefreshAsync();
                await Until(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "refresh selection");
                Assert(vm.SelectedCommit?.Commit.Id == tracked?.Commit.Id, "Refresh lost the selected commit.");
                Assert(vm.SelectedFile is not null && vm.Diff is not null, "Refresh lost the selected diff.");
                Assert(vm.SelectedFile?.Path == trackedFile, "Refresh lost the selected file.");
                window.Width = 1100;
                window.Height = 720;
                await Render(window, Path.Combine(output, "history-compact.png"));
                Assert(((DiffView)window.FindName("FileDiffView")).ActualWidth > 250, "Compact window clips the Diff pane.");

                // Exercise the actual row template with a large source and verify UI virtualization.
                BindingOperations.ClearBinding(commits, Selector.SelectedItemProperty);
                var actualItems = commits.ItemsSource;
                var template = vm.Commits[0];
                commits.ItemsSource = Enumerable.Range(0, 12000).Select(index => template with { Commit = template.Commit with { Id = index.ToString("x40"), Subject = $"Virtualization fixture {index}" } }).ToArray();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert(Descendants<GraphCell>(commits).Count() < 100, "The commit list is creating offscreen visuals.");
                commits.ItemsSource = actualItems;
                commits.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedCommit)) { Mode = BindingMode.TwoWay });

                var scratch = Path.Combine(Path.GetTempPath(), "GitManager.UiChecks", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
                var git = new GitManager.Core.GitService();
                var runner = new GitManager.Core.GitRunner();
                await git.CloneAsync(repository, scratch);
                await runner.RunAsync(scratch, ["config", "user.name", "UI Checks"]);
                await runner.RunAsync(scratch, ["config", "user.email", "ui@example.test"]);
                await runner.RunAsync(scratch, ["config", "commit.gpgSign", "false"]);
                await runner.RunAsync(scratch, ["config", "core.hooksPath", ".git/no-hooks"]);
                await File.WriteAllTextAsync(Path.Combine(scratch, "UI 检查.txt"), "UI staging and commit check\n");
                await vm.OpenRepositoryAsync(scratch);
                vm.IsHistory = false;
                await Until(() => !vm.IsBusy && !vm.IsLoadingDiff && vm.Files.Count > 0, "load scratch working tree");
                await vm.ToggleStageAsync(vm.Files.Single(file => file.Path == "UI 检查.txt"));
                Assert(vm.StagedCount == 1 && vm.Error.Length == 0, "Staging through the ViewModel failed.");
                vm.Summary = "test: commit through WPF command";
                Assert(vm.CommitCommand.CanExecute(null), "Commit command should be enabled for a staged file and summary.");
                vm.CommitCommand.Execute(null);
                await Until(() => !vm.IsBusy && vm.ChangeCount == 0, "commit scratch change");
                Assert(vm.Summary.Length == 0 && vm.Error.Length == 0, "Commit did not clear the form or update status.");
                Assert(bindings.Errors.Count == 0, string.Join(Environment.NewLine, bindings.Errors));
                Console.WriteLine("PASS WPF startup, real data, graph rendering, pane geometry, metadata, branch filtering, search, Diff modes, tabs, selection, compact layout, 12,000-row virtualization, staging and commit command.");
                Console.WriteLine($"Screenshots: {output}");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { window?.Close(); app.Shutdown(exitCode); }
        });
        app.Run();
        return exitCode;
    }

    private static async Task Until(Func<bool> predicate, string action)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"Timed out trying to {action}.");
            await Task.Delay(75);
        }
        await Task.Delay(150);
    }
    private static async Task Render(Window window, string path)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Task.Delay(100);
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class BindingTraceListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
