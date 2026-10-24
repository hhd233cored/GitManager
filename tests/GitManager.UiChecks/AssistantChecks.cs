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
        using var settingsVm = new AssistantViewModel(vm, persist: true, store: settings, secrets: secrets);
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
        dialog.Close();
        Console.WriteLine("PASS assistant drawer, narrow layout, tool-generated draft, explicit fill, selection invalidation, repository cancellation and private settings");
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
}
