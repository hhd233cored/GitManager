using System.Collections.ObjectModel;
using GitManager.App.Infrastructure;

namespace GitManager.App.ViewModels;

public sealed class RepositoryTreeItem(string name, string relativePath, bool isDirectory) : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    public string Name { get; } = name;
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = isDirectory;
    public ObservableCollection<RepositoryTreeItem> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public static RepositoryTreeItem Build(string name, IReadOnlyList<string> paths, ISet<string> expanded)
    {
        var root = new RepositoryTreeItem(name, "", true) { IsExpanded = true };
        var directories = new Dictionary<string, RepositoryTreeItem>(StringComparer.Ordinal) { [""] = root };
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            var parent = root;
            var relative = "";
            for (var i = 0; i < parts.Length; i++)
            {
                relative = relative.Length == 0 ? parts[i] : relative + "/" + parts[i];
                if (i < parts.Length - 1)
                {
                    if (!directories.TryGetValue(relative, out var folder))
                    {
                        folder = new RepositoryTreeItem(parts[i], relative, true) { IsExpanded = expanded.Contains(relative) };
                        directories[relative] = folder;
                        parent.Children.Add(folder);
                    }
                    parent = folder;
                }
                else parent.Children.Add(new RepositoryTreeItem(parts[i], path, false));
            }
        }
        foreach (var folder in directories.Values)
        {
            var sorted = folder.Children.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            folder.Children.Clear();
            foreach (var child in sorted) folder.Children.Add(child);
        }
        return root;
    }

    public IEnumerable<RepositoryTreeItem> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.DescendantsAndSelf()) yield return item;
    }
}
