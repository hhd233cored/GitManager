using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.Dialogs;
using GitManager.Core;

internal static class OperationsChecks
{
    public static async Task RunAsync(MainWindow window, string fixture, string output, Func<Window, string, Task> capture)
    {
        var git = new GitService();
        var runner = new GitRunner();
        var path = Path.Combine(Path.GetTempPath(), "GitManager.OperationsUiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        await runner.RunAsync(path, ["init", "-b", "main"]);
        foreach (var pair in new[] { ("user.name", "UI Operations"), ("user.email", "ui@example.test"),
            ("core.autocrlf", "false"), ("core.hooksPath", ".git/no-hooks"), ("commit.gpgSign", "false") })
            await runner.RunAsync(path, ["config", pair.Item1, pair.Item2]);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(path, "b.txt"), "base b\n");
        await git.StageAsync(path); await git.CommitAsync(path, "root", "");
        var root = await git.ResolveCommitAsync(path, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "main\n");
        await git.StageAsync(path); await git.CommitAsync(path, "main change", "");
        await runner.RunAsync(path, ["switch", "-c", "feature", root]);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "feature\n");
        await git.StageAsync(path); await git.CommitAsync(path, "feature change", "");
        var feature = await git.ResolveCommitAsync(path, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(path, "b.txt"), "staged b\n");
        await git.StageAsync(path, new ChangedFile("b.txt", null, ' ', 'M'));
        await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new saved file\n");

