using GitManager.App.Infrastructure;
using GitManager.Core;

namespace GitManager.App.ViewModels;

// Shared by graph-row snapshots, so filtering and refresh preserve expanded files.
public sealed class CommitDetails : ObservableObject
{
    private bool _isExpanded;
    private bool _isLoadingFiles;
    private bool _isLoadingStats;
    private IReadOnlyList<FileItem> _files = [];
    private CommitStats? _stats;
    private string _filesError = "";
    private string _statsError = "";
    internal Task? FilesTask { get; set; }
    internal Task? StatsTask { get; set; }
    internal bool FilesLoaded { get; set; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsLoadingFiles { get => _isLoadingFiles; internal set => Set(ref _isLoadingFiles, value); }
    public bool IsLoadingStats { get => _isLoadingStats; internal set => Set(ref _isLoadingStats, value); }
    public IReadOnlyList<FileItem> Files { get => _files; internal set => Set(ref _files, value); }
    public string FilesError { get => _filesError; internal set => Set(ref _filesError, value); }
    public string StatsError { get => _statsError; internal set => Set(ref _statsError, value); }
    public CommitStats? Stats
    {
        get => _stats;
        internal set
        {
            if (!Set(ref _stats, value)) return;
            Raise(nameof(HasStats));
            Raise(nameof(StatsSummary));
        }
    }
    public bool HasStats => Stats is not null;
    public string StatsSummary => Stats is null ? "" : $"{Stats.FileCount} 个变更文件"
        + (Stats.BinaryFileCount > 0 ? $" · 含 {Stats.BinaryFileCount} 个二进制文件" : "");
}
