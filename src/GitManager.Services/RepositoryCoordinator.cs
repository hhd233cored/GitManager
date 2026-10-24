namespace GitManager.Services;

// One execution host owns both UI and agent operations, including repository switches.
public sealed class RepositoryCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable?> EnterAsync(bool wait = true, CancellationToken cancellationToken = default)
    {
        if (wait) await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
