using GitManager.Services;

namespace GitManager.App.ViewModels;

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> GetIncludedFilePaths() => _workingFiles.Where(file => IsFileIncluded(file.Path)).Select(file => file.Path).ToArray();
    public void OnAssistantRepositoryUpdated(string repositoryId, bool fetched)
    {
        if (RepositoryTools.Context?.Id != repositoryId || _disposed) return;
        if (fetched) MarkFetched();
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }
    public async Task<OperationPlan> ExecuteAssistantPlanAsync(OperationPlan plan)
    {
        if (IsBusy || _disposed) throw new InvalidOperationException("请等待当前操作完成。");
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        BusyMessage = "正在执行已确认的操作…";
        try
        {
            var result = await RepositoryTools.ExecuteApprovedPlanAsync(plan.RepositoryId, plan.Id, cancellation.Token);
            Notice = result.Result ?? result.Status;
            return result;
        }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            BusyMessage = "";
            await RefreshAsync(silent: true);
        }
    }
}
