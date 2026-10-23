using GitManager.App.Infrastructure;
using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed class FileItem(ChangedFile file, bool isIncluded, Action<FileItem>? onInclusionChanged = null) : ObservableObject
{
    private bool _isIncluded = isIncluded;
    private bool _isSelected;
    public ChangedFile File { get; } = file;
    public string Path => File.Path;
    public string DisplayPath => File.DisplayPath;
    public string Name => System.IO.Path.GetFileName(Path);
    public string DirectoryPath => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public char Status => File.Status;
    public string StatusLabel => File.StatusLabel;
    public bool HasStagedChanges => File.HasStagedChanges;
    public bool HasUnstagedChanges => File.HasUnstagedChanges;
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }
    public bool IsIncluded
    {
        get => _isIncluded;
        set { if (Set(ref _isIncluded, value)) onInclusionChanged?.Invoke(this); }
    }
}
