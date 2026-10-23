namespace GitManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private CommitDetails GetCommitDetails(string id)
    {
        if (!_commitDetails.TryGetValue(id, out var details)) _commitDetails[id] = details = new CommitDetails();
        return details;
    }

    public Task EnsureCommitFilesAsync(CommitItem item)
    {
        if (Repository is null || item.Details.FilesLoaded) return Task.CompletedTask;
        if (item.Details.FilesTask?.IsCompleted == true) item.Details.FilesTask = null;
        return item.Details.FilesTask ??= LoadAsync();

        async Task LoadAsync()
        {
            var details = item.Details;
            var path = Repository.Path;
            var token = _repositoryCancellation.Token;
            details.IsLoadingFiles = true;
            details.FilesError = "";
            try
            {
                var files = await _git.GetCommitFilesAsync(path, item.Commit, token);
                token.ThrowIfCancellationRequested();
                details.Files = files.Select(file => new FileItem(file, false)).ToArray();
                details.FilesLoaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { details.FilesError = $"读取失败：{ex.Message}"; }
            finally { details.IsLoadingFiles = false; details.FilesTask = null; }
        }
    }

    public Task EnsureCommitStatsAsync(CommitItem item)
    {
        if (Repository is null || item.Details.HasStats) return Task.CompletedTask;
        if (item.Details.StatsTask?.IsCompleted == true) item.Details.StatsTask = null;
        return item.Details.StatsTask ??= LoadAsync();

        async Task LoadAsync()
        {
            var details = item.Details;
            var path = Repository.Path;
            var token = _repositoryCancellation.Token;
            details.IsLoadingStats = true;
            details.StatsError = "";
            try
            {
                var stats = await _git.GetCommitStatsAsync(path, item.Commit, token);
                token.ThrowIfCancellationRequested();
                details.Stats = stats;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { details.StatsError = "暂时无法读取变更统计，下次悬停时重试。"; }
            finally { details.IsLoadingStats = false; details.StatsTask = null; }
        }
    }

    public async Task ExpandCommitAsync(CommitItem item)
    {
        item.Details.IsExpanded = true;
        await EnsureCommitFilesAsync(item);
    }

    public void SelectHistoryFile(CommitItem item, FileItem file)
    {
        if (!item.IsVisible) return;
        _historyFileSelections[item.Commit.Id] = file.Path;
        SelectedCommit = item;
        SelectedFile = file;
    }
}
