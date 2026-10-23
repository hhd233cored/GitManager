using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed record CommitItem(CommitInfo Commit, GraphRow Graph, double GraphWidth)
{
    public string Subject => Commit.Subject;
    public string Decorations => Commit.Decorations.Replace("HEAD -> ", "● ", StringComparison.Ordinal);
    public bool HasDecorations => !string.IsNullOrWhiteSpace(Decorations);
}