        var vm = window.ViewModel;
        window.Width = 1440; window.Height = 900;
        await vm.OpenRepositoryAsync(path);
        await Ready(window);
        vm.IsHistory = false;
        await vm.SaveStashAsync("界面检查 · 保存改动", true, false);
        await Ready(window);
        Assert(vm.Error.Length == 0 && vm.ChangeCount == 0 && vm.Stashes.Count == 1, "Stash UI did not refresh after saving changes.");
        var entry = vm.Stashes.Single();
        var stashes = new StashWindow(window, vm);
        try
        {
            stashes.Show();
            ((ListBox)stashes.FindName("StashesList")).SelectedItem = entry;
            await Wait(() => vm.IsStash && !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Diff is not null, "preview Stash");
            Assert(vm.Files.Count == 2 && vm.FileCount == 2 && !vm.HasCommit && vm.DetailSubject.Contains(entry.Message, StringComparison.Ordinal), "Stash header/files are mixed with the selected commit.");
            vm.SelectedFile = vm.Files.Single(file => file.Path == "new.txt");
            await Ready(window);
            Assert(vm.Diff!.Patch.Contains("+new saved file", StringComparison.Ordinal) && ((DiffView)window.FindName("FileDiffView")).IsSplit, "Shared Diff did not show the selected Stash file.");
            await capture(stashes, Path.Combine(output, "stash-list.png"));
            await capture(window, Path.Combine(output, "stash-shared-diff.png"));
            vm.SelectedCommit = vm.SelectedCommit;
            await Ready(window);
            Assert(!vm.IsStash && vm.HasCommit && vm.DiffSourceLabel == "提交变更", "Selecting the same commit should leave Stash preview.");
        }
        finally { stashes.Close(); }
        await vm.ApplyStashAsync(entry, false, true);
        await Ready(window);
        Assert(vm.Error.Length == 0 && vm.ChangeCount == 2 && vm.StagedCount == 1, "Applying Stash did not restore the original index and working files.");
        var plan = await vm.PreviewOperationAsync("main", "UI rebase");
        Assert(plan is not null, vm.Error);
        await vm.RebaseAsync(plan!);
        await Ready(window);
        Assert(vm.CurrentOperation.Kind == RepositoryOperationKind.Rebase && vm.IsChanges && !vm.CanStartOperation,
            "Rebase conflict did not switch the shared workspace to conflict files.");
        var banner = (Border)window.FindName("OperationBanner");
        var next = (Button)window.FindName("ContinueOperationButton");
        Assert(banner.IsVisible && !next.IsEnabled && !vm.CommitCommand.CanExecute(null), "Conflicted operations must disable Continue and ordinary commits.");
        vm.SelectedWorkingFile = vm.WorkingFiles.Single(file => file.File.IsConflicted);
        await Ready(window);
        Assert(vm.CanResolveSelectedFile && vm.Diff!.Patch.Contains("<<<<<<<", StringComparison.Ordinal), "Conflict preview is unavailable.");
        await capture(window, Path.Combine(output, "rebase-conflict.png"));
        var conflict = await vm.ReadConflictAsync();
        Assert(conflict is not null, vm.Error);
        var editor = new ConflictWindow(window, conflict!, true);
        try
        {
            editor.Show();
            await capture(editor, Path.Combine(output, "conflict-editor.png"));
            Assert(Descendants<TextBox>(editor).Count() == 3 && Descendants<TextBox>(editor).Single(box => box.Name == "ConflictResultBox").AcceptsReturn,
                "Conflict editor should show both sides and an editable result.");
        }
        finally { editor.Close(); }
        await vm.ResolveConflictAsync("a.txt", ConflictResolution.Edited, "resolved\r\n");
        await Ready(window);
        Assert(next.IsEnabled, "Continue did not become available after resolving the final file.");
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(() => !vm.IsBusy && !vm.HasActiveOperation, "continue rebase through the UI");
        await Ready(window);
        Assert(vm.Error.Length == 0 && vm.ChangeCount == 2 && vm.StagedCount == 1 && !banner.IsVisible, "Rebase completion lost the saved working state or left its banner visible.");
        Assert(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")) == "resolved\n", "Conflict editing changed an LF file to CRLF.");

        var reset = await vm.PreviewOperationAsync(feature, "UI reset");
        await vm.ResetAsync(reset!, ResetMode.Hard);
        await Ready(window);
        var point = vm.RecoveryPoints.First();
        Assert(vm.Error.Length == 0 && vm.ChangeCount == 0, "Hard reset did not refresh the workspace.");
        var recovery = new RecoveryWindow(window, vm);
        try
        {
            recovery.Show();
            ((ListBox)recovery.FindName("RecoveryList")).SelectedItem = point;
            await capture(recovery, Path.Combine(output, "recovery-list.png"));
        }
        finally { recovery.Close(); }
        await vm.RestoreRecoveryAsync(point);
        await Ready(window);
        Assert(vm.Error.Length == 0 && vm.ChangeCount == 2 && vm.StagedCount == 1, "Recovery UI did not restore original dirty files/index.");

        var confirmation = new OperationDialog(window, "同步预览", "当前分支与上游比较", "本地独有提交、远程独有提交和文件差异", [
            new ActionChoice(SynchronizationMode.Normal, "常规同步", "快进拉取或普通推送"),
            new ActionChoice(SynchronizationMode.Remote, "采用远程版本", "原版本先保存为恢复点", true)]);
        try
        {
            confirmation.Show(); confirmation.UpdateLayout();
            var choices = Descendants<ComboBox>(confirmation).Single();
            var acknowledge = Descendants<CheckBox>(confirmation).Single();
            var execute = Descendants<Button>(confirmation).Single(button => button.Name == "ExecuteOperationButton");
            choices.SelectedIndex = 1;
            Assert(!execute.IsEnabled && acknowledge.IsVisible, "Overwriting a version should require explicit confirmation in the preview.");
            acknowledge.IsChecked = true;
            Assert(execute.IsEnabled, "Acknowledging the concrete preview did not enable execution.");
            await capture(confirmation, Path.Combine(output, "operation-confirmation.png"));
            choices.SelectedIndex = 0;
            Assert(execute.IsEnabled && !acknowledge.IsVisible, "Ordinary synchronization retained the overwrite acknowledgement.");
        }
        finally { confirmation.Close(); }
        await vm.OpenRepositoryAsync(fixture);
        await Ready(window);
        Console.WriteLine("PASS Stash/shared Diff, conflict editor, operation banner and Continue, backup recovery, LF preservation and overwrite confirmation UI.");
    }
    private static async Task Ready(MainWindow window)
    {
        var vm = window.ViewModel;
        await Wait(() => !vm.IsBusy && !vm.IsLoadingDetails && !vm.IsLoadingDiff, "settle operation UI");
        window.UpdateLayout();
    }
    private static async Task Wait(Func<bool> condition, string action)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("Timed out trying to " + action);
            await Task.Delay(70);
        }
        await Task.Delay(100);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
