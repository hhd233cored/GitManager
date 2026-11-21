using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.Agent;
using GitManager.App;
using GitManager.App.Dialogs;
using GitManager.App.Controls;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using Microsoft.Extensions.AI;

internal static class AssistantChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<MainWindow, string, Task> render)
    {
        var vm = window.ViewModel;
        var assistant = window.Assistant;
        var path = vm.Repository!.Path;
        var originalWidth = window.Width;
        var originalSummary = vm.Summary;
        var originalDescription = vm.Description;
        var head = vm.Commits[0].Commit.Id;
        Assert(!assistant.IsOpen, "Assistant must be collapsed initially.");
        ((Button)window.FindName("ToggleAssistantButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(150); window.UpdateLayout();
        var panel = (FrameworkElement)window.FindName("AssistantPanel");
        Assert(panel.IsVisible && panel.ActualWidth >= 300, "Assistant drawer did not open.");
        await render(window, Path.Combine(output, "assistant-open.png"));
        window.Width = 1080; await Task.Delay(100); window.UpdateLayout();
        var bounds = panel.TransformToAncestor(window).TransformBounds(new Rect(panel.RenderSize));
        Assert(bounds.Right <= window.ActualWidth + 1, "Assistant clips outside narrow window.");
        await render(window, Path.Combine(output, "assistant-narrow.png"));
        window.Width = originalWidth;

        assistant.SaveOptions(new("http://127.0.0.1:18791/v1", "ui-check-model", UseStreaming: false), null, false);
        assistant.ClientFactory = (_, _) => new DraftClient(vm.GetIncludedFilePaths().First());
        await assistant.RunAsync(AssistantTask.CommitDraft);
        await Task.Delay(100);
        Assert(assistant.HasDraft && assistant.Draft!.Summary == "feat: 生成界面测试草稿", assistant.Error);
        Assert(vm.Summary == originalSummary && vm.Description == originalDescription, "Draft changed the commit form without user action.");
        await render(window, Path.Combine(output, "assistant-draft.png"));
        await assistant.ApplyDraftAsync();
        Assert(vm.Summary == "feat: 生成界面测试草稿" && vm.Description == "只处理所选文件。", "Draft did not fill the form.");
        Assert(vm.Commits[0].Commit.Id == head && vm.ChangeCount > 0, "Draft created a Git commit.");
        assistant.ClientFactory = (_, _) => new WaitingClient();
        assistant.Input = "解释一下这些改动";
        Assert(((AssistantPane)panel).HandleShortcut(Key.Enter, ModifierKeys.Control), "Assistant did not consume Ctrl+Enter.");
        Assert(assistant.IsBusy && vm.Commits[0].Commit.Id == head, "Assistant shortcut reached the Git commit action.");
        Assert(((AssistantPane)panel).HandleShortcut(Key.Escape, ModifierKeys.None), "Assistant did not consume Escape.");
        for (var attempt = 0; assistant.IsBusy && attempt < 100; attempt++) await Task.Delay(25);
        Assert(!assistant.IsBusy, "Assistant shortcut did not cancel.");
        assistant.Input = "";
        Assert(((AssistantPane)panel).HandleShortcut(Key.Enter, ModifierKeys.Control), "Empty assistant input must still consume the commit shortcut.");
        assistant.ClientFactory = (_, _) => new DraftClient(vm.GetIncludedFilePaths().First());

        await assistant.RunAsync(AssistantTask.CommitDraft);
        var file = vm.WorkingFiles.First(item => item.IsIncluded);
        file.IsIncluded = false;
        await assistant.ApplyDraftAsync();
        Assert(assistant.Error.Contains("已变化"), "Changed selection did not invalidate draft.");
        file.IsIncluded = true;
        assistant.ClientFactory = (_, _) => new WaitingClient();
        var running = assistant.RunAsync(AssistantTask.Chat, "解释仓库");
        await Task.Delay(80);
        await vm.OpenRepositoryAsync(path);
        await running;
        Assert(!assistant.IsBusy && !assistant.HasDraft && assistant.Messages.Count == 0, "Repository switch retained a stale task/draft.");
        Assert(!assistant.Options.EnableMcp, "MCP must be opt-in.");
        assistant.IsOpen = false; window.UpdateLayout();
        Assert(!panel.IsVisible && ((ColumnDefinition)window.FindName("ChangesColumn")).MinWidth == 225, "Closing drawer did not restore layout.");
        vm.Summary = originalSummary; vm.Description = originalDescription;

        var settings = new AssistantSettingsStore(Path.Combine(output, "assistant-test-settings.json"));
        var secrets = new MemorySecrets();
        var historyPath = Path.Combine(output, $"assistant-chats-{Guid.NewGuid():N}.db");
        var conversations = new AssistantConversationStore(historyPath);
        using var settingsVm = new AssistantViewModel(vm, persist: true, store: settings, secrets: secrets, conversations: conversations);
        settingsVm.SaveOptions(new("https://example.test/v1", "test-model"), "test-secret-never-serialize", false);
        Assert(!File.ReadAllText(Path.Combine(output, "assistant-test-settings.json")).Contains("test-secret"), "Credential stored in plain settings.");
        Assert(settingsVm.ReadSecret("https://other.example.test/v1") is null && settingsVm.ReadSecret("https://example.test/v1") == "test-secret-never-serialize", "Credentials not bound to endpoint.");
        Assert(AssistantViewModel.SafeError(new Exception("bad test-secret-never-serialize"), "test-secret-never-serialize") == "bad ***", "Error leaked key.");
        var nativeSecrets = new WindowsAssistantSecretStore();
        var testEndpoint = "https://gitmanager-credential-check.invalid/" + Guid.NewGuid().ToString("N");
        try
        {
            nativeSecrets.Write(testEndpoint, "temporary-dummy-key");
            Assert(nativeSecrets.Read(testEndpoint) == "temporary-dummy-key", "Windows credential round-trip failed.");
        }
        finally { nativeSecrets.Delete(testEndpoint); }
        var dialog = new AssistantSettingsWindow(window, settingsVm);
        dialog.Show(); await Task.Delay(80);
        Assert(Descendants(dialog).OfType<PasswordBox>().Count() == 1, "Credential editor must mask API key.");
        Assert(Descendants(dialog).OfType<CheckBox>().Any(box => box.Name == "AssistantIncludeProject")
            && Descendants(dialog).OfType<TextBox>().Any(box => box.Name == "AssistantExcludedPaths"), "Project content settings missing.");
        await render(window, Path.Combine(output, "assistant-reading-trace.png"));
        dialog.Close();
        settingsVm.SaveOptions(settingsVm.Options with { IncludeDiff = false, UseStreaming = false, ExcludedPaths = "private\n*.local.json" }, null, false);
        Assert(!vm.RepositoryTools.ContentPolicy.AllowDiff && !vm.RepositoryTools.ContentPolicy.AllowProject, "Settings did not revoke shared service content access");
        Assert(settings.Load().ExcludedPaths.Contains("private"), "Excluded paths did not persist");
        Console.WriteLine("PASS assistant drawer, narrow layout, tool-generated draft, explicit fill, selection invalidation, repository cancellation and private settings");
        await CheckSavedConversationsAsync(window, settingsVm, settings, secrets, historyPath, output, render);
    }

    private static async Task CheckSavedConversationsAsync(MainWindow window, AssistantViewModel assistant,
        AssistantSettingsStore settings, MemorySecrets secrets, string historyPath, string output, Func<MainWindow, string, Task> render)
    {
        const string secret = "test-secret-never-serialize";
        Assert(assistant.ConversationTitle == "新会话", "An empty conversation was given a history title.");
        assistant.ClientFactory = (_, _) => new TextClient("**已检查** " + secret);
        await assistant.RunAsync(AssistantTask.Chat, "第一次提问\n细节");
        await assistant.RunAsync(AssistantTask.Chat, "继续检查 " + secret);
        assistant.ClientFactory = (_, _) => new WaitingClient();
        var cancelled = assistant.RunAsync(AssistantTask.Chat, "这轮会取消");
        assistant.CancelCommand.Execute(null);
        await cancelled;
        assistant.NewConversationCommand.Execute(null);
        Assert(assistant.ConversationTitle == "新会话", "A new conversation kept the previous title.");
        assistant.ClientFactory = (_, _) => new TextClient("另一段回答");
        await assistant.RunAsync(AssistantTask.Chat, "另一个话题");
        Assert(assistant.Error.Length == 0, assistant.Error);

        var reopened = new AssistantConversationStore(historyPath);
        using var restarted = new AssistantViewModel(window.ViewModel, true, settings, secrets, reopened);
        Assert(restarted.Options.Model == "test-model" && restarted.ReadSecret(restarted.Options.BaseUrl) == secret,
            "The model configuration or saved API key was lost when the assistant was recreated.");
        var sessions = reopened.List();
        Assert(sessions.Count == 2 && sessions[0].Title == "另一个话题" && sessions[1].Title == "第一次提问",
            "New conversations were not saved separately in most-recent order.");
        var first = sessions[1];
        var context = window.ViewModel.RepositoryTools.Context!;
        Assert(first.RepositoryPath == context.Path && first.Branch == context.Branch, "Saved conversation lost its repository or branch.");
        var transcript = reopened.Read(first.Id);
        Assert(transcript.SequenceEqual(new AssistantTurn[] {
            new("user", "第一次提问\n细节"), new("assistant", "**已检查** ***"),
            new("user", "继续检查 ***"), new("assistant", "**已检查** ***") }),
            "Saved messages lost their order or text, or included a cancelled turn or the configured API key.");

        var pane = (AssistantPane)window.FindName("AssistantPanel");
        window.Assistant.IsOpen = true;
        pane.DataContext = restarted;
        restarted.IsOpen = true;
        try
        {
            var title = (Button)pane.FindName("AssistantHistoryButton");
            var menu = (Border)pane.FindName("ConversationMenu");
            var list = (ListBox)pane.FindName("ConversationList");
            var messages = (ItemsControl)pane.FindName("ConversationMessages");
            for (var attempt = 0; !restarted.IsViewingHistory && attempt < 100; attempt++) await Task.Delay(25);
            Assert(Equals(title.Content, "另一个话题") && title.FontWeight == FontWeights.SemiBold && title.FontSize == 13
                && title.Background == Brushes.Transparent && title.BorderThickness == new Thickness(0),
                "The history title did not restore the latest conversation or retained button chrome.");
            var windowCount = Application.Current.Windows.Count;
            title.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert(menu.IsVisible, "Hovering over the title did not open the list.");
            title.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(240);
            Assert(menu.IsVisible && list.Items.Count == 2, "The conversation list closed during the move from the title into it.");
            await render(window, Path.Combine(output, "assistant-history-menu.png"));
            window.UpdateLayout();
            var entry = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var open = Descendants(entry).OfType<Button>().Single(button => button.Content is StackPanel);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; messages.Items.Count != 4 && attempt < 100; attempt++) await Task.Delay(25);
            Assert(!menu.IsVisible && Application.Current.Windows.Count == windowCount && Equals(title.Content, "第一次提问")
                && messages.Items.Count == 4 && ((AssistantMessage)messages.Items[3]).Text == "**已检查** ***",
                "Selecting a conversation did not open its transcript in the original sidebar.");
            await restarted.DeleteConversationAsync(first);
            Assert(!restarted.IsViewingHistory && restarted.ConversationTitle == "新会话", "Deleting the displayed conversation left its title visible.");
        }
        finally
        {
            pane.DataContext = window.Assistant;
            window.Assistant.IsOpen = false;
        }

        Assert(reopened.List().Single().Id == sessions[0].Id && reopened.Read(first.Id).Count == 0,
            "Deleting one conversation did not cascade to its messages or removed the wrong conversation.");
        Console.WriteLine("PASS SQLite persistence, hover history, inline transcript, cancellation, credential redaction and cascading deletion");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class MemorySecrets : IAssistantSecretStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string endpoint) => _values.GetValueOrDefault(endpoint);
        public void Write(string endpoint, string secret) => _values[endpoint] = secret;
        public void Delete(string endpoint) => _values.Remove(endpoint);
    }
    private sealed class DraftClient(string filePath) : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = _calls++;
            if (call == 0) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("diff-call", "get_diff", new Dictionary<string, object?> { ["filePath"] = filePath, ["commitId"] = null, ["staged"] = false })])));
            if (call == 1) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("draft-call", "propose_commit_message", new Dictionary<string, object?> { ["summary"] = "feat: 生成界面测试草稿", ["description"] = "只处理所选文件。" })])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "提交草稿已生成，请查看。")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class WaitingClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class TextClient(string answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
