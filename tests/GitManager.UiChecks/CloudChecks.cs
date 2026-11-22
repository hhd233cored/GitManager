using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.Dialogs;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using GitManager.Contracts;

internal static class CloudChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<Window, string, Task> render)
    {
        var server = Environment.GetEnvironmentVariable("GITMANAGER_SERVER_URL") ?? "http://127.0.0.1:5187";
        using var http = new HttpClient();
        using var unauthorized = await http.GetAsync(server + "/api/backups");
        Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Backups must require authentication.");

        var suffix = Guid.NewGuid().ToString("N");
        var email = $"desktop-{suffix}@example.test";
        const string password = "Test-only!42";
        var firstSettings = new AssistantSettingsStore(Path.Combine(output, $"first-options-{suffix}.json"));
        var secondSettings = new AssistantSettingsStore(Path.Combine(output, $"second-options-{suffix}.json"));
        var firstStore = new AssistantConversationStore(Path.Combine(output, $"first-chats-{suffix}.db"));
        var secondStore = new AssistantConversationStore(Path.Combine(output, $"second-chats-{suffix}.db"));
        using var first = new AssistantViewModel(window.ViewModel, true, firstSettings, null, firstStore);
        using var second = new AssistantViewModel(window.ViewModel, true, secondSettings, null, secondStore);
        var context = window.ViewModel.RepositoryTools.Context!;
        var id = Guid.NewGuid().ToString("N");
        firstStore.Append(id, context, "备份测试", "提问一", "**回答一**");
        first.SaveOptions(first.Options with { Model = "cloud-model", UseStreaming = false, MaxOutputTokens = 4000 }, null, false);
        second.SaveOptions(second.Options with { BaseUrl = "http://127.0.0.1:1234/v1", IncludeDiff = false, ExcludedPaths = "private" }, null, false);

        var accountWindow = new CloudAccountWindow(window, first);
        accountWindow.Show();
        try
        {
            ((TextBox)accountWindow.FindName("ServerAddress")).Text = server;
            ((TextBox)accountWindow.FindName("AccountEmail")).Text = email;
            ((PasswordBox)accountWindow.FindName("AccountPassword")).Password = password;
            Click(accountWindow, "RegisterButton");
            await UntilAsync(() => ((Button)accountWindow.FindName("LogoutButton")).IsEnabled, "register and sign in via WPF");
            Assert(first.CloudAccount.Email == email, "The registered account did not sign in.");
            Click(accountWindow, "UploadPreferencesButton");
            await UntilAsync(() => ((TextBlock)accountWindow.FindName("Status")).Text.StartsWith("已上传", StringComparison.Ordinal), "upload preferences from WPF");
        }
        finally { accountWindow.Close(); }

        var pane = (AssistantPane)window.FindName("AssistantPanel");
        window.Assistant.IsOpen = true;
        pane.DataContext = first;
        first.IsOpen = true;
        try
        {
            var title = (Button)pane.FindName("AssistantHistoryButton");
            var list = (ListBox)pane.FindName("ConversationList");
            title.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            await UntilAsync(() => list.Items.Count == 1, "open the local conversation menu");
            window.UpdateLayout();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var upload = Descendants(row).OfType<Button>().Single(button => Equals(button.Content, "备份"));
            upload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => ((TextBlock)pane.FindName("ConversationMenuStatus")).Text == "已备份到账号。", "upload from the local conversation menu");
        }
        finally
        {
            pane.DataContext = window.Assistant;
            window.Assistant.IsOpen = false;
        }

        firstStore.Append(id, context, "备份测试", "提问二", "回答二");
        await Task.WhenAll(first.UploadConversationAsync(id), first.UploadConversationAsync(id));
        Assert((await first.CloudAccount.ListBackupsAsync(default)).Single().ConversationId == id, "Repeated uploads duplicated the backup.");
        var backup = await first.CloudAccount.DownloadAsync(id, default);
        Assert(backup.Messages.Length == 4 && backup.Messages[^1] == new BackupMessage("assistant", "回答二"), "Replacing a backup did not keep message order or latest contents.");

        await second.CloudAccount.SignInAsync(server, email, password, register: false, default);
        var restoreWindow = new CloudAccountWindow(window, second);
        restoreWindow.Show();
        try
        {
            var list = (ListBox)restoreWindow.FindName("BackupList");
            await UntilAsync(() => list.IsEnabled && list.Items.Count == 1, "load backups in another client");
            await render(restoreWindow, Path.Combine(output, "cloud-account.png"));
            list.SelectedIndex = 0;
            Click(restoreWindow, "RestoreButton");
            await UntilAsync(() => ((TextBlock)restoreWindow.FindName("Status")).Text.StartsWith("已恢复", StringComparison.Ordinal), "restore from WPF");
            Assert(secondStore.List().Single().RepositoryPath.Length == 0 && secondStore.Read(id).Last().Text == "回答二",
                "Restored transcript lost its messages or inherited a device-specific repository path.");
            Click(restoreWindow, "ApplyPreferencesButton");
            await UntilAsync(() => ((TextBlock)restoreWindow.FindName("Status")).Text == "已应用云端偏好。", "apply preferences in another client");
        }
        finally { restoreWindow.Close(); }

        var restoredOptions = secondSettings.Load();
        Assert(restoredOptions.Model == "cloud-model" && !restoredOptions.UseStreaming && restoredOptions.MaxOutputTokens == 4000
            && restoredOptions.BaseUrl == "http://127.0.0.1:1234/v1" && !restoredOptions.IncludeDiff && restoredOptions.ExcludedPaths == "private",
            "Applying cloud preferences changed device-specific settings or did not persist.");
        Assert(await second.RestoreConversationAsync(backup, false, default) == ConversationRestore.AlreadyPresent, "Restoring an identical backup created a duplicate.");
        secondStore.Append(id, context, "备份测试", "本机新增", "本机回答");
        Assert(await second.RestoreConversationAsync(backup, false, default) == ConversationRestore.Conflict && secondStore.Read(id).Count == 6,
            "A different local conversation was replaced without confirmation.");
        Assert(await second.RestoreConversationAsync(backup, true, default) == ConversationRestore.Restored && secondStore.Read(id).Count == 4,
            "Confirming replacement did not restore the backup atomically.");

        var other = new CloudAccountClient();
        await other.SignInAsync(server, $"other-{suffix}@example.test", password, register: true, default);
        Assert((await other.ListBackupsAsync(default)).Length == 0 && await other.DownloadPreferencesAsync(default) is null,
            "A second account could see another account's backups or preferences.");
        await ExpectMissingAsync(() => other.DownloadAsync(id, default));
        await ExpectMissingAsync(() => other.DeleteAsync(id, default));
        await other.UploadAsync(id, new BackupContent("另一个账号", "repo", "main", [new("user", "独立记录")]), default);
        Assert((await first.CloudAccount.DownloadAsync(id, default)).Summary.Title == "备份测试", "Identical client IDs collided across accounts.");
        await first.CloudAccount.DeleteAsync(id, default);
        Assert((await first.CloudAccount.ListBackupsAsync(default)).Length == 0 && firstStore.Read(id).Count == 4
            && (await other.ListBackupsAsync(default)).Length == 1, "Cloud deletion removed local history or another account's backup.");
        first.CloudAccount.SignOut();
        Assert(first.CloudAccount.Email is null, "Signing out did not clear the client session.");
        Console.WriteLine("PASS WPF account, SQLite backend, backup upload/restore/conflict, preference scope, and two-account isolation");
    }

    private static void Click(FrameworkElement scope, string name) => ((Button)scope.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task UntilAsync(Func<bool> condition, string action)
    {
        for (var attempt = 0; !condition() && attempt < 120; attempt++) await Task.Delay(40);
        Assert(condition(), $"Timed out waiting to {action}.");
    }

    private static async Task ExpectMissingAsync(Func<Task> action)
    {
        try { await action(); }
        catch (CloudAccountException ex) when (ex.Message.Contains("找不到", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Another user's backup did not return Not Found.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Assert(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
