using GitManager.Core;

namespace GitManager.App.ViewModels;

public sealed record CommitItem(CommitInfo Commit, GraphRow Graph, double GraphWidth, bool IsVisible = true)
{
    public CommitDetails Details { get; init; } = new();
    public const double RowHeight = 22;
    public string Subject => IsVisible ? Commit.Subject : "";
    public string AuthorSuffix => IsVisible ? "  " + Commit.Author : "";
    public string AuthorDetails => Commit.Author;
    public string Timestamp => Commit.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    public string ParentSummary => Commit.Parents.Count == 0 ? "初始提交" : "父提交：" + string.Join("  ", Commit.Parents);
    public string Decorations => IsVisible ? Commit.Decorations.Replace("HEAD -> ", "● ", StringComparison.Ordinal) : "";
    public bool HasDecorations => !string.IsNullOrWhiteSpace(Decorations);
}
