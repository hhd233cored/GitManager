using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GitManager.Agent;
using GitManager.App.Infrastructure;
using GitManager.Services;
using Microsoft.Extensions.AI;

namespace GitManager.App.ViewModels;

public sealed class AssistantMessage(string label, string text = "") : ObservableObject
{
    private string _text = text;
    public string Label { get; } = label;
    public string Text { get => _text; set => Set(ref _text, value); }
    public ObservableCollection<string> ToolSteps { get; } = [];
}
public sealed record AssistantPlanItem(OperationPlan Plan)
{
    public string Title => Plan.Preview.Title;
    public string Status => Plan.Status switch { "preview" => "预览待查看", "awaiting_approval" => "等待你查看并执行", "running" => "执行中",
        "completed" => "已完成", "failed" => "执行失败", "cancelled" => "已取消", "declined" => "已取消", "stale" => "预览已失效", "needs_attention" => "需要处理冲突", _ => Plan.Status };
    public string Detail => Plan.Result ?? $"{Plan.Preview.Branch} · {Plan.Preview.Head[..8]} → {Plan.Preview.Target[..8]}";
    public bool CanReview => Plan.CanRequestApproval;
}

public sealed class AssistantViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _workspace;
    private readonly AssistantSettingsStore _settingsStore;
    private readonly bool _persist;
    private readonly IAssistantSecretStore _secrets;
    private readonly List<AssistantTurn> _history = [];
    private LocalToolBridgeServer? _bridge;
    private Task _bridgeShutdown = Task.CompletedTask;
    private int _bridgeRevision;
    private CancellationTokenSource? _cancellation;
    private string? _repositoryId;
    private string _conversationId = Guid.NewGuid().ToString("N");
    private string? _conversationTitle;
    private AssistantConversation? _viewedConversation;
    private IReadOnlyList<AssistantMessage> _viewedMessages = [];
    private int _conversationViewRevision;
    private bool _checkedLatestConversation;
    private bool _deletingConversation;
    private bool _disposed;
    private bool _isOpen;
    private bool _isBusy;
    private string _input = "";
    private string _error = "";
    private string _step = "选择提交或文件，让助手解释变更。";
    private string _mcpStatus = "MCP 未启用";
    private CommitDraft? _draft;
    private AssistantOptions _options;
    public AssistantViewModel(MainViewModel workspace, bool persist = true, AssistantSettingsStore? store = null, IAssistantSecretStore? secrets = null)
        : this(workspace, persist, store, secrets, persist ? new AssistantConversationStore() : null) { }

    internal AssistantViewModel(MainViewModel workspace, bool persist, AssistantSettingsStore? store, IAssistantSecretStore? secrets,
        AssistantConversationStore? conversations)
    {
        _workspace = workspace;
        _persist = persist;
        _settingsStore = store ?? new();
        _secrets = secrets ?? new WindowsAssistantSecretStore();
        Conversations = conversations;
        _options = persist ? _settingsStore.Load() : new();
        ApplyContentPolicy();
        SendCommand = new AsyncCommand(() => RunAsync(AssistantTask.Chat), () => CanRun && !IsViewingHistory && !string.IsNullOrWhiteSpace(Input));
        DraftCommand = new AsyncCommand(() => RunAsync(AssistantTask.CommitDraft), () => CanRun && _workspace.IncludedCount > 0);
        ExplainCommitCommand = new AsyncCommand(() => RunAsync(AssistantTask.ExplainCommit), () => CanRun && _workspace.SelectedCommit is not null);
        ExplainDiffCommand = new AsyncCommand(() => RunAsync(AssistantTask.ExplainDiff), () => CanRun && _workspace.SelectedFile is not null);
        AnalyzeSyncCommand = new AsyncCommand(() => RunAsync(AssistantTask.AnalyzeSync), () => CanRun);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        ApplyDraftCommand = new AsyncCommand(ApplyDraftAsync, () => Draft is not null && !IsBusy && !_workspace.IsBusy);
        NewConversationCommand = new RelayCommand(NewConversation, () => !IsBusy);
        ReturnToConversationCommand = new RelayCommand(ReturnToConversation, () => IsViewingHistory && !IsBusy);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        CloseCommand = new RelayCommand(() => IsOpen = false);
        _workspace.PropertyChanged += WorkspaceChanged;
        _workspace.RepositoryTools.PlanChanged += OnPlanChanged;
        _workspace.RepositoryTools.RepositoryUpdated += OnRepositoryUpdated;
        _repositoryId = workspace.RepositoryTools.Context?.Id;
        UpdateBridge();
    }
    public event Action? SettingsRequested;
    private AssistantConversationStore? Conversations { get; }
    public Func<AssistantOptions, string?, IChatClient> ClientFactory { get; set; } = AssistantClientFactory.Create;
    public ObservableCollection<AssistantMessage> Messages { get; } = [];
    public IEnumerable<AssistantMessage> DisplayedMessages => IsViewingHistory ? _viewedMessages : Messages;
    public ObservableCollection<AssistantPlanItem> Plans { get; } = [];
    public ICommand SendCommand { get; }
    public ICommand DraftCommand { get; }
    public ICommand ExplainCommitCommand { get; }
    public ICommand ExplainDiffCommand { get; }
    public ICommand AnalyzeSyncCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplyDraftCommand { get; }
    public ICommand NewConversationCommand { get; }
    public ICommand ReturnToConversationCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CloseCommand { get; }
    public bool IsOpen
    {
        get => _isOpen;
        set { if (Set(ref _isOpen, value) && value) _ = ShowLatestConversationAsync(); }
    }
    public bool IsViewingHistory => _viewedConversation is not null;
    public bool IsLiveConversation => !IsViewingHistory;
    public string ConversationTitle => _viewedConversation?.Title ?? _conversationTitle ?? "新会话";
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Input { get => _input; set { Set(ref _input, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Step { get => _step; private set => Set(ref _step, value); }
    public string McpStatus { get => _mcpStatus; private set => Set(ref _mcpStatus, value); }
    public CommitDraft? Draft { get => _draft; private set { Set(ref _draft, value); Raise(nameof(HasDraft)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasDraft => Draft is not null;
    public AssistantOptions Options => _options;
    public RepositoryTools RepositoryTools => _workspace.RepositoryTools;
    public string Scope => _workspace.RepositoryTools.Context is { } context ? context.Name + " · " + context.Branch : "请先打开一个仓库";
    public string ConnectionLabel => string.IsNullOrWhiteSpace(Options.Model) ? "尚未配置模型 · 打开设置连接" : Options.Model;
    public bool CanRun => !_disposed && !_deletingConversation && !IsBusy && !_workspace.IsBusy && _workspace.HasRepository;
    public string McpConfiguration => ToolJson.Serialize(new { mcpServers = new Dictionary<string, object> {
        ["gitmanager"] = new { command = Path.Combine(AppContext.BaseDirectory, "mcp", "GitManager.Mcp.exe"), args = Array.Empty<string>() } } });
    public string? ReadSecret(string endpoint) => _secrets.Read(endpoint);
    public void SaveOptions(AssistantOptions options, string? newSecret, bool deleteSecret)
    {
        _ = options.Endpoint;
        options.ValidateContentAccess();
        if (!string.IsNullOrWhiteSpace(newSecret)) _secrets.Write(options.BaseUrl, newSecret.Trim());
        else if (deleteSecret) _secrets.Delete(options.BaseUrl);
        if (_persist) _settingsStore.Save(options);
        _options = options;
        _cancellation?.Cancel();
        _history.Clear();
        Draft = null;
        ApplyContentPolicy();
        Raise(nameof(Options)); Raise(nameof(ConnectionLabel));
        UpdateBridge();
    }
    private void ApplyContentPolicy() => RepositoryTools.SetContentPolicy(new(Options.IncludeDiff,
        Options.IncludeDiff && Options.IncludeRepositoryContent, Options.ExcludedPaths));

    public async Task RunAsync(AssistantTask task, string? prompt = null)
    {
        IsOpen = true;
        if (!CanRun) return;
        Error = "";
        if (string.IsNullOrWhiteSpace(Options.Model)) { SettingsRequested?.Invoke(); return; }
        var context = _workspace.RepositoryTools.Context;
        if (context is null) return;
        if (task == AssistantTask.ExplainDiff && _workspace.IsStash) { Error = "当前显示的是 Stash。可先选择工作区文件或 History 文件进行解释。"; return; }
        prompt ??= task switch {
            AssistantTask.CommitDraft => "为勾选的文件生成简洁的中文提交说明。",
            AssistantTask.ExplainCommit => "解释选中的提交，包括它解决的问题、行为变化和需要注意的地方。",
            AssistantTask.ExplainDiff => "解释选中文件的 Diff 和可能影响。",
            AssistantTask.AnalyzeSync => "获取最新远程信息，分析本地与远程的提交差异和建议的同步方式。本次只分析。",
            _ => Input.Trim() };
        if (prompt.Length == 0) return;
        ReturnToConversation();
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Draft = null;
        Step = "正在分析…";
        var message = new AssistantMessage("助手");
        Messages.Add(new("你", prompt)); Messages.Add(message);
        while (Messages.Count > 32) Messages.RemoveAt(0);
        if (task == AssistantTask.Chat) Input = "";
        string? key = null;
        var acceptingProgress = true;
        try
        {
            key = _secrets.Read(Options.BaseUrl);
            using var model = ClientFactory(Options, key);
            var request = new AssistantRequest(context, task, prompt, _workspace.GetIncludedFilePaths(),
                task != AssistantTask.CommitDraft && (task == AssistantTask.ExplainCommit || _workspace.IsHistory) ? _workspace.SelectedCommit?.Commit.Id : null,
                task == AssistantTask.CommitDraft ? null : _workspace.SelectedFile?.Path, task != AssistantTask.CommitDraft && _workspace.ShowStagedDiff);
            var progress = new Progress<AssistantProgress>(update =>
            {
                if (_disposed || _repositoryId != context.Id || !acceptingProgress) return;
                if (update.Kind == "text") message.Text += update.Text;
                else
                {
                    Step = update.Text;
                    if (message.ToolSteps.Count < 64) message.ToolSteps.Add(update.Text);
                }
            });
            var result = await new AssistantSession(_workspace.RepositoryTools).RunAsync(model, Options, request, _history.ToArray(), progress, cancellation.Token);
            acceptingProgress = false;
            if (_disposed || RepositoryTools.Context?.Id != context.Id) return;
            cancellation.Token.ThrowIfCancellationRequested();
            message.Text = result.Text;
            Draft = result.Draft;
            _history.Add(new("user", prompt)); _history.Add(new("assistant", result.Text));
            while (_history.Count > 6) _history.RemoveAt(0);
            Step = Draft is not null ? "草稿已生成，查看后可填入提交区。" : "分析完成";
            await SaveTurnAsync(context, prompt, result, key);
        }
        catch (OperationCanceledException)
        {
            if (_repositoryId == context.Id) { if (message.Text.Length == 0) message.Text = "已停止本次分析。"; Step = "已停止"; }
        }
        catch (Exception ex)
        {
            if (_repositoryId == context.Id)
            {
                Error = SafeError(ex, key);
                if (message.Text.Length == 0) message.Text = "本次分析未完成，请检查设置或缩小分析范围后重试。";
                Step = "分析未完成";
            }
        }
        finally
        {
            acceptingProgress = false;
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            IsBusy = false;
        }
    }

    private async Task SaveTurnAsync(RepositoryContext repository, string prompt, AssistantResult result, string? key)
    {
        var archivedQuestion = HideSecret(prompt, key);
        if (_conversationTitle is null)
        {
            _conversationTitle = AssistantConversationStore.CreateTitle(archivedQuestion);
            Raise(nameof(ConversationTitle));
        }
        if (Conversations is not { } conversations) return;
        var id = _conversationId;
        var title = _conversationTitle;
        var answer = result.Text;
        if (result.Draft is { } draft)
            answer += $"\n\n### 提交草稿\n\n{draft.Summary}\n\n{draft.Description}\n\n{draft.Warning}";
        var archivedAnswer = HideSecret(answer, key);
        try { await Task.Run(() => conversations.Append(id, repository, title, archivedQuestion, archivedAnswer)); }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex))
        {
            if (!_disposed && id == _conversationId) Error = "无法保存本轮对话。" + SafeError(ex, key);
        }
    }

    private void NewConversation()
    {
        ReturnToConversation();
        ResetLiveConversation();
    }

    private void ResetLiveConversation()
    {
        _conversationId = Guid.NewGuid().ToString("N");
        _conversationTitle = null;
        Messages.Clear();
        _history.Clear();
        Draft = null;
        Error = "";
        Raise(nameof(ConversationTitle));
    }

    internal async Task<IReadOnlyList<AssistantConversation>> ListConversationsAsync()
        => Conversations is { } conversations ? await Task.Run(conversations.List) : [];

    private async Task ShowLatestConversationAsync()
    {
        if (_checkedLatestConversation) return;
        _checkedLatestConversation = true;
        var revision = _conversationViewRevision;
        try
        {
            var latest = (await ListConversationsAsync()).FirstOrDefault();
            if (latest is not null && IsOpen && !IsBusy && Messages.Count == 0 && Plans.Count == 0 && revision == _conversationViewRevision)
                await ViewConversationAsync(latest);
        }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex))
        {
            if (!_disposed && revision == _conversationViewRevision) Error = "无法读取会话列表。" + SafeError(ex, null);
        }
    }

    internal async Task ViewConversationAsync(AssistantConversation conversation)
    {
        if (_disposed || _deletingConversation || !IsOpen || IsBusy || Conversations is not { } conversations) return;
        if (conversation.Id == _conversationId) { ReturnToConversation(); return; }
        var revision = ++_conversationViewRevision;
        try
        {
            var messages = await Task.Run(() => conversations.Read(conversation.Id));
            if (_disposed || !IsOpen || revision != _conversationViewRevision) return;
            if (messages.Count == 0) { Error = "这段会话已删除。"; return; }
            _viewedMessages = messages.Select(message => new AssistantMessage(message.Role == "user" ? "你" : "助手", message.Text)).ToArray();
            _viewedConversation = conversation;
            Error = "";
            NotifyConversationViewChanged();
        }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex))
        {
            if (!_disposed && revision == _conversationViewRevision) Error = "无法读取这段会话。" + SafeError(ex, null);
        }
    }

    internal async Task DeleteConversationAsync(AssistantConversation conversation)
    {
        if (_disposed || _deletingConversation || IsBusy || Conversations is not { } conversations) return;
        _deletingConversation = true;
        _conversationViewRevision++;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await Task.Run(() => conversations.Delete(conversation.Id));
            if (_disposed) return;
            if (_viewedConversation?.Id == conversation.Id) ReturnToConversation();
            if (_conversationId == conversation.Id) ResetLiveConversation();
        }
        finally
        {
            _deletingConversation = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ReturnToConversation()
    {
        _conversationViewRevision++;
        if (_viewedConversation is null) return;
        _viewedConversation = null;
        _viewedMessages = [];
        Error = "";
        NotifyConversationViewChanged();
    }

    private void NotifyConversationViewChanged()
    {
        Raise(nameof(ConversationTitle));
        Raise(nameof(IsViewingHistory));
        Raise(nameof(IsLiveConversation));
        Raise(nameof(DisplayedMessages));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task ApplyDraftAsync()
    {
        var draft = Draft;
        if (draft is null) return;
        try
        {
            var fingerprint = await _workspace.RepositoryTools.CaptureFingerprintAsync(draft.RepositoryId);
            _workspace.RepositoryTools.RequireContext(draft.RepositoryId);
            if (!_workspace.GetIncludedFilePaths().Order(StringComparer.Ordinal).SequenceEqual(draft.SelectedPaths.Order(StringComparer.Ordinal))
                || fingerprint != draft.Fingerprint)
                throw new InvalidOperationException("勾选文件或文件内容已变化，请重新生成提交草稿。");
            _workspace.Summary = draft.Summary;
            _workspace.Description = draft.Description;
            Step = "已填入提交区，可以继续编辑后提交。";
            Draft = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void DeclinePlan(OperationPlan plan)
    {
        try { _workspace.RepositoryTools.DeclinePlan(plan.RepositoryId, plan.Id); }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.RepositoryName) or nameof(MainViewModel.BranchName) or nameof(MainViewModel.HasRepository))
        {
            Raise(nameof(Scope));
            var id = _workspace.RepositoryTools.Context?.Id;
            if (id != _repositoryId)
            {
                _cancellation?.Cancel();
                _repositoryId = id;
                NewConversation();
                Plans.Clear();
                Step = "上下文已切换到当前仓库和分支。";
            }
        }
        CommandManager.InvalidateRequerySuggested();
    }
    private void OnPlanChanged(OperationPlan plan) => OnUi(() =>
    {
        if (_disposed || _workspace.RepositoryTools.Context?.Id != plan.RepositoryId) return;
        var existing = Plans.FirstOrDefault(item => item.Plan.Id == plan.Id);
        if (existing is null) Plans.Insert(0, new(plan));
        else Plans[Plans.IndexOf(existing)] = new(plan);
        while (Plans.Count > 10) Plans.RemoveAt(Plans.Count - 1);
        if (plan.Status == "awaiting_approval")
        {
            ReturnToConversation();
            IsOpen = true;
        }
    });
    private void OnRepositoryUpdated(string id, bool fetched) => OnUi(() => { if (!_disposed) _workspace.OnAssistantRepositoryUpdated(id, fetched); });
    private void UpdateBridge()
    {
        var revision = ++_bridgeRevision;
        if (!Options.EnableMcp)
        {
            var previous = _bridge;
            _bridge = null;
            if (previous is not null) _bridgeShutdown = previous.DisposeAsync().AsTask();
            McpStatus = "MCP 未启用";
            return;
        }
        if (_bridge is null) _ = StartBridgeAsync(revision);
    }
    private async Task StartBridgeAsync(int revision)
    {
        await _bridgeShutdown;
        if (!_disposed && Options.EnableMcp && revision == _bridgeRevision) StartBridge();
    }
    private void StartBridge()
    {
        _bridge = new LocalToolBridgeServer((call, token) =>
        {
            if (!Options.EnableMcp || _disposed) throw new InvalidOperationException("MCP 访问已关闭。");
            return _workspace.RepositoryTools.InvokeAsync(call, token);
        });
        _bridge.Failed += error => OnUi(() => McpStatus = "MCP 未启动：" + error);
        McpStatus = "MCP 已启用 · 仅当前仓库";
    }
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }
    public static string SafeError(Exception exception, string? key)
    {
        return RepositoryTools.Clip(HideSecret(ToolErrors.Message(exception), key), 700);
    }
    private static string HideSecret(string text, string? key) => string.IsNullOrWhiteSpace(key) ? text : text.Replace(key, "***", StringComparison.Ordinal);
    public void Dispose()
    {
        _disposed = true;
        _cancellation?.Cancel();
        _workspace.PropertyChanged -= WorkspaceChanged;
        _workspace.RepositoryTools.PlanChanged -= OnPlanChanged;
        _workspace.RepositoryTools.RepositoryUpdated -= OnRepositoryUpdated;
        if (_bridge is not null) { _ = _bridge.DisposeAsync(); _bridge = null; }
    }
}
