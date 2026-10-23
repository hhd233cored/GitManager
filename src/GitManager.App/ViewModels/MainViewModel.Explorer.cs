using System.Collections.ObjectModel;
using System.Windows.Input;
using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _pathFilterCancellation;
    private IReadOnlyList<string> _repositoryFilePaths = [];
    private IReadOnlySet<string>? _pathCommitIds;
    private string _historyPath = "";
    private bool _historyPathIsDirectory = true;
    private bool _isFilteringHistory;
    public ObservableCollection<RepositoryTreeItem> RepositoryTree { get; } = [];
    public bool IsUpdatingExplorer { get; private set; }
    public bool HasHistoryPathFilter => _historyPath.Length > 0;
    public string HistoryPath => _historyPath;
    public string HistoryPathLabel => !HasHistoryPathFilter ? "所有文件" : _historyPath + (_historyPathIsDirectory ? "/" : "");
    public int VisibleCommitCount => Commits.Count(item => item.IsVisible);
    public string HistoryPathSummary => IsFilteringHistory ? "正在筛选路径…" : $"{VisibleCommitCount} / {CommitCount} 个提交 · 保留行占位";
    public bool IsFilteringHistory { get => _isFilteringHistory; private set { Set(ref _isFilteringHistory, value); Raise(nameof(HistoryPathSummary)); } }

    private void UpdateRepositoryTree(IReadOnlyList<string> paths)
    {
        if (RepositoryTree.Count > 0 && RepositoryTree[0].Name == RepositoryName && _repositoryFilePaths.SequenceEqual(paths, StringComparer.Ordinal)) return;
        var expanded = RepositoryTree.SelectMany(root => root.DescendantsAndSelf()).Where(item => item.IsExpanded)
            .Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        _repositoryFilePaths = paths;
        var root = RepositoryTreeItem.Build(RepositoryName, paths, expanded);
        foreach (var item in root.DescendantsAndSelf()) item.IsSelected = HasHistoryPathFilter && item.RelativePath == _historyPath;
        IsUpdatingExplorer = true;
        try { RepositoryTree.Clear(); RepositoryTree.Add(root); }
        finally { IsUpdatingExplorer = false; }
    }

    public async Task SelectHistoryPathAsync(RepositoryTreeItem item)
    {
        if (IsUpdatingExplorer || Repository is null) return;
        if (item.RelativePath == _historyPath && item.IsDirectory == _historyPathIsDirectory) return;
        IsHistory = true;
        _historyPath = item.RelativePath;
        _historyPathIsDirectory = item.IsDirectory;
        _pathCommitIds = null;
        RaiseHistoryPathProperties();
        await RefreshHistoryPathAsync();
    }

    public void ClearHistoryPath()
    {
        _pathFilterCancellation?.Cancel();
        _historyPath = "";
        _historyPathIsDirectory = true;
        _pathCommitIds = null;
        IsFilteringHistory = false;
        foreach (var item in RepositoryTree.SelectMany(root => root.DescendantsAndSelf())) item.IsSelected = item.RelativePath.Length == 0;
        RaiseHistoryPathProperties();
        UpdateCommitFilter();
    }

    private async Task RefreshHistoryPathAsync(CancellationToken ct = default)
    {
        _pathFilterCancellation?.Cancel();
        _pathFilterCancellation?.Dispose();
        var cancellation = _pathFilterCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!HasHistoryPathFilter || Repository is null)
        {
            _pathCommitIds = null;
            IsFilteringHistory = false;
            UpdateCommitFilter();
            return;
        }
        var selectedId = SelectedCommit?.Commit.Id;
        IsFilteringHistory = true;
        _pathCommitIds ??= new HashSet<string>(StringComparer.Ordinal);
        UpdateCommitFilter();
        try
        {
            var matches = await _git.GetPathCommitIdsAsync(Repository.Path, _allCommits.Select(item => item.Commit).ToArray(), _historyPath, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _pathCommitIds = matches;
            UpdateCommitFilter(selectedId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(_pathFilterCancellation, cancellation)) IsFilteringHistory = false; }
    }

    private bool MatchesHistoryPath(ChangedFile file)
    {
        bool Matches(string? path) => path is not null && (path == _historyPath || _historyPathIsDirectory && path.StartsWith(_historyPath + "/", StringComparison.Ordinal));
        return Matches(file.Path) || Matches(file.OldPath);
    }

    private void RaiseHistoryPathProperties()
    {
        foreach (var property in new[] { nameof(HistoryPath), nameof(HistoryPathLabel), nameof(HasHistoryPathFilter), nameof(HistoryPathSummary) }) Raise(property);
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearExplorer()
    {
        _pathFilterCancellation?.Cancel();
        _repositoryFilePaths = [];
        _historyPath = "";
        _historyPathIsDirectory = true;
        _pathCommitIds = null;
        IsFilteringHistory = false;
        RepositoryTree.Clear();
        RaiseHistoryPathProperties();
    }
}
