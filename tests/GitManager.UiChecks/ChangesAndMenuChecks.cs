using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class ChangesAndMenuChecks
{
    public static async Task RunAsync(MainWindow window, string fixture, string output, Func<Window, string, Task> capture)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitManager.SelectionChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        var git = new GitService();
        var runner = new GitRunner();
        await git.CloneAsync(fixture, root);
        await runner.RunAsync(root, ["config", "user.name", "Selection Checks"]);
        await runner.RunAsync(root, ["config", "user.email", "selection@example.test"]);
        await runner.RunAsync(root, ["config", "commit.gpgSign", "false"]);
        await runner.RunAsync(root, ["config", "core.hooksPath", ".git/selection-hooks"]);
        await File.AppendAllTextAsync(Path.Combine(root, "README.md"), "\nKeep this staged content.\n");
        await git.StageAsync(root, new ChangedFile("README.md", null, ' ', 'M'));
        await File.AppendAllTextAsync(Path.Combine(root, "README.md"), "Keep this unstaged content too.\n");
        await File.AppendAllTextAsync(Path.Combine(root, "src/Services/GitService.cs"), "\n// Selected working-tree edit.\n");
        await File.WriteAllTextAsync(Path.Combine(root, "[notes] 新增.txt"), "A new selected file.\n");
        await File.WriteAllTextAsync(Path.Combine(root, "[skip] later.txt"), "Leave this file untracked.\n");
        File.Delete(Path.Combine(root, "src/Diff/DiffParser.cs"));
        var before = (await runner.RunAsync(root, ["diff", "--cached", "--binary", "--", "README.md"])).Output;
        var vm = window.ViewModel;
        await vm.OpenRepositoryAsync(root);
        vm.IsHistory = false;
        await Wait(() => !vm.IsBusy && !vm.IsLoadingDiff, "load selection fixture");
        window.UpdateLayout();
        Assert(vm.ChangeCount == 5 && vm.IncludedCount == 5 && vm.AllChangesIncluded == true, "Changes should default to every file included.");
        Assert(vm.StagedCount == 1 && vm.Files.All(file => file.IsIncluded), "Default inclusion should not stage files.");
        vm.SelectedFile = vm.Files.Single(file => file.Path == "README.md");
        await Wait(() => !vm.IsLoadingDiff && vm.Diff is not null, "preview full-file changes");
        Assert(vm.Diff!.Patch.Contains("+Keep this staged content.", StringComparison.Ordinal)
            && vm.Diff.Patch.Contains("+Keep this unstaged content too.", StringComparison.Ordinal), "Default preview must include both staged and unstaged changes.");
        vm.ShowStagedDiff = true;
        await Wait(() => !vm.IsLoadingDiff && vm.Diff is not null, "preview staged changes");
        Assert(vm.Diff!.Patch.Contains("+Keep this staged content.", StringComparison.Ordinal)
            && !vm.Diff.Patch.Contains("+Keep this unstaged content too.", StringComparison.Ordinal), "Staged-only preview is incorrect.");
        vm.ShowStagedDiff = false;
        Assert(before == (await runner.RunAsync(root, ["diff", "--cached", "--binary", "--", "README.md"])).Output, "Opening Changes changed the index.");
        var master = (CheckBox)window.FindName("SelectAllChangesCheckBox");
        var list = (ListBox)window.FindName("ChangesList");
        vm.Summary = "test: only included files";
        Click(master);
        Assert(vm.IncludedCount == 0 && vm.AllChangesIncluded == false && !vm.CommitCommand.CanExecute(null), "Uncheck-all must disable commit.");
        Click(master);
        Assert(vm.IncludedCount == 5 && vm.AllChangesIncluded == true, "Check-all must include all changes.");
        Click(FileCheckBox(list, "README.md"));
        Click(FileCheckBox(list, "[skip] later.txt"));
        Assert(vm.IncludedCount == 3 && vm.AllChangesIncluded is null && master.IsChecked is null, "Partial selection must show the master checkbox indeterminate.");
        // Clicking a mixed master checkbox selects everything instead of cycling to an empty selection.
        Click(master);
        Assert(vm.IncludedCount == 5 && master.IsChecked == true, "An indeterminate master click should select all.");
        Click(FileCheckBox(list, "README.md"));
        Click(FileCheckBox(list, "[skip] later.txt"));
        await vm.RefreshAsync();
        Assert(!vm.Files.Single(file => file.Path == "README.md").IsIncluded && vm.IncludedCount == 3, "Refresh lost explicit exclusions.");
        vm.IsHistory = true;
        vm.IsHistory = false;
        Assert(vm.IncludedCount == 3, "Tab switching reset inclusion.");
        await vm.OpenRepositoryAsync(fixture);
        await vm.OpenRepositoryAsync(root);
        Assert(vm.IncludedCount == 3 && vm.Files.Count(file => !file.IsIncluded) == 2, "Repository switching reset explicit exclusions.");
        await File.WriteAllTextAsync(Path.Combine(root, "arrived.txt"), "A newly discovered change.\n");
        await vm.RefreshAsync();
        Assert(vm.IncludedCount == 4 && vm.Files.Single(file => file.Path == "arrived.txt").IsIncluded, "Newly discovered files should default to included.");
        Assert(before == (await runner.RunAsync(root, ["diff", "--cached", "--binary", "--", "README.md"])).Output, "Checkbox interaction changed excluded staging.");
        window.UpdateLayout();
        var icons = Descendants<FileStatusIcon>(list).ToArray();
        Assert(icons.Any(icon => icon.Status == 'A') && icons.Any(icon => icon.Status == 'M') && icons.Any(icon => icon.Status == 'D'), "File status icons are missing.");
        vm.Summary = "test: only included files";
        vm.Description = "Commit the selected files; keep README staging and the skipped new file.";
        await capture(window, Path.Combine(output, "changes-selection.png"));

        var hookDirectory = Path.Combine(root, ".git/selection-hooks");
        Directory.CreateDirectory(hookDirectory);
        var hookPath = Path.Combine(hookDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, "#!/bin/sh\nexit 1\n");
        await Wait(() => !vm.IsBusy && vm.CommitCommand.CanExecute(null), "enable selected commit");
        vm.CommitCommand.Execute(null);
        await Wait(() => !vm.IsBusy && vm.HasError, "report failed hook");
        Assert(vm.Summary == "test: only included files" && vm.IncludedCount == 4, "A failed commit must retain the message and inclusion state.");
        Assert(before == (await runner.RunAsync(root, ["diff", "--cached", "--binary", "--", "README.md"])).Output, "A failed commit modified excluded staging.");
        File.Delete(hookPath);
        await Wait(() => !vm.IsBusy && vm.CommitCommand.CanExecute(null), "retry selected commit");
        vm.CommitCommand.Execute(null);
        await Wait(() => !vm.IsBusy && vm.Summary.Length == 0 && vm.ChangeCount == 2, "commit the included files");
        Assert(!vm.HasError && vm.IncludedCount == 0 && vm.AllChangesIncluded == false, "Excluded remaining files should stay unchecked after commit.");
        var head = (await git.GetHistoryAsync(root, false, 1)).Single();
        var changed = (await git.GetCommitFilesAsync(root, head)).Select(file => file.Path).ToHashSet();
        Assert(changed.SetEquals(["src/Services/GitService.cs", "src/Diff/DiffParser.cs", "[notes] 新增.txt", "arrived.txt"]), "The commit included the wrong files.");
        Assert(before == (await runner.RunAsync(root, ["diff", "--cached", "--binary", "--", "README.md"])).Output, "Commit changed excluded staged content.");
        Assert(vm.Files.Single(file => file.Path == "README.md").File.StageState is null, "Excluded staged and unstaged versions were not preserved.");
        Assert(vm.Files.Single(file => file.Path == "[skip] later.txt").File.IsUntracked, "An excluded untracked file was staged.");
        Console.WriteLine("PASS Changes default selection, actual checkbox clicks, mixed master state, refresh/tab/repository retention, new files, hook failure/retry and excluded-index preservation.");

    }

    public static async Task CheckMenusAsync(MainWindow window, string output)
    {
        var menu = (MenuItem)window.FindName("FileMenuItem");
        var popup = (Popup)menu.Template.FindName("PART_Popup", menu);
        // Unattended checks have no active mouse input provider for MenuBase's capture.
        // Open the real popup directly, then exercise the menu item's actual hover handler.
        popup.IsOpen = true;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Task.Delay(150);
        var frame = (Border)popup.Child;
        Assert(popup.IsOpen && frame.ActualWidth > 0, "Menu template failed to render.");
        Assert(((SolidColorBrush)frame.Background).Color == Colors.White, "The menu popup background must be pure white.");
        var first = menu.Items.OfType<MenuItem>().First();
        first.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        first.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var chrome = (Border)first.Template.FindName("MenuChrome", first);
        CaptureElement(frame, Path.Combine(output, "menu-white-blue.png"));
        Assert(first.IsHighlighted && ((SolidColorBrush)chrome.Background).Color == Color.FromRgb(9, 105, 218),
            $"The highlighted menu item should be blue (highlighted={first.IsHighlighted}, focused={first.IsKeyboardFocused}, background={chrome.Background}).");
        popup.IsOpen = false;
        popup.ClearValue(Popup.IsOpenProperty);
        Console.WriteLine("PASS white menu popup template and blue hovered item.");
    }

    private static CheckBox FileCheckBox(ListBox list, string path) => Descendants<CheckBox>(list).Single(box => box.DataContext is FileItem item && item.Path == path);
    private static void Click(ToggleButton button)
    {
        Assert(button.IsEnabled, "Checkbox is unexpectedly disabled.");
        typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
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
    private static async Task Wait(Func<bool> condition, string action)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"Timed out trying to {action}.");
            await Task.Delay(60);
        }
    }
    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(element.RenderSize));
        bitmap.Render(backdrop);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
