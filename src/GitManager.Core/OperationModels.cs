namespace GitManager.Core;

public enum RepositoryOperationKind { None, Rebase, Merge, Revert, CherryPick, StashApply, RestoreChanges }
public enum ResetMode { Soft, Mixed, Hard }
public enum SynchronizationMode { Normal, Merge, Rebase, Remote, Local }
public enum ConflictResolution { Edited, Current, Incoming }
public sealed record StashEntry(string Id, string Selector, string Message, DateTimeOffset Date);
public sealed record RecoveryPoint(string Id, string Reference, string Branch, string Head, string? StashId, string Reason, DateTimeOffset Date);
public sealed record RepositoryOperation(RepositoryOperationKind Kind, IReadOnlyList<string> Conflicts, string? RecoveryReference = null)
{
    public bool IsActive => Kind != RepositoryOperationKind.None;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool CanContinue => IsActive && !HasConflicts;
    public string Label => Kind switch
    {
        RepositoryOperationKind.Rebase => "正在变基", RepositoryOperationKind.Merge => "正在合并",
        RepositoryOperationKind.Revert => "正在撤销提交", RepositoryOperationKind.CherryPick => "正在应用提交",
        RepositoryOperationKind.StashApply => "正在恢复 Stash", RepositoryOperationKind.RestoreChanges => "待恢复保存的改动", _ => ""
    };
}
public sealed record OperationPreview(string RepositoryPath, string Branch, string Head, string Target, string Title,
    string Details, int ChangedFiles, IReadOnlyList<string> LocalCommits, IReadOnlyList<string> IncomingCommits,
    string? Remote = null, string? RemoteReference = null);
public sealed record ConflictContent(string Path, string? Current, string? Incoming, string Result, bool IsBinary);
internal sealed record OperationSession(RepositoryOperationKind Kind, string RecoveryReference, string? StashId,
    string? AppliedStash = null, bool DropStash = false, IReadOnlyList<string>? IntroducedFiles = null);
