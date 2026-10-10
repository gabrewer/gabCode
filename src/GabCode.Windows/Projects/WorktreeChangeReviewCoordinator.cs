namespace GabCode.Windows.Projects;

/// <summary>Owns cancellation and generation-scoped reconciliation of the selected worktree's review snapshot.</summary>
internal sealed class WorktreeChangeReviewCoordinator : IDisposable
{
    private readonly Func<string, string?, CancellationToken, Task<ReviewSnapshot>> read;
    private readonly object gate = new();
    private CancellationTokenSource? pending;
    private long generation;
    private string? worktree;
    private string? mainBranch;
    private bool disposed;

    internal WorktreeChangeReviewCoordinator(Func<string, string?, CancellationToken, Task<ReviewSnapshot>> read) => this.read = read;
    internal ReviewSnapshot? Snapshot { get; private set; }
    internal event Action<ReviewSnapshot?>? Changed;

    internal void Select(string? selectedWorktree, string? branch)
    {
        lock (gate)
        {
            if (disposed) return;
            ++generation;
            pending?.Cancel();
            pending?.Dispose();
            pending = null;
            worktree = selectedWorktree;
            mainBranch = branch;
            Snapshot = null;
        }
        Changed?.Invoke(null);
    }

    internal void MarkStale(string error)
    {
        ReviewSnapshot? stale;
        lock (gate)
        {
            if (disposed || Snapshot is null) return;
            ++generation;
            pending?.Cancel();
            Snapshot = Snapshot with { IsStale = true, Error = error };
            stale = Snapshot;
        }
        Changed?.Invoke(stale);
    }

    internal async Task RefreshAsync()
    {
        CancellationToken token;
        string path;
        string? branch;
        long current;
        lock (gate)
        {
            if (disposed || worktree is null) return;
            current = ++generation;
            pending?.Cancel();
            pending?.Dispose();
            pending = new CancellationTokenSource();
            token = pending.Token;
            path = worktree;
            branch = mainBranch;
        }
        ReviewSnapshot result;
        try { result = await read(path, branch, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) { result = new ReviewSnapshot(path, null, null, [], [], true, error.Message); }
        ReviewSnapshot? visible;
        lock (gate)
        {
            if (disposed || current != generation) return;
            visible = result.IsStale && Snapshot is not null
                ? Snapshot with { IsStale = true, Error = result.Error }
                : result;
            Snapshot = visible;
        }
        Changed?.Invoke(visible);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ++generation;
            pending?.Cancel();
            pending?.Dispose();
            pending = null;
            Snapshot = null;
        }
    }
}
