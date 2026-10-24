using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using GitManager.App.Infrastructure;
using GitManager.App.Controls;
using GitManager.Core;
using GitManager.Services;

namespace GitManager.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly GitService _git = new();
    private readonly SettingsStore _store;
    private readonly bool _persistSettings;
    private bool _updatingCommits;
    private bool _updatingFiles;
    public RepositoryCoordinator Coordinator { get; } = new();
    public RepositoryTools RepositoryTools { get; }
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _diffCancellation;
    private CancellationTokenSource _repositoryCancellation = new();
    private FileSystemWatcher? _watcher;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private readonly System.Windows.Threading.DispatcherTimer _fetchTimeTimer;
    private bool _disposed;
    private RepositoryInfo? _repository;
    private bool _isBusy;
    private string _busyMessage = "";
    private string _error = "";
    private string _notice = "打开一个仓库，开始查看提交历史。";
    private bool _isHistory = true;
    private bool _allBranches;
    private bool _showStagedDiff;
    private string _historySearch = "";
    private string _fileSearch = "";
    private string _summary = "";
    private string _description = "";
    private DateTimeOffset? _lastFetchedAt;
    private string? _fetchedSyncContext;
    private int _historyLimit = 300;
    private bool _canLoadMore;
    private CommitItem? _selectedCommit;
    private FileItem? _selectedFile;
    private FileItem? _selectedWorkingFile;
    private DiffDocument? _diff;
    private bool _isLoadingDetails;
    private bool _isLoadingDiff;
    private IReadOnlyList<CommitItem> _allCommits = [];
    private IReadOnlyList<ChangedFile> _workingFiles = [];
    private IReadOnlyList<ChangedFile> _commitFiles = [];
    private readonly Dictionary<string, string> _historyFileSelections = [];
    private readonly Dictionary<string, CommitDetails> _commitDetails = new(StringComparer.Ordinal);
    private string? _workingSelectionPath;
    private readonly Dictionary<string, DateTimeOffset> _fetchTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _excludedFiles = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(bool persistSettings = true, SettingsStore? settingsStore = null)
    {
        _persistSettings = persistSettings;
        RepositoryTools = new RepositoryTools(_git, Coordinator, persistSettings ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager", "operations.jsonl") : null);
        _store = settingsStore ?? new SettingsStore();
        var settings = persistSettings ? _store.Load() : new AppSettings([], null);
        _allBranches = settings.AllBranches;
        var savedRepositories = settings.Repositories ?? (settings.RecentRepositories ?? []).Select(path => new RepositoryEntry(path)).ToList();
        foreach (var entry in savedRepositories)
        {
            try
            {
                var normalized = entry with { Path = RepositoryEntry.NormalizePath(entry.Path) };
                if (!Repositories.Any(existing => RepositoryEntry.SamePath(existing.Path, normalized.Path))) Repositories.Add(normalized);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        LastRepository = settings.LastRepository;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => HasRepository && !IsBusy);
        FetchCommand = new AsyncCommand(FetchAsync, () => HasRepository && Repository!.HasRemote && !IsBusy);
        PullCommand = new AsyncCommand(PullAsync, () => HasRepository && Repository!.Upstream is not null && !IsBusy && !HasActiveOperation);
        PushCommand = new AsyncCommand(PushAsync, () => HasRepository && Repository!.HasRemote && Repository.HasHead && !Repository.IsDetached && !IsBusy && !HasActiveOperation);
        SyncCommand = new AsyncCommand(ExecuteSyncAsync, () => !IsBusy && SyncState.IsEnabled && !HasActiveOperation);
        CommitCommand = new AsyncCommand(CommitAsync, () => HasRepository && !IsBusy && !HasActiveOperation && IncludedCount > 0 && !string.IsNullOrWhiteSpace(Summary));
        ToggleAllChangesCommand = new RelayCommand(() => SetAllChangesIncluded(AllChangesIncluded != true), () => HasRepository && ChangeCount > 0 && !IsBusy);
        LoadMoreCommand = new AsyncCommand(async () => { _historyLimit += 300; await RefreshAsync(); }, () => HasRepository && CanLoadMore && !IsBusy);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => IsBusy);
        DismissErrorCommand = new RelayCommand(() => Error = "");
        ClearHistoryPathCommand = new RelayCommand(ClearHistoryPath, () => HasHistoryPathFilter);
        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            if (IsBusy) { _refreshTimer.Start(); return; }
            if (HasRepository) await RefreshAsync(silent: true);
        };
        _fetchTimeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _fetchTimeTimer.Tick += (_, _) => { Raise(nameof(LastFetched)); RaiseSyncProperties(); };
        _fetchTimeTimer.Start();
    }

    public event Action<string>? OperationCompleted;
    public string? LastRepository { get; private set; }
    public ObservableCollection<RepositoryEntry> Repositories { get; } = [];
    public ObservableCollection<CommitItem> Commits { get; } = [];
    public ObservableCollection<FileItem> Files { get; } = [];
    public ObservableCollection<FileItem> WorkingFiles { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand ToggleAllChangesCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DismissErrorCommand { get; }
    public ICommand ClearHistoryPathCommand { get; }

    public RepositoryInfo? Repository
    {
        get => _repository;
        private set
        {
            var previousContext = CurrentSyncContext;
            if (!Set(ref _repository, value)) return;
            RepositoryTools.SetRepository(value?.Path, value?.Branch ?? "");
            if (previousContext != CurrentSyncContext) _fetchedSyncContext = null;
            foreach (var name in new[] { nameof(HasRepository), nameof(RepositoryName), nameof(BranchName), nameof(PushLabel), nameof(CommitButtonLabel), nameof(RepositoryPath) }) Raise(name);
            RaiseSyncProperties();
            RaiseOperationProperties();
        }
    }
    public bool HasRepository => Repository is not null;
    public string RepositoryName => Repository is null ? "添加本地仓库"
        : Repositories.FirstOrDefault(entry => RepositoryEntry.SamePath(entry.Path, Repository.Path))?.Name ?? Repository.Name;
    public string RepositoryPath => Repository?.Path ?? "选择文件夹或克隆远程仓库";
    public string BranchName => Repository?.Branch ?? "未选择仓库";
    public string PushLabel => Repository?.Upstream is null ? "发布分支" : "Push";
    private string? CurrentSyncContext => Repository is null ? null
        : $"{Repository.Path.ToUpperInvariant()}\0{Repository.Branch}\0{Repository.RemoteName}\0{Repository.Upstream}";
    private bool HasFetchedCurrentContext => _fetchedSyncContext is not null && _fetchedSyncContext == CurrentSyncContext;
    public RepositorySyncState SyncState => RepositorySyncState.From(Repository, HasFetchedCurrentContext);
    public string SyncTitle => SyncState.Label;
    public string SyncGlyph => SyncState.Glyph;
    public string SyncBadge => SyncState.Badge;
    public bool HasSyncBadge => SyncBadge.Length > 0;
    public string SyncSubtitle => Repository is null ? "选择仓库以同步"
        : !Repository.HasRemote ? "尚未配置远程仓库"
        : LastFetched;
    public string SyncToolTip => !SyncState.IsEnabled ? SyncSubtitle : SyncState.Action switch
    {
        SyncAction.Resolve => "两边都有独有提交，选择合并、变基或采用某一端的版本。",
        SyncAction.Pull => "拉取远程更新（仅快进）；分支分叉时保留现状并显示 Git 错误。",
        SyncAction.Push => "推送当前分支到它的上游分支。",
        SyncAction.Publish => $"发布当前分支到 {SyncState.RemoteName}，并设置上游分支。",
        _ => $"从 {SyncState.RemoteName} 获取最新状态，随后按提交差异显示 Pull 或 Push。"
    };
    public bool CanManageRepositories => !IsBusy;
    public string LastFetched => FormatFetchTime(_lastFetchedAt, DateTimeOffset.UtcNow);
    internal static string FormatFetchTime(DateTimeOffset? fetched, DateTimeOffset now)
    {
        if (fetched is null) return "Last fetched never";
        var elapsed = now - fetched.Value;
        if (elapsed.TotalMinutes < 1) return "Last fetched just now";
        var count = elapsed.TotalHours < 1 ? (int)elapsed.TotalMinutes : elapsed.TotalDays < 1 ? (int)elapsed.TotalHours : (int)elapsed.TotalDays;
        var unit = elapsed.TotalHours < 1 ? "minute" : elapsed.TotalDays < 1 ? "hour" : "day";
        return $"Last fetched {count} {unit}{(count == 1 ? "" : "s")} ago";
    }
    private void SetLastFetchTime(DateTimeOffset? fetched)
    {
        _lastFetchedAt = fetched;
        Raise(nameof(LastFetched));
        RaiseSyncProperties();
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); Raise(nameof(CanManageRepositories)); RaiseOperationProperties(); CommandManager.InvalidateRequerySuggested(); }
    }
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }
    public string Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    // Selects the shared Diff source; Changes and History themselves are always visible.
    public bool IsHistory
    {
        get => _isHistory;
        set
        {
            if (!Set(ref _isHistory, value)) return;
            if (!value) ClearStashPreview();
            Raise(nameof(IsChanges));
            if (value)
            {
                _selectedWorkingFile = null;
                Raise(nameof(SelectedWorkingFile));
            }
            UpdateFiles();
            if (value && SelectedCommit is not null) _ = LoadCommitDetailsAsync(SelectedCommit);
            RaiseDetailProperties();
        }
    }
    public bool IsChanges { get => !IsHistory; set { if (value) IsHistory = false; } }
    public bool AllBranches
    {
        get => _allBranches;
        set
        {
            if (!Set(ref _allBranches, value)) return;
            SaveSettings();
            if (HasRepository && !IsBusy) { _historyLimit = 300; _ = RefreshAsync(); }
        }
    }
    public bool ShowStagedDiff
    {
        get => _showStagedDiff;
        set { if (Set(ref _showStagedDiff, value) && IsChanges) { Raise(nameof(DiffSourceLabel)); _ = LoadDiffAsync(); } }
    }
    public string HistorySearch
    {
        get => _historySearch;
        set { if (Set(ref _historySearch, value)) UpdateCommitFilter(); }
    }
    public string FileSearch
    {
        get => _fileSearch;
        set
        {
            if (!Set(ref _fileSearch, value)) return;
            UpdateWorkingFiles();
            if (IsChanges) UpdateFiles();
        }
    }
    public string Summary { get => _summary; set { Set(ref _summary, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Description { get => _description; set => Set(ref _description, value); }
    public int ChangeCount => _workingFiles.Count;
    public int StagedCount => _workingFiles.Count(file => file.HasStagedChanges);
    public int IncludedCount => _workingFiles.Count(file => IsFileIncluded(file.Path));
    public bool? AllChangesIncluded => ChangeCount == 0 || IncludedCount == 0 ? false : IncludedCount == ChangeCount ? true : null;
    public int CommitCount => _allCommits.Count;
    public int FileCount => IsStash ? _stashFiles.Length : IsHistory ? _commitFiles.Count : _workingFiles.Count;
    public string CommitButtonLabel => $"提交 {IncludedCount} 个文件到 {BranchName}";
    public bool CanLoadMore { get => _canLoadMore; private set => Set(ref _canLoadMore, value); }
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => Set(ref _isLoadingDetails, value); }
    public bool IsLoadingDiff { get => _isLoadingDiff; private set => Set(ref _isLoadingDiff, value); }
    public bool HasCommit => IsHistory && !IsStash && SelectedCommit is not null;
    public string DetailSubject => IsStash ? "Stash · " + SelectedStash!.Message : IsHistory ? SelectedCommit?.Commit.Subject ?? (HasHistoryPathFilter ? "该路径没有匹配的已加载提交" : "选择一条提交") : "工作区变更";
    public string DetailBody => IsStash ? $"{SelectedStash!.Selector} · {SelectedStash.Date.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : IsHistory ? SelectedCommit?.Commit.Body ?? "" : "选择文件查看变更，勾选要包含在本次提交中的文件。";
    public string DetailAuthor => SelectedCommit?.Commit.Author ?? "";
    public string DetailDate => SelectedCommit?.Commit.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "";
    public string DetailSha => SelectedCommit?.Commit.Id ?? "";
    public string DetailShortSha => SelectedCommit?.Commit.ShortId ?? "";
    public string DetailInitials => SelectedCommit?.Commit.Initials ?? "G";
    public string ParentDescription => SelectedCommit is null ? "" : SelectedCommit.Commit.Parents.Count switch
    {
        0 => "初始提交", 1 => $"父提交 {SelectedCommit.Commit.Parents[0][..8]}", _ => $"合并提交 · {SelectedCommit.Commit.Parents.Count} 个父提交 · Diff 对比第一父提交"
    };
    public string FileTitle => SelectedFile?.DisplayPath ?? "选择一个文件查看 Diff";
    public string FileStatus => SelectedFile?.StatusLabel ?? "";
    public bool CanChooseDiffSource => IsChanges && SelectedFile is { HasStagedChanges: true, HasUnstagedChanges: true } && !SelectedFile.File.IsConflicted;
    public string DiffSourceLabel => IsStash ? "Stash · " + SelectedStash!.Selector : IsHistory ? "提交变更" : ShowStagedDiff ? "已暂存的变更" : "文件全部变更";
    public CommitItem? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (_updatingCommits) return;
            if (value is { IsVisible: false }) return;
            if (_selectedFile?.File.IsHistory == true) _selectedFile.IsSelected = false;
            var wasStash = IsStash;
            ClearStashPreview();
            var changed = Set(ref _selectedCommit, value) || wasStash;
            if (!IsHistory) { IsHistory = true; return; }
            if (!changed) return;
            RaiseDetailProperties();
            if (IsHistory) _ = LoadCommitDetailsAsync(value);
        }
    }
    public FileItem? SelectedWorkingFile
    {
        get => _selectedWorkingFile;
        set
        {
            if (_updatingFiles || value is null) return;
            _workingSelectionPath = value.Path;
            if (IsHistory) IsHistory = false;
            SelectedFile = WorkingFiles.FirstOrDefault(file => file.Path == value.Path);
        }
    }
    public FileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_updatingFiles) return;
            var previous = _selectedFile;
            if (!Set(ref _selectedFile, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = !IsHistory || IsStash;
            if (value is not null)
            {
                if (IsHistory && !IsStash && SelectedCommit is not null) _historyFileSelections[SelectedCommit.Commit.Id] = value.Path;
                else if (IsChanges) _workingSelectionPath = value.Path;
            }
            if (value is not null && IsChanges) _showStagedDiff = false;
            if (IsChanges)
            {
                _selectedWorkingFile = value;
                Raise(nameof(SelectedWorkingFile));
            }
            foreach (var name in new[] { nameof(FileTitle), nameof(FileStatus), nameof(CanChooseDiffSource), nameof(ShowStagedDiff), nameof(DiffSourceLabel) }) Raise(name);
            Raise(nameof(CanResolveSelectedFile));
            _ = LoadDiffAsync();
        }
    }
    public DiffDocument? Diff
    {
        get => _diff;
        private set { Set(ref _diff, value); Raise(nameof(Additions)); Raise(nameof(Deletions)); }
    }
    public int Additions => Diff?.Additions ?? 0;
    public int Deletions => Diff?.Deletions ?? 0;

    public Task OpenRepositoryAsync(string path) => RunOperationAsync("正在打开仓库…", ct => OpenRepositoryCoreAsync(path, ct));

    public Task AddRepositoriesAsync(IEnumerable<string> paths)
    {
        var requestedPaths = paths.ToArray();
        return RunOperationAsync("正在添加仓库…", async ct =>
        {
            var initialCount = Repositories.Count;
            var errors = new List<string>();
            string? lastPath = null;
            foreach (var requestedPath in requestedPaths)
            {
                try
                {
                    var info = await _git.GetRepositoryAsync(requestedPath, ct);
                    RegisterRepository(info.Path);
                    lastPath = info.Path;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{requestedPath}\n{ex.Message}"); }
            }
            SaveSettings();
            if (lastPath is not null) await OpenRepositoryCoreAsync(lastPath, ct);
            if (Repositories.Count > initialCount) Notice = $"已添加 {Repositories.Count - initialCount} 个仓库";
            if (errors.Count > 0) Error = string.Join("\n\n", errors);
        });
    }

    private async Task OpenRepositoryCoreAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"找不到仓库文件夹：{path}\n请在管理仓库列表中更改位置。");
        var repository = await _git.GetRepositoryAsync(path, ct);
        ClearRepository();
        RegisterRepository(repository.Path);
        Repository = repository;
        LastRepository = repository.Path;
        SetLastFetchTime(_fetchTimes.TryGetValue(repository.Path, out var fetched) ? fetched : null);
        SaveSettings();
        try { await RefreshDataAsync(ct); }
        finally { StartWatcher(repository.Path); }
        Notice = $"已打开 {RepositoryName}";
    }

    private void RegisterRepository(string path)
    {
        var normalized = RepositoryEntry.NormalizePath(path);
        if (!Repositories.Any(entry => RepositoryEntry.SamePath(entry.Path, normalized))) Repositories.Add(new RepositoryEntry(normalized));
    }

    private void ClearRepository()
    {
        _refreshTimer.Stop();
        _repositoryCancellation.Cancel();
        _repositoryCancellation.Dispose();
        _repositoryCancellation = new CancellationTokenSource();
        _commitDetails.Clear();
        Stashes.Clear();
        RecoveryPoints.Clear();
        ClearStashPreview();
        CurrentOperation = new RepositoryOperation(RepositoryOperationKind.None, []);
        _selectionCancellation?.Cancel();
        _diffCancellation?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        _historyLimit = 300;
        _allCommits = [];
        _commitFiles = [];
        _workingFiles = [];
        _selectedCommit = null;
        _selectedFile = null;
        _selectedWorkingFile = null;
        _updatingCommits = _updatingFiles = true;
        try { Commits.Clear(); Files.Clear(); WorkingFiles.Clear(); Branches.Clear(); }
        finally { _updatingCommits = _updatingFiles = false; }
        _historyFileSelections.Clear();
        _workingSelectionPath = null;
        Repository = null;
        LastRepository = null;
        ClearExplorer();
        HistorySearch = "";
        FileSearch = "";
        Summary = "";
        Description = "";
        SetLastFetchTime(null);
        Diff = null;
        CanLoadMore = false;
        IsLoadingDetails = IsLoadingDiff = false;
        foreach (var name in new[] { nameof(SelectedCommit), nameof(SelectedFile), nameof(SelectedWorkingFile), nameof(CommitCount), nameof(ChangeCount), nameof(StagedCount), nameof(FileTitle), nameof(CanChooseDiffSource) }) Raise(name);
        RaiseDetailProperties();
        RaiseInclusionProperties();
        CommandManager.InvalidateRequerySuggested();
    }

    public void RenameRepository(RepositoryEntry entry, string name)
    {
        var index = FindRepositoryIndex(entry.Path);
        if (index < 0) return;
        var displayName = name.Trim();
        Repositories[index] = Repositories[index] with { DisplayName = displayName.Length > 0 ? displayName : null };
        Raise(nameof(RepositoryName));
        SaveSettings();
    }

    public void MoveRepository(RepositoryEntry entry, int offset)
    {
        var index = FindRepositoryIndex(entry.Path);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Repositories.Count) return;
        Repositories.Move(index, target);
        SaveSettings();
    }

    public Task RemoveRepositoryAsync(RepositoryEntry entry) => RunOperationAsync("正在更新仓库列表…", async ct =>
    {
        var index = FindRepositoryIndex(entry.Path);
        if (index < 0) return;
        var wasActive = RepositoryEntry.SamePath(Repository?.Path, entry.Path);
        Repositories.RemoveAt(index);
        _excludedFiles.Remove(entry.Path);
        if (RepositoryEntry.SamePath(LastRepository, entry.Path)) LastRepository = null;
        _fetchTimes.Remove(entry.Path);
        if (wasActive)
        {
            ClearRepository();
            var next = Repositories.Skip(index).Concat(Repositories.Take(index)).FirstOrDefault(candidate => Directory.Exists(candidate.Path));
            SaveSettings();
            if (next is not null) await OpenRepositoryCoreAsync(next.Path, ct);
        }
        SaveSettings();
        Notice = $"已从列表移除 {entry.Name}";
    });

    public Task RelocateRepositoryAsync(RepositoryEntry entry, string path) => RunOperationAsync("正在更新仓库位置…", async ct =>
    {
        var info = await _git.GetRepositoryAsync(path, ct);
        var index = FindRepositoryIndex(entry.Path);
        if (index < 0) return;
        if (Repositories.Where((_, candidate) => candidate != index).Any(candidate => RepositoryEntry.SamePath(candidate.Path, info.Path)))
            throw new InvalidOperationException("该仓库已在列表中，请选择另一个位置。");
        var wasActive = RepositoryEntry.SamePath(Repository?.Path, entry.Path);
        Repositories[index] = Repositories[index] with { Path = info.Path };
        if (wasActive) await OpenRepositoryCoreAsync(info.Path, ct);
        Raise(nameof(RepositoryName));
        SaveSettings();
        Notice = "仓库位置已更新";
    });

    private int FindRepositoryIndex(string path)
    {
        for (var i = 0; i < Repositories.Count; i++)
            if (RepositoryEntry.SamePath(Repositories[i].Path, path)) return i;
        return -1;
    }

    public Task RefreshAsync(bool silent = false) => RunOperationAsync(silent ? "正在同步本地状态…" : "正在刷新仓库…", async ct =>
    {
        await RefreshDataAsync(ct);
        if (!silent) Notice = "仓库状态已更新";
    }, silent);

    private async Task RefreshDataAsync(CancellationToken ct)
    {
        if (Repository is null) return;
        var path = Repository.Path;
        var infoTask = _git.GetRepositoryAsync(path, ct);
        var historyTask = _git.GetHistoryPageAsync(path, AllBranches, _historyLimit, ct);
        var filesTask = _git.GetStatusAsync(path, ct);
        var branchesTask = _git.GetBranchesAsync(path, ct);
        var treeTask = _git.GetRepositoryFilesAsync(path, ct);
        var stashesTask = _git.GetStashesAsync(path, ct);
        var recoveryTask = _git.GetRecoveryPointsAsync(path, ct);
        var operationTask = _git.GetOperationAsync(path, ct);
        var fetchTimeTask = _git.GetLastFetchTimeAsync(path, ct);
        await Task.WhenAll(infoTask, historyTask, filesTask, branchesTask, treeTask, stashesTask, recoveryTask, operationTask, fetchTimeTask);
        Repository = await infoTask;
        var lastFetch = await fetchTimeTask;
        if (lastFetch is not null) _fetchTimes[path] = lastFetch.Value;
        SetLastFetchTime(lastFetch ?? (_fetchTimes.TryGetValue(path, out var cachedFetch) ? cachedFetch : null));
        var history = await historyTask;
        CanLoadMore = history.HasMore;
        var shown = history.Commits;
        var layout = history.Graph;
        _allCommits = shown.Select((commit, i) => new CommitItem(commit, layout.Rows[i], GraphCell.WidthFor(layout.Rows[i]))
        {
            Details = GetCommitDetails(commit.Id)
        }).ToArray();
        _workingFiles = await filesTask;
        ReconcileInclusions();
        CurrentOperation = await operationTask;
        Stashes.Clear();
        foreach (var entry in await stashesTask) Stashes.Add(entry);
        RecoveryPoints.Clear();
        foreach (var entry in await recoveryTask) RecoveryPoints.Add(entry);
        Raise(nameof(StashLabel));
        RaiseOperationProperties();
        UpdateWorkingFiles();
        if (CurrentOperation.HasConflicts) IsHistory = false;
        UpdateRepositoryTree(await treeTask);
        Branches.Clear();
        foreach (var branch in await branchesTask) Branches.Add(branch);
        await RefreshHistoryPathAsync(ct);
        Raise(nameof(SelectedCommit));
        Raise(nameof(CommitCount));
        Raise(nameof(ChangeCount));
        Raise(nameof(StagedCount));
        RaiseInclusionProperties();
        RaiseDetailProperties();
        if (IsStash)
        {
            var entry = Stashes.FirstOrDefault(item => item.Id == SelectedStash!.Id);
            if (entry is not null) await ViewStashAsync(entry);
            else { ClearStashPreview(); await LoadCommitDetailsAsync(SelectedCommit); }
        }
        else if (IsHistory) await LoadCommitDetailsAsync(SelectedCommit);
        else UpdateFiles(forceDiff: true);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateCommitFilter(string? preferredId = null)
    {
        // Mask nodes and text while keeping every graph row and its geometry in place.
        var selectedId = preferredId ?? SelectedCommit?.Commit.Id;
        var rows = _allCommits.Select(item => item with { IsVisible = !HasHistoryPathFilter || _pathCommitIds?.Contains(item.Commit.Id) == true }).ToArray();
        _updatingCommits = true;
        try
        {
            Commits.Clear();
            foreach (var item in rows) Commits.Add(item);
        }
        finally { _updatingCommits = false; }
        var visible = rows.Where(item => item.IsVisible).ToArray();
        var selection = (HasSearch ? visible.FirstOrDefault(IsSearchMatch) : null)
            ?? visible.FirstOrDefault(item => item.Commit.Id == selectedId) ?? visible.FirstOrDefault();
        var changed = !Equals(_selectedCommit, selection);
        _selectedCommit = selection;
        if (selectedId is null && selection is not null) selection.Details.IsExpanded = true;
        if (changed && IsHistory && !IsStash) _ = LoadCommitDetailsAsync(selection);
        Raise(nameof(SelectedCommit));
        Raise(nameof(VisibleCommitCount));
        Raise(nameof(HistoryPathSummary));
        Raise(nameof(HasSearch));
        Raise(nameof(SearchMatchCount));
        RaiseDetailProperties();
    }
    public bool HasSearch => !string.IsNullOrWhiteSpace(HistorySearch);
    public int SearchMatchCount => HasSearch ? Commits.Count(item => item.IsVisible && IsSearchMatch(item)) : 0;
    public bool IsSearchMatch(CommitItem item) => item.Commit.Subject.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Author.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Id.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Decorations.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase);

    public void SelectNextMatch()
    {
        if (!HasSearch) return;
        var matches = Commits.Where(item => item.IsVisible && IsSearchMatch(item)).ToArray();
        if (matches.Length == 0) return;
        var index = Array.IndexOf(matches, SelectedCommit);
        SelectedCommit = matches[(index + 1) % matches.Length];
    }

    private async Task LoadCommitDetailsAsync(CommitItem? item)
    {
        if (IsStash) return;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        var cancellation = _selectionCancellation = new CancellationTokenSource();
        IsLoadingDetails = false;
        _commitFiles = [];
        UpdateFiles();
        if (item is null || Repository is null || !IsHistory) return;
        IsLoadingDetails = true;
        try
        {
            await EnsureCommitFilesAsync(item).WaitAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || SelectedCommit?.Commit.Id != item.Commit.Id || !IsHistory) return;
            _commitFiles = item.Details.Files.Select(file => file.File).ToArray();
            UpdateFiles(forceDiff: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(cancellation, _selectionCancellation)) IsLoadingDetails = false; }
    }

    private void UpdateFiles(bool forceDiff = false)
    {
        var oldPath = IsHistory && SelectedCommit is not null
            ? _historyFileSelections.GetValueOrDefault(SelectedCommit.Commit.Id) : _workingSelectionPath;
        var filtered = IsStash ? _stashFiles : IsHistory ? SelectedCommit?.Details.Files.ToArray() ?? [] : WorkingFiles.ToArray();
        _updatingFiles = true;
        try
        {
            Files.Clear();
            foreach (var file in filtered) Files.Add(file);
        }
        finally { _updatingFiles = false; }
        var candidates = IsHistory && HasHistoryPathFilter ? filtered.Where(file => MatchesHistoryPath(file.File)).ToArray() : filtered;
        var selection = candidates.FirstOrDefault(file => file.Path == oldPath) ?? candidates.FirstOrDefault() ?? filtered.FirstOrDefault();
        if (Equals(SelectedFile, selection) && forceDiff) _ = LoadDiffAsync();
        else SelectedFile = selection;
        Raise(nameof(SelectedFile));
        Raise(nameof(FileCount));
    }

    private void UpdateWorkingFiles()
    {
        _updatingFiles = true;
        try
        {
            WorkingFiles.Clear();
            foreach (var file in _workingFiles.Where(file => file.DisplayPath.Contains(FileSearch, StringComparison.OrdinalIgnoreCase)))
                WorkingFiles.Add(new FileItem(file, IsFileIncluded(file.Path), InclusionChanged));
            _selectedWorkingFile = IsChanges ? WorkingFiles.FirstOrDefault(file => file.Path == _workingSelectionPath) : null;
        }
        finally { _updatingFiles = false; }
        Raise(nameof(SelectedWorkingFile));
    }

    private async Task LoadDiffAsync()
    {
        _diffCancellation?.Cancel();
        _diffCancellation?.Dispose();
        var cancellation = _diffCancellation = new CancellationTokenSource();
        IsLoadingDiff = false;
        Diff = null;
        Raise(nameof(DiffSourceLabel));
        if (SelectedFile is null || Repository is null) return;
        var file = SelectedFile.File;
        var stash = SelectedStash;
        var commit = IsHistory && !IsStash ? SelectedCommit?.Commit : null;
        if (IsHistory && !IsStash && commit is null) return;
        IsLoadingDiff = true;
        try
        {
            var patch = stash is not null ? await _git.GetStashDiffAsync(Repository.Path, stash.Id, file, cancellation.Token)
                : await _git.GetDiffAsync(Repository.Path, file, commit, ShowStagedDiff, cancellation.Token, includeAllChanges: IsChanges && !ShowStagedDiff);
            var document = await Task.Run(() => DiffParser.Parse(patch), cancellation.Token);
            if (!cancellation.IsCancellationRequested) Diff = document;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(cancellation, _diffCancellation)) IsLoadingDiff = false; }
    }

    public bool IsFileIncluded(string path) => Repository is not null
        && (!_excludedFiles.TryGetValue(Repository.Path, out var excluded) || !excluded.Contains(path));

    private void InclusionChanged(FileItem item)
    {
        if (Repository is null || item.File.IsHistory) return;
        if (!_excludedFiles.TryGetValue(Repository.Path, out var excluded))
            _excludedFiles[Repository.Path] = excluded = new HashSet<string>(StringComparer.Ordinal);
        if (item.IsIncluded) excluded.Remove(item.Path); else excluded.Add(item.Path);
        RaiseInclusionProperties();
    }

    public void SetAllChangesIncluded(bool include)
    {
        if (Repository is null || IsBusy) return;
        _excludedFiles[Repository.Path] = include ? new HashSet<string>(StringComparer.Ordinal)
            : _workingFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var item in WorkingFiles) item.IsIncluded = include;
        RaiseInclusionProperties();
    }

    private void ReconcileInclusions()
    {
        if (Repository is null || !_excludedFiles.TryGetValue(Repository.Path, out var excluded)) return;
        foreach (var file in _workingFiles)
            if (file.OldPath is not null && excluded.Contains(file.OldPath)) excluded.Add(file.Path);
        excluded.IntersectWith(_workingFiles.Select(file => file.Path));
    }

    private void RaiseInclusionProperties()
    {
        foreach (var name in new[] { nameof(IncludedCount), nameof(AllChangesIncluded), nameof(CommitButtonLabel) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public Task SwitchBranchAsync(BranchInfo branch) => MutateAsync("正在切换分支…", ct =>
    {
        var localName = branch.IsRemote && branch.Name.Contains('/') ? branch.Name[(branch.Name.IndexOf('/') + 1)..] : branch.Name;
        var local = Branches.FirstOrDefault(candidate => !candidate.IsRemote && candidate.Name == localName);
        return _git.SwitchAsync(Repository!.Path, local?.Name ?? branch.Name, ct, branch.IsRemote && local is null);
    }, $"已切换到 {branch.Name}");
    public Task CreateBranchAsync(string name) => MutateAsync("正在创建分支…", ct => _git.CreateBranchAsync(Repository!.Path, name, ct), $"已创建分支 {name}");

    private Task FetchAsync() => MutateAsync("正在获取远程更新…", async ct =>
    {
        _fetchedSyncContext = null;
        RaiseSyncProperties();
        await _git.FetchAsync(Repository!.Path, ct);
        MarkFetched();
    }, "已获取远程更新");
    private Task PullAsync() => MutateAsync("正在拉取更新（仅快进）…", async ct =>
    {
        await _git.PullAsync(Repository!.Path, ct);
        MarkFetched();
    }, "已拉取远程更新");
    private void MarkFetched()
    {
        if (Repository is null) return;
        _fetchedSyncContext = CurrentSyncContext;
        var fetched = DateTimeOffset.UtcNow;
        _fetchTimes[Repository.Path] = fetched;
        SetLastFetchTime(fetched);
    }
    private Task PushAsync() => MutateAsync("正在推送提交…", ct => _git.PushAsync(Repository!.Path, ct), "已推送到远程仓库");
    private Task ExecuteSyncAsync() => SyncState.Action switch
    {
        SyncAction.Resolve => RequestSyncResolutionAsync(),
        SyncAction.Pull => PullAsync(),
        SyncAction.Push or SyncAction.Publish => PushAsync(),
        _ => FetchAsync()
    };
    private Task CommitAsync() => MutateAsync("正在创建提交…", async ct =>
    {
        var selected = _workingFiles.Where(file => IsFileIncluded(file.Path)).ToArray();
        await _git.CommitSelectedAsync(Repository!.Path, selected, Summary.Trim(), Description.Trim(), ct);
        Summary = "";
        Description = "";
    }, "提交已创建");

    public async Task CloneAsync(string url, string destination)
    {
        var completed = false;
        await RunOperationAsync("正在克隆仓库…", async ct => { await _git.CloneAsync(url, destination, ct); completed = true; });
        if (completed) await OpenRepositoryAsync(destination);
    }

    private Task MutateAsync(string message, Func<CancellationToken, Task> action, string success) => RunOperationAsync(message, async ct =>
    {
        if (Repository is null) return;
        _selectionCancellation?.Cancel();
        _diffCancellation?.Cancel();
        try { await action(ct); }
        finally
        {
            // A cancelled Git process can still have completed a local write. Always reconcile its state.
            await RefreshDataAsync(CancellationToken.None);
        }
        Notice = success;
        OperationCompleted?.Invoke(success);
    });

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action, bool silent = false)
    {
        if (_disposed) return;
        // Passive refresh may be skipped; explicit user operations must wait instead of disappearing.
        using var lease = await Coordinator.EnterAsync(wait: !silent);
        if (lease is null || _disposed) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        if (!silent) { IsBusy = true; BusyMessage = message; }
        if (!silent) Error = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Notice = "操作已取消"; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            _operationCancellation = null;
            if (!silent) { IsBusy = false; BusyMessage = ""; }
        }
    }

    private void RaiseDetailProperties()
    {
        foreach (var name in new[] { nameof(HasCommit), nameof(DetailSubject), nameof(DetailBody), nameof(DetailAuthor), nameof(DetailDate), nameof(DetailSha), nameof(DetailShortSha), nameof(DetailInitials), nameof(ParentDescription), nameof(FileCount) }) Raise(name);
    }

    private void SaveSettings()
    {
        if (!_persistSettings) return;
        var error = _store.Save(new AppSettings(Repositories.Select(entry => entry.Path).ToList(), LastRepository, AllBranches, Repositories.ToList()));
        if (error is not null) Error = $"无法保存仓库列表：{error}";
    }

    private void RaiseSyncProperties()
    {
        foreach (var name in new[] { nameof(SyncState), nameof(SyncTitle), nameof(SyncGlyph), nameof(SyncBadge), nameof(HasSyncBadge), nameof(SyncSubtitle), nameof(SyncToolTip) }) Raise(name);
    }

    private void StartWatcher(string path)
    {
        try
        {
            _watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler change = (_, e) => QueueRefresh(e.FullPath);
            _watcher.Changed += change;
            _watcher.Created += change;
            _watcher.Deleted += change;
            _watcher.Renamed += (_, e) => QueueRefresh(e.FullPath);
            _watcher.Error += (_, _) => QueueRefresh(path);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice = "自动刷新不可用，可按 F5 手动刷新。"; }
    }

    private void QueueRefresh(string path)
    {
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || path.Contains("\\.git\\objects\\", StringComparison.OrdinalIgnoreCase)) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _fetchTimeTimer.Stop();
        SaveSettings();
        _refreshTimer.Stop();
        _watcher?.Dispose();
        _operationCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _diffCancellation?.Cancel();
        _pathFilterCancellation?.Cancel();
        _repositoryCancellation.Cancel();
    }
}
