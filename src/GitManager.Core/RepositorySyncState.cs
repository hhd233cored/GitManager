namespace GitManager.Core;

public enum SyncAction { Fetch, Pull, Push, Publish, Resolve }

public sealed record RepositorySyncState(SyncAction Action, string RemoteName, bool IsEnabled, int Ahead, int Behind)
{
    public string Label => Action switch
    {
        SyncAction.Pull => $"Pull {RemoteName}",
        SyncAction.Push => $"Push {RemoteName}",
        SyncAction.Publish => "Publish branch",
        SyncAction.Resolve => "解决分叉",
        _ => $"Fetch {RemoteName}"
    };
    public string Glyph => Action == SyncAction.Resolve ? "⇄" : Action == SyncAction.Fetch ? "⟳" : Action == SyncAction.Pull ? "↓" : "↑";
    public string Badge => Action switch
    {
        SyncAction.Pull when Ahead > 0 => $"{Behind} ↓  {Ahead} ↑",
        SyncAction.Pull => $"{Behind} ↓",
        SyncAction.Push => $"{Ahead} ↑",
        SyncAction.Resolve => $"{Behind} ↓  {Ahead} ↑",
        _ => ""
    };

    public static RepositorySyncState From(RepositoryInfo? repository, bool hasFetchedRemote = true)
    {
        var remote = repository?.RemoteName ?? "remote";
        if (repository is null || !repository.HasRemote) return new(SyncAction.Fetch, remote, false, 0, 0);
        if (!repository.HasHead || repository.IsDetached) return new(SyncAction.Fetch, remote, true, 0, 0);
        if (repository.Upstream is not null && !hasFetchedRemote) return new(SyncAction.Fetch, remote, true, 0, 0);
        var action = repository.Upstream is null ? SyncAction.Publish
            : repository.Ahead > 0 && repository.Behind > 0 ? SyncAction.Resolve
            : repository.Behind > 0 ? SyncAction.Pull
            : repository.Ahead > 0 ? SyncAction.Push
            : SyncAction.Fetch;
        return new(action, remote, true, repository.Ahead, repository.Behind);
    }
}
