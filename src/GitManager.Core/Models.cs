namespace GitManager.Core;

public sealed record CommitInfo(string Id, IReadOnlyList<string> Parents, string Author,
    string Email, DateTimeOffset Date, string Subject, string Body, string Decorations)
{
    public string ShortId => Id[..Math.Min(8, Id.Length)];
    public string Initials => string.Concat(Author.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(2).Select(word => word[..1])).ToUpperInvariant() is { Length: > 0 } value ? value : "G";
}

public sealed record RepositoryInfo(string Path, string Branch, string? Upstream, int Ahead, int Behind,
    bool HasHead, bool HasRemote)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
}

public sealed record BranchInfo(string Name, bool IsCurrent, bool IsRemote);

public sealed record ChangedFile(string Path, string? OldPath, char IndexStatus, char WorkTreeStatus,
    bool IsHistory = false)
{
    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';
    public bool HasStagedChanges => !IsHistory && IndexStatus is not (' ' or '?' or '!');
    public bool HasUnstagedChanges => !IsHistory && (IsUntracked || WorkTreeStatus != ' ');
    public bool? StageState => HasStagedChanges ? HasUnstagedChanges ? null : true : false;
    public char Status => IsHistory ? IndexStatus : IsUntracked ? 'A' : WorkTreeStatus != ' ' ? WorkTreeStatus : IndexStatus;
    public string StatusLabel => Status switch
    {
        'A' => "新增", 'D' => "删除", 'R' => "重命名", 'C' => "复制", 'U' => "冲突", 'T' => "类型变更", _ => "修改"
    };
    public string DisplayPath => OldPath is null ? Path : $"{OldPath} → {Path}";
}

public sealed record GitResult(int ExitCode, string Output, string Error);

public sealed class GitException(string message, int exitCode = -1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
