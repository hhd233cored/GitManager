using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using GitManager.App.Infrastructure;
using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly GitService _git = new();
    private readonly SettingsStore _store = new();
    private readonly bool _persistSettings;
    private bool _updatingCommits;
    private bool _updatingFiles;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _diffCancellation;
    private FileSystemWatcher? _watcher;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private bool _disposed;
    private RepositoryInfo? _repository;
    private bool _isBusy;
    private string _busyMessage = "";
    private string _error = "";
    private string _notice = "打开一个仓库，开始查看提交历史。";
    private bool _isHistory = true;
    private bool _allBranches;
    private bool _isSplitDiff = true;
    private bool _showStagedDiff;
    private string _historySearch = "";
    private string _fileSearch = "";
    private string _summary = "";
    private string _description = "";
    private string _lastFetched = "";
    private int _historyLimit = 300;
    private bool _canLoadMore;
    private CommitItem? _selectedCommit;
    private ChangedFile? _selectedFile;
    private DiffDocument? _diff;
    private bool _isLoadingDetails;
    private bool _isLoadingDiff;
    private IReadOnlyList<CommitItem> _allCommits = [];
    private IReadOnlyList<ChangedFile> _workingFiles = [];
    private IReadOnlyList<ChangedFile> _commitFiles = [];
    private readonly Dictionary<string, string> _historyFileSelections = [];
    private string? _workingSelectionPath;

    public MainViewModel(bool persistSettings = true)
    {
        _persistSettings = persistSettings;
        var settings = persistSettings ? _store.Load() : new AppSettings([], null);
        _allBranches = settings.AllBranches;
        foreach (var path in settings.RecentRepositories.Where(Directory.Exists).Take(10)) RecentRepositories.Add(path);
        LastRepository = settings.LastRepository;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => HasRepository && !IsBusy);
        FetchCommand = new AsyncCommand(FetchAsync, () => HasRepository && Repository!.HasRemote && !IsBusy);
        PullCommand = new AsyncCommand(PullAsync, () => HasRepository && Repository!.Upstream is not null && !IsBusy);
        PushCommand = new AsyncCommand(PushAsync, () => HasRepository && Repository!.HasRemote && Repository.HasHead && !IsBusy);
        CommitCommand = new AsyncCommand(CommitAsync, () => HasRepository && !IsBusy && StagedCount > 0 && !string.IsNullOrWhiteSpace(Summary));
        StageAllCommand = new AsyncCommand(() => StageAsync(null), () => HasRepository && !IsBusy && _workingFiles.Any(file => file.HasUnstagedChanges));
        UnstageAllCommand = new AsyncCommand(() => UnstageAsync(null), () => HasRepository && !IsBusy && StagedCount > 0);
        LoadMoreCommand = new AsyncCommand(async () => { _historyLimit += 300; await RefreshAsync(); }, () => HasRepository && CanLoadMore && !IsBusy);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => IsBusy);
        DismissErrorCommand = new RelayCommand(() => Error = "");
        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            if (IsBusy) { _refreshTimer.Start(); return; }
            if (HasRepository) await RefreshAsync(silent: true);
        };
    }

    public event Action<string>? OperationCompleted;
    public string? LastRepository { get; }
    public ObservableCollection<string> RecentRepositories { get; } = [];
    public ObservableCollection<CommitItem> Commits { get; } = [];
    public ObservableCollection<ChangedFile> Files { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DismissErrorCommand { get; }

    public RepositoryInfo? Repository
    {
        get => _repository;
        private set
        {
            if (!Set(ref _repository, value)) return;
            foreach (var name in new[] { nameof(HasRepository), nameof(RepositoryName), nameof(BranchName), nameof(SyncStatus), nameof(PushLabel), nameof(CommitButtonLabel), nameof(RepositoryPath) }) Raise(name);
        }
    }
    public bool HasRepository => Repository is not null;
    public string RepositoryName => Repository?.Name ?? "打开本地仓库";
    public string RepositoryPath => Repository?.Path ?? "选择文件夹或克隆远程仓库";
    public string BranchName => Repository?.Branch ?? "未选择仓库";
    public string SyncStatus => Repository is null ? "本地 Git 客户端" : Repository.Upstream is null ? "尚未跟踪远程分支" : $"↑ {Repository.Ahead}   ↓ {Repository.Behind}  ·  {Repository.Upstream}";
    public string PushLabel => Repository?.Upstream is null ? "发布分支" : "Push";
    public string LastFetched { get => _lastFetched; private set => Set(ref _lastFetched, value); }
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }
    public string Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsHistory
    {
        get => _isHistory;
        set
        {
            if (!Set(ref _isHistory, value)) return;
            Raise(nameof(IsChanges));
            FileSearch = "";
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
    public bool IsSplitDiff { get => _isSplitDiff; set => Set(ref _isSplitDiff, value); }
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
    public string FileSearch { get => _fileSearch; set { if (Set(ref _fileSearch, value)) UpdateFiles(); } }
    public string Summary { get => _summary; set { Set(ref _summary, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Description { get => _description; set => Set(ref _description, value); }
    public int ChangeCount => _workingFiles.Count;
    public int StagedCount => _workingFiles.Count(file => file.HasStagedChanges);
    public int CommitCount => _allCommits.Count;
    public int FileCount => IsHistory ? _commitFiles.Count : _workingFiles.Count;
    public string CommitButtonLabel => $"提交到 {BranchName}";
    public bool CanLoadMore { get => _canLoadMore; private set => Set(ref _canLoadMore, value); }
    public bool IsLoadingDetails { get => _isLoadingDetails; private set => Set(ref _isLoadingDetails, value); }
    public bool IsLoadingDiff { get => _isLoadingDiff; private set => Set(ref _isLoadingDiff, value); }
    public bool HasCommit => IsHistory && SelectedCommit is not null;
    public string DetailSubject => IsHistory ? SelectedCommit?.Commit.Subject ?? "选择一条提交" : "工作区变更";
    public string DetailBody => IsHistory ? SelectedCommit?.Commit.Body ?? "" : "选择文件查看变更，勾选文件暂存到下一次提交。";
    public string DetailAuthor => SelectedCommit?.Commit.Author ?? "";
    public string DetailEmail => SelectedCommit?.Commit.Email ?? "";
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
    public bool CanChooseDiffSource => IsChanges && SelectedFile is { HasStagedChanges: true, HasUnstagedChanges: true };
    public string DiffSourceLabel => IsHistory ? "提交变更" : ShowStagedDiff ? "已暂存的变更" : "工作区变更";
    public CommitItem? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (_updatingCommits) return;
            if (!Set(ref _selectedCommit, value)) return;
            RaiseDetailProperties();
            if (IsHistory) _ = LoadCommitDetailsAsync(value);
        }
    }
    public ChangedFile? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_updatingFiles) return;
            if (!Set(ref _selectedFile, value)) return;
            if (value is not null)
            {
                if (IsHistory && SelectedCommit is not null) _historyFileSelections[SelectedCommit.Commit.Id] = value.Path;
                else if (IsChanges) _workingSelectionPath = value.Path;
            }
            if (value is not null && IsChanges) _showStagedDiff = value.HasStagedChanges && !value.HasUnstagedChanges;
            foreach (var name in new[] { nameof(FileTitle), nameof(FileStatus), nameof(CanChooseDiffSource), nameof(ShowStagedDiff), nameof(DiffSourceLabel) }) Raise(name);
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

    public async Task OpenRepositoryAsync(string path)
    {
        await RunOperationAsync("正在打开仓库…", async ct =>
        {
            var repository = await _git.GetRepositoryAsync(path, ct);
            _selectionCancellation?.Cancel();
            _diffCancellation?.Cancel();
            _watcher?.Dispose();
            _historyLimit = 300;
            _allCommits = [];
            _commitFiles = [];
            _workingFiles = [];
            _selectedCommit = null;
            _selectedFile = null;
            _updatingCommits = _updatingFiles = true;
            try { Commits.Clear(); Files.Clear(); Branches.Clear(); }
            finally { _updatingCommits = _updatingFiles = false; }
            _historyFileSelections.Clear();
            _workingSelectionPath = null;
            HistorySearch = "";
            FileSearch = "";
            Summary = "";
            Description = "";
            LastFetched = "";
            Repository = repository;
            Raise(nameof(SelectedCommit));
            Raise(nameof(SelectedFile));
            RaiseDetailProperties();
            Diff = null;
            await RefreshDataAsync(ct);
            RecentRepositories.Remove(repository.Path);
            RecentRepositories.Insert(0, repository.Path);
            while (RecentRepositories.Count > 10) RecentRepositories.RemoveAt(RecentRepositories.Count - 1);
            SaveSettings();
            StartWatcher(repository.Path);
            Notice = $"已打开 {repository.Name}";
        });
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
        var historyTask = _git.GetHistoryAsync(path, AllBranches, _historyLimit + 1, ct);
        var filesTask = _git.GetStatusAsync(path, ct);
        var branchesTask = _git.GetBranchesAsync(path, ct);
        await Task.WhenAll(infoTask, historyTask, filesTask, branchesTask);
        Repository = await infoTask;
        var history = await historyTask;
        CanLoadMore = history.Count > _historyLimit;
        var shown = history.Take(_historyLimit).ToArray();
        var layout = GraphLayout.Build(shown);
        var width = Math.Max(40, layout.LaneCount * 16 + 20);
        _allCommits = shown.Select((commit, i) => new CommitItem(commit, layout.Rows[i], width)).ToArray();
        _workingFiles = await filesTask;
        Branches.Clear();
        foreach (var branch in await branchesTask) Branches.Add(branch);
        var previousId = SelectedCommit?.Commit.Id;
        var updatedSelection = _allCommits.FirstOrDefault(item => item.Commit.Id == previousId) ?? _allCommits.FirstOrDefault();
        _selectedCommit = updatedSelection;
        _updatingCommits = true;
        try
        {
            Commits.Clear();
            foreach (var item in _allCommits) Commits.Add(item);
        }
        finally { _updatingCommits = false; }
        UpdateCommitFilter();
        Raise(nameof(SelectedCommit));
        Raise(nameof(CommitCount));
        Raise(nameof(ChangeCount));
        Raise(nameof(StagedCount));
        RaiseDetailProperties();
        if (IsHistory) await LoadCommitDetailsAsync(SelectedCommit);
        else UpdateFiles(forceDiff: true);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateCommitFilter()
    {
        // Keep graph topology intact while searching: matching commits are highlighted in the same history.
        Raise(nameof(HasSearch));
        Raise(nameof(SearchMatchCount));
        if (!string.IsNullOrWhiteSpace(HistorySearch))
        {
            var match = _allCommits.FirstOrDefault(IsSearchMatch);
            if (match is not null) SelectedCommit = match;
        }
    }
    public bool HasSearch => !string.IsNullOrWhiteSpace(HistorySearch);
    public int SearchMatchCount => HasSearch ? _allCommits.Count(IsSearchMatch) : 0;
    public bool IsSearchMatch(CommitItem item) => item.Commit.Subject.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Author.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Id.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase)
        || item.Commit.Decorations.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase);

    public void SelectNextMatch()
    {
        if (!HasSearch) return;
        var matches = _allCommits.Where(IsSearchMatch).ToArray();
        if (matches.Length == 0) return;
        var index = Array.IndexOf(matches, SelectedCommit);
        SelectedCommit = matches[(index + 1) % matches.Length];
    }

    private async Task LoadCommitDetailsAsync(CommitItem? item)
    {
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
            var files = await _git.GetCommitFilesAsync(Repository.Path, item.Commit, cancellation.Token);
            if (cancellation.IsCancellationRequested || SelectedCommit?.Commit.Id != item.Commit.Id || !IsHistory) return;
            _commitFiles = files;
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
        var source = IsHistory ? _commitFiles : _workingFiles;
        var filtered = source.Where(file => file.DisplayPath.Contains(FileSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        _updatingFiles = true;
        try
        {
            Files.Clear();
            foreach (var file in filtered) Files.Add(file);
        }
        finally { _updatingFiles = false; }
        var selection = filtered.FirstOrDefault(file => file.Path == oldPath) ?? filtered.FirstOrDefault();
        if (Equals(SelectedFile, selection) && forceDiff) _ = LoadDiffAsync();
        else SelectedFile = selection;
        Raise(nameof(SelectedFile));
        Raise(nameof(FileCount));
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
        var file = SelectedFile;
        var commit = IsHistory ? SelectedCommit?.Commit : null;
        if (IsHistory && commit is null) return;
        IsLoadingDiff = true;
        try
        {
            var patch = await _git.GetDiffAsync(Repository.Path, file, commit, ShowStagedDiff, cancellation.Token);
            var document = await Task.Run(() => DiffParser.Parse(patch), cancellation.Token);
            if (!cancellation.IsCancellationRequested) Diff = document;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(cancellation, _diffCancellation)) IsLoadingDiff = false; }
    }

    public Task ToggleStageAsync(ChangedFile file) => file.HasStagedChanges && !file.HasUnstagedChanges ? UnstageAsync(file) : StageAsync(file);
    public Task StageAsync(ChangedFile? file) => MutateAsync("正在暂存变更…", ct => _git.StageAsync(Repository!.Path, file, ct), "已暂存所选变更");
    public Task UnstageAsync(ChangedFile? file) => MutateAsync("正在取消暂存…", ct => _git.UnstageAsync(Repository!.Path, file, ct), "已取消暂存，工作区文件已保留");
    public Task SwitchBranchAsync(BranchInfo branch) => MutateAsync("正在切换分支…", ct =>
    {
        var localName = branch.IsRemote && branch.Name.Contains('/') ? branch.Name[(branch.Name.IndexOf('/') + 1)..] : branch.Name;
        var local = Branches.FirstOrDefault(candidate => !candidate.IsRemote && candidate.Name == localName);
        return _git.SwitchAsync(Repository!.Path, local?.Name ?? branch.Name, ct, branch.IsRemote && local is null);
    }, $"已切换到 {branch.Name}");
    public Task CreateBranchAsync(string name) => MutateAsync("正在创建分支…", ct => _git.CreateBranchAsync(Repository!.Path, name, ct), $"已创建分支 {name}");

    private Task FetchAsync() => MutateAsync("正在获取远程更新…", async ct =>
    {
        await _git.FetchAsync(Repository!.Path, ct);
        LastFetched = $"上次获取 {DateTime.Now:HH:mm}";
    }, "已获取远程更新");
    private Task PullAsync() => MutateAsync("正在拉取更新（仅快进）…", ct => _git.PullAsync(Repository!.Path, ct), "已拉取远程更新");
    private Task PushAsync() => MutateAsync("正在推送提交…", ct => _git.PushAsync(Repository!.Path, ct), "已推送到远程仓库");
    private Task CommitAsync() => MutateAsync("正在创建提交…", async ct =>
    {
        await _git.CommitAsync(Repository!.Path, Summary.Trim(), Description.Trim(), ct);
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
        if (_disposed || !await _operationLock.WaitAsync(0)) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        BusyMessage = message;
        if (!silent) Error = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Notice = "操作已取消"; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            BusyMessage = "";
            _operationLock.Release();
        }
    }

    private void RaiseDetailProperties()
    {
        foreach (var name in new[] { nameof(HasCommit), nameof(DetailSubject), nameof(DetailBody), nameof(DetailAuthor), nameof(DetailEmail), nameof(DetailDate), nameof(DetailSha), nameof(DetailShortSha), nameof(DetailInitials), nameof(ParentDescription), nameof(FileCount) }) Raise(name);
    }

    private void SaveSettings()
    {
        if (_persistSettings) _store.Save(new AppSettings(RecentRepositories.ToList(), Repository?.Path ?? LastRepository, AllBranches));
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
        SaveSettings();
        _refreshTimer.Stop();
        _watcher?.Dispose();
        _operationCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _diffCancellation?.Cancel();
    }
}
