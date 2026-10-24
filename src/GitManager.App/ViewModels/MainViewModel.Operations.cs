using System.Collections.ObjectModel;
using System.Windows.Input;
using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed partial class MainViewModel
{
    private RepositoryOperation _currentOperation = new(RepositoryOperationKind.None, []);
    private StashEntry? _selectedStash;
    private FileItem[] _stashFiles = [];
    public ObservableCollection<StashEntry> Stashes { get; } = [];
    public ObservableCollection<RecoveryPoint> RecoveryPoints { get; } = [];
    public event Action? SyncResolutionRequested;
    public StashEntry? SelectedStash => _selectedStash;
    public bool IsStash => SelectedStash is not null;
    public string StashLabel => Stashes.Count == 0 ? "Stash" : $"Stash ({Stashes.Count})";
    public RepositoryOperation CurrentOperation
    {
        get => _currentOperation;
        private set { if (Set(ref _currentOperation, value)) RaiseOperationProperties(); }
    }
    public bool HasActiveOperation => CurrentOperation.IsActive || CurrentOperation.HasConflicts;
    public bool CanStartOperation => HasRepository && !IsBusy && !HasActiveOperation;
    public bool CanContinueOperation => !IsBusy && CurrentOperation.CanContinue;
    public bool CanResolveSelectedFile => !IsBusy && IsChanges && SelectedFile?.File.IsConflicted == true;
    public string OperationLabel => CurrentOperation.Label + (CurrentOperation.HasConflicts ? $" · {CurrentOperation.Conflicts.Count} 个冲突文件" : "");
    public string ContinueOperationLabel => CurrentOperation.Kind == RepositoryOperationKind.StashApply ? "完成恢复" : CurrentOperation.Kind == RepositoryOperationKind.RestoreChanges ? "恢复保存的改动" : "继续";
    public string AbortOperationLabel => CurrentOperation.Kind == RepositoryOperationKind.RestoreChanges ? "保留为 Stash" : "中止";
    private Task RequestSyncResolutionAsync() { SyncResolutionRequested?.Invoke(); return Task.CompletedTask; }
    private void RaiseOperationProperties()
    {
        foreach (var name in new[] { nameof(HasActiveOperation), nameof(CanStartOperation), nameof(CanContinueOperation), nameof(CanResolveSelectedFile), nameof(OperationLabel), nameof(ContinueOperationLabel), nameof(AbortOperationLabel) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void ClearStashPreview()
    {
        if (_selectedStash is null) return;
        _selectedStash = null;
        _stashFiles = [];
        Raise(nameof(SelectedStash));
        Raise(nameof(IsStash));
        RaiseDetailProperties();
        Raise(nameof(DiffSourceLabel));
    }
    public async Task ViewStashAsync(StashEntry entry)
    {
        if (Repository is null) return;
        var path = Repository.Path;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        var cancellation = _selectionCancellation = new CancellationTokenSource();
        var oldPath = SelectedFile?.Path;
        _selectedStash = entry;
        if (!IsHistory) IsHistory = true;
        Raise(nameof(SelectedStash));
        Raise(nameof(IsStash));
        RaiseDetailProperties();
        IsLoadingDetails = true;
        try
        {
            var files = await _git.GetStashFilesAsync(path, entry.Id, cancellation.Token);
            if (cancellation.IsCancellationRequested || Repository?.Path != path || SelectedStash?.Id != entry.Id) return;
            _stashFiles = files.Select(file => new FileItem(file, false)).ToArray();
            UpdateFiles(forceDiff: true);
            SelectedFile = Files.FirstOrDefault(file => file.Path == oldPath) ?? Files.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = ex.Message; }
        finally { if (ReferenceEquals(cancellation, _selectionCancellation)) IsLoadingDetails = false; }
    }
    public Task SaveStashAsync(string message, bool untracked, bool selectedOnly) => MutateAsync("正在保存改动…", ct =>
        _git.CreateStashAsync(Repository!.Path, message, untracked, selectedOnly
            ? _workingFiles.Where(file => IsFileIncluded(file.Path)).SelectMany(file => file.OldPath is null ? new[] { file.Path } : new[] { file.OldPath, file.Path }).Distinct(StringComparer.Ordinal).ToArray() : null, ct), "改动已保存到 Stash");
    public Task ApplyStashAsync(StashEntry entry, bool drop, bool index) => MutateAsync("正在恢复 Stash…", ct => _git.ApplyStashAsync(Repository!.Path, entry.Id, drop, index, ct), "Stash 已恢复");
    public Task DeleteStashAsync(StashEntry entry) => MutateAsync("正在删除 Stash…", ct => _git.DropStashAsync(Repository!.Path, entry.Id, ct), "Stash 已删除");
    public Task RestoreRecoveryAsync(RecoveryPoint point) => MutateAsync("正在恢复备份…", ct => _git.RestoreRecoveryAsync(Repository!.Path, point, ct), "备份已恢复，远程分支未修改");
    public Task ContinueOperationAsync() => MutateAsync("正在继续操作…", ct => _git.ContinueOperationAsync(Repository!.Path, ct), "操作已继续");
    public Task AbortOperationAsync() => MutateAsync("正在中止操作…", ct => _git.AbortOperationAsync(Repository!.Path, ct), "操作已中止");
    public async Task<OperationPreview?> PreviewOperationAsync(string target, string title)
    {
        OperationPreview? result = null;
        await RunOperationAsync("正在准备操作预览…", async ct => { if (Repository is not null) result = await _git.PreviewHistoryOperationAsync(Repository.Path, target, title, ct); });
        return result;
    }
    public async Task<OperationPreview?> PreviewSynchronizationAsync()
    {
        OperationPreview? result = null;
        await RunOperationAsync("正在获取远程版本并比较…", async ct =>
        {
            if (Repository is null) return;
            result = await _git.PreviewSynchronizationAsync(Repository.Path, ct);
            MarkFetched();
            await RefreshDataAsync(ct);
        });
        return result;
    }
    public Task ResetAsync(OperationPreview preview, ResetMode mode) => MutateAsync("正在重置分支…", ct => _git.ResetAsync(Repository!.Path, preview, mode, ct), "分支已重置，原版本已保留恢复点");
    public Task RebaseAsync(OperationPreview preview) => MutateAsync("正在变基…", ct => _git.RebaseAsync(Repository!.Path, preview, ct), "变基已完成");
    public Task RevertAsync(OperationPreview preview, int mainline) => MutateAsync("正在撤销提交…", ct => _git.RevertAsync(Repository!.Path, preview, mainline, ct), "已创建撤销提交");
    public Task SynchronizeAsync(OperationPreview preview, SynchronizationMode mode) => MutateAsync("正在同步分支…", ct => _git.SynchronizeAsync(Repository!.Path, preview, mode, ct), "同步已完成");
    public async Task<ConflictContent?> ReadConflictAsync()
    {
        ConflictContent? result = null;
        await RunOperationAsync("正在读取冲突版本…", async ct =>
        {
            if (Repository is not null && SelectedFile is not null) result = await _git.GetConflictAsync(Repository.Path, SelectedFile.Path, ct);
        });
        return result;
    }
    public Task ResolveConflictAsync(string path, ConflictResolution resolution, string? edited) => MutateAsync("正在保存冲突处理结果…",
        ct => _git.ResolveConflictAsync(Repository!.Path, path, resolution, edited, ct), "文件已标记为解决");
}
