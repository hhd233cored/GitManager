using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GitManager.App;
using GitManager.App.Dialogs;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class RepositoryChecks
{
    public static async Task RunAsync(MainWindow window, string fixture, string output, Func<Window, string, Task> capture)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitManager.RepositoryChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        var runner = new GitRunner();
        var remote = Path.Combine(root, "remote.git");
        var firstPath = Path.Combine(root, "work-a");
        var secondPath = Path.Combine(root, "work-b");
        var relocatedPath = Path.Combine(root, "relocated-repository");
        var missingPath = Path.Combine(root, "missing-repository");
        await runner.RunAsync(root, ["clone", "--bare", "--", fixture, remote]);
        foreach (var path in new[] { firstPath, secondPath, relocatedPath })
        {
            await git.CloneAsync(remote, path);
            await runner.RunAsync(path, ["config", "user.name", "Repository Checks"]);
            await runner.RunAsync(path, ["config", "user.email", "repositories@example.test"]);
            await runner.RunAsync(path, ["config", "commit.gpgSign", "false"]);
            await runner.RunAsync(path, ["config", "core.hooksPath", ".git/no-hooks"]);
        }

        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        Assert(store.Save(new AppSettings([firstPath, firstPath + Path.DirectorySeparatorChar, missingPath], firstPath)) is null, "Unable to create legacy settings fixture.");
        using (var managed = new MainViewModel(settingsStore: store))
        {
            Assert(managed.Repositories.Count == 2, "Legacy migration must deduplicate paths and retain missing repositories for relocation.");
            await managed.OpenRepositoryAsync(firstPath);
            await managed.OpenRepositoryAsync(missingPath);
            Assert(managed.Error.Contains("找不到仓库文件夹", StringComparison.Ordinal)
                && RepositoryEntry.SamePath(managed.Repository?.Path, firstPath), "Opening a missing saved location should retain the current repository and explain relocation.");
            var activationRefresh = managed.RefreshAsync(silent: true);
            await managed.AddRepositoriesAsync([firstPath, Path.Combine(firstPath, "src"), secondPath]);
            await activationRefresh;
            Assert(RepositoryEntry.SamePath(managed.Repository?.Path, secondPath), "Adding a second repository during refresh was dropped.");
            Assert(managed.Repositories.Count == 3, "Adding aliases of the same working tree created duplicates.");
            var refresh = managed.RefreshAsync(silent: true);
            await managed.OpenRepositoryAsync(firstPath);
            await refresh;
            Assert(RepositoryEntry.SamePath(managed.Repository?.Path, firstPath), "Repository switching during passive refresh was dropped.");
            await managed.OpenRepositoryAsync(secondPath);
            var second = managed.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, secondPath));
            managed.RenameRepository(second, "工作副本 B");
            second = managed.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, secondPath));
            managed.MoveRepository(second, -2);
            Assert(managed.RepositoryName == "工作副本 B" && managed.Repositories[0].Name == "工作副本 B", "Rename/reorder did not update the current repository title.");
            var missing = managed.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, missingPath));
            await managed.RelocateRepositoryAsync(missing, relocatedPath);
            var relocated = managed.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, relocatedPath));
            managed.RenameRepository(relocated, "已重新关联的仓库");
            await managed.RelocateRepositoryAsync(relocated, firstPath);
            Assert(managed.HasError && managed.Repositories.Any(entry => RepositoryEntry.SamePath(entry.Path, relocatedPath)), "Relocation onto a duplicate path should preserve the original entry.");
            var saved = store.Load();
            Assert(saved.Repositories?.Count == 3 && saved.Repositories[0].DisplayName == "工作副本 B", "Repository names/order were not saved.");
        }
        using (var restored = new MainViewModel(settingsStore: store))
        {
            Assert(restored.Repositories.Count == 3 && restored.Repositories[0].Name == "工作副本 B", "Repository preferences did not survive restart.");
            await restored.OpenRepositoryAsync(restored.LastRepository!);
            Assert(restored.RepositoryName == "工作副本 B", "Last repository and alias did not restore.");
            await restored.RemoveRepositoryAsync(restored.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, firstPath)));
            Assert(Directory.Exists(Path.Combine(firstPath, ".git")), "Removing a list entry deleted a repository.");
            await restored.RemoveRepositoryAsync(restored.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, secondPath)));
            Assert(RepositoryEntry.SamePath(restored.Repository?.Path, relocatedPath), "Removing the active repository did not select the next entry.");
            await restored.RemoveRepositoryAsync(restored.Repositories.Single());
            Assert(!restored.HasRepository && restored.LastRepository is null && restored.Commits.Count == 0, "Removing the last repository did not return to welcome.");
            Assert(store.Load().LastRepository is null && store.Load().Repositories?.Count == 0, "Removed repositories will reappear after restart.");
            Assert(Directory.Exists(Path.Combine(secondPath, ".git")) && Directory.Exists(Path.Combine(relocatedPath, ".git")), "Repository list removal must retain local files.");
        }
        using (var empty = new MainViewModel(settingsStore: store))
            Assert(empty.Repositories.Count == 0 && empty.LastRepository is null, "An empty repository list did not persist.");
        var largeStore = new SettingsStore(Path.Combine(root, "many-repositories.json"));
        largeStore.Save(new AppSettings(Enumerable.Range(0, 15).Select(index => Path.Combine(root, $"saved-{index}")).ToList(), null));
        using (var many = new MainViewModel(settingsStore: largeStore))
            Assert(many.Repositories.Count == 15, "The repository list still has a ten-item recent-history cap.");
        Console.WriteLine("PASS repository migration, multi-add, refresh race, deduplication, aliases, reorder, relocation, remove, restart and lists over 10 entries.");

        var vm = window.ViewModel;
        window.Width = 1440;
        window.Height = 900;
        await vm.OpenRepositoryAsync(firstPath);
        Assert(vm.SyncState.Action == SyncAction.Fetch && vm.SyncTitle == "Fetch origin", "Initial sync action must be Fetch origin.");
        var syncButton = (Button)window.FindName("SyncButton");
        Assert(ReferenceEquals(syncButton.Command, vm.SyncCommand), "The toolbar button is not bound to unified sync.");
        var menuButton = (Button)window.FindName("SyncMenuButton");
        menuButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var menu = menuButton.ContextMenu;
        Assert(menu is not null && menu.Items.OfType<MenuItem>().Count() == 3, "The sync dropdown must contain Fetch, Pull and Push.");
        Assert(menu!.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, vm.PullCommand))
            && menu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, vm.PushCommand)), "Pull/Push are missing from the sync dropdown.");
        menu.IsOpen = false;
        await File.WriteAllTextAsync(Path.Combine(secondPath, "peer-update.txt"), "Update from the peer repository.\n");
        await git.StageAsync(secondPath);
        await git.CommitAsync(secondPath, "test: incoming change", "");
        await git.PushAsync(secondPath);
        await ExecuteSync(vm, () => vm.SyncState.Action == SyncAction.Pull && vm.Repository!.Behind == 1, "Fetch to Pull");
        Assert(vm.SyncBadge == "1 ↓" && vm.SyncTitle == "Pull origin", "Pull count/title did not update.");
        // Let the filesystem notification from fetch finish its debounced local refresh before capturing.
        await Task.Delay(1200);
        await Wait(() => !vm.IsBusy, "settle fetch notifications");
        await capture(window, Path.Combine(output, "sync-pull.png"));
        await ExecuteSync(vm, () => vm.SyncState.Action == SyncAction.Fetch, "Pull to Fetch");
        Assert(File.Exists(Path.Combine(firstPath, "peer-update.txt")), "Unified Pull did not update the working tree.");
        await File.WriteAllTextAsync(Path.Combine(firstPath, "local-update.txt"), "Outgoing change.\n");
        await git.StageAsync(firstPath);
        await git.CommitAsync(firstPath, "test: outgoing change", "");
        await vm.RefreshAsync();
        Assert(vm.SyncState.Action == SyncAction.Push && vm.SyncBadge == "1 ↑", "Local commits must select Push and show a count.");
        await capture(window, Path.Combine(output, "sync-push.png"));
        await ExecuteSync(vm, () => vm.SyncState.Action == SyncAction.Fetch, "Push to Fetch");
        await vm.CreateBranchAsync("feature/publish-ui");
        Assert(vm.SyncState.Action == SyncAction.Publish, "A branch without upstream must select Publish.");
        await ExecuteSync(vm, () => vm.Repository?.Upstream == "origin/feature/publish-ui", "Publish branch");
        await runner.RunAsync(firstPath, ["switch", "--detach", "HEAD"]);
        await vm.RefreshAsync();
        Assert(vm.SyncState.Action == SyncAction.Fetch && !vm.PushCommand.CanExecute(null), "Detached HEAD must not offer publishing.");
        await git.SwitchAsync(firstPath, "main");
        await vm.RefreshAsync();
        vm.IsHistory = true;
        var selected = vm.Commits.First(item => item.Subject.Contains("优化 History", StringComparison.Ordinal));
        vm.SelectedCommit = selected;
        await Wait(() => !vm.IsLoadingDetails && !vm.IsLoadingDiff && vm.Files.Count > 0, "select preview commit");
        vm.SelectedFile = vm.Files.Single(file => file.Path.EndsWith("HistoryView.xaml", StringComparison.Ordinal));
        await Wait(() => !vm.IsLoadingDiff && vm.Diff is not null, "select preview file");
        await capture(window, Path.Combine(output, "history.png"));
        Console.WriteLine("PASS unified sync dropdown and real Fetch → Pull → Fetch, Push → Fetch, Publish, and detached HEAD states.");

        await vm.AddRepositoriesAsync([secondPath]);
        vm.RenameRepository(vm.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, firstPath)), "GitManager · 工作副本 A");
        vm.RenameRepository(vm.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, secondPath)), "GitManager · 远程协作副本");
        using var dialogLifetime = new DialogLifetime(new RepositoryManagerWindow(window, vm));
        var manager = dialogLifetime.Window;
        manager.Show();
        var list = (ListBox)manager.FindName("RepositoryList");
        var search = (TextBox)manager.FindName("SearchBox");
        Assert(list.Items.Count == vm.Repositories.Count, "The management dialog is missing repositories.");
        search.Text = "远程协作";
        Assert(list.Items.Count == 1, "The repository list search did not filter aliases.");
        search.Text = "";
        list.SelectedItem = vm.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, firstPath));
        await capture(manager, Path.Combine(output, "repositories.png"));
        var initialCount = vm.Repositories.Count;
        list.SelectedItem = vm.Repositories.Single(entry => RepositoryEntry.SamePath(entry.Path, secondPath));
        ((Button)manager.FindName("RemoveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Wait(() => !vm.IsBusy && vm.Repositories.Count == initialCount - 1, "remove active repository through dialog");
        Assert(!RepositoryEntry.SamePath(vm.Repository?.Path, secondPath) && Directory.Exists(Path.Combine(secondPath, ".git")), "Dialog removal did not safely switch repositories.");
        Console.WriteLine("PASS repository manager list, search and active-entry removal through WPF controls.");
    }

    private static async Task ExecuteSync(MainViewModel vm, Func<bool> expected, string action)
    {
        await Wait(() => !vm.IsBusy && vm.SyncCommand.CanExecute(null), $"enable {action}");
        vm.SyncCommand.Execute(null);
        await Wait(() => !vm.IsBusy && expected(), action);
        Assert(!vm.HasError, vm.Error);
    }
    private static async Task Wait(Func<bool> predicate, string action)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException($"Timed out trying to {action}.");
            await Task.Delay(60);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class DialogLifetime(RepositoryManagerWindow window) : IDisposable
    {
        public RepositoryManagerWindow Window { get; } = window;
        public void Dispose() => Window.Close();
    }
}
