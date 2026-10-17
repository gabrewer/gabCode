using System.IO;

namespace GabCode.Windows.Projects;

/// <summary>Owns cancellation and generation-scoped reconciliation of the selected worktree's review snapshot.</summary>
internal sealed class WorktreeChangeReviewCoordinator : IDisposable
{
    private readonly Func<string, string?, CancellationToken, Task<ReviewSnapshot>> read;
    private readonly object gate = new();
    // Cancellation invalidates a result immediately, but capacity is held until its reader exits.
    private readonly SemaphoreSlim readSlot = new(1, 1);
    private readonly HashSet<string> paused = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> pauseOrder = [];
    private readonly Dictionary<string, ReviewSnapshot> lastSuccessful = new(StringComparer.Ordinal);
    private readonly List<string> snapshotOrder = [];
    private CancellationTokenSource? pending;
    private long generation;
    private string? worktree;
    private string? mainBranch;
    private string? retryScope;
    private bool scopeOverflow;
    private const string ScopeLimitMessage = "Git refresh is paused across this app instance because too many worktrees stopped. Restart the app to recover; no automatic refresh will run.";
    private bool disposed;

    internal WorktreeChangeReviewCoordinator(Func<string, string?, CancellationToken, Task<ReviewSnapshot>> read) => this.read = read;
    internal ReviewSnapshot? Snapshot { get; private set; }
    internal bool IsPaused { get { lock (gate) return worktree is not null && (scopeOverflow || paused.Contains(worktree)); } }
    internal bool CanRefresh { get { lock (gate) return worktree is not null && !scopeOverflow && (!paused.Contains(worktree) || retryScope == worktree); } }
    internal string? StopMessage { get { lock (gate) return scopeOverflow ? ScopeLimitMessage : null; } }
    internal event Action<ReviewSnapshot?>? Changed;

    private void Pause(string path)
    {
        if (paused.Contains(path) || scopeOverflow) return;
        if (pauseOrder.Count == 8) { scopeOverflow = true; return; }
        paused.Add(path);
        pauseOrder.Add(path);
    }

    internal void CancelAndPause() => StopAndPause(ReviewStopReason.Cancelled, "Refresh cancelled. Automatic refresh is paused.");

    internal void PauseForSafety(string reason) => StopAndPause(ReviewStopReason.Limit, reason);

    private void StopAndPause(ReviewStopReason reason, string message)
    {
        ReviewSnapshot? stale;
        lock (gate)
        {
            if (disposed || worktree is null) return;
            Pause(worktree);
            ++generation;
            retryScope = null;
            pending?.Cancel();
            if (scopeOverflow) message = ScopeLimitMessage;
            stale = Snapshot is null
                ? new ReviewSnapshot(worktree, null, null, [], [], true, message, reason)
                : Snapshot with { IsStale = true, Error = message, StopReason = reason };
            Snapshot = stale;
        }
        Changed?.Invoke(stale);
    }

    internal bool Retry()
    {
        lock (gate)
        {
            if (disposed || worktree is null || scopeOverflow || readSlot.CurrentCount == 0) return false;
            retryScope = worktree;
            return true;
        }
    }

    private static string SnapshotKey(string path, string? branch) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + "\0" + branch;

    internal void Select(string? selectedWorktree, string? branch)
    {
        ReviewSnapshot? visible;
        lock (gate)
        {
            if (disposed) return;
            ++generation;
            pending?.Cancel();
            pending?.Dispose();
            pending = null;
            retryScope = null;
            worktree = selectedWorktree;
            mainBranch = branch;
            visible = selectedWorktree is not null && lastSuccessful.TryGetValue(SnapshotKey(selectedWorktree, branch), out var prior)
                ? prior with { IsStale = true, Error = "Refreshing changes; showing previous results.", StopReason = ReviewStopReason.None }
                : null;
            Snapshot = visible;
        }
        Changed?.Invoke(visible);
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
        bool retry;
        lock (gate)
        {
            if (disposed || worktree is null || scopeOverflow || paused.Contains(worktree) && retryScope != worktree) return;
            retry = retryScope == worktree;
            retryScope = null;
            current = ++generation;
            pending?.Cancel();
            pending?.Dispose();
            pending = new CancellationTokenSource();
            token = pending.Token;
            path = worktree;
            branch = mainBranch;
        }
        ReviewSnapshot result;
        var acquired = false;
        try
        {
            await readSlot.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            token.ThrowIfCancellationRequested();
            for (var attempt = 1; ; attempt++)
            {
                result = await read(path, branch, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.StopReason != ReviewStopReason.Budget || attempt == 4) break;
                // The prior attempt has finished cleanup. Retry immediately without publishing its error.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error) { result = new ReviewSnapshot(path, null, null, [], [], true, error.Message); }
        finally { if (acquired) readSlot.Release(); }
        ReviewSnapshot? visible;
        lock (gate)
        {
            if (disposed || current != generation) return;
            if (result.StopReason is ReviewStopReason.Limit or ReviewStopReason.Budget) Pause(path);
            if (retry && !result.IsStale)
            {
                paused.Remove(path);
                pauseOrder.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
            }
            visible = result.IsStale && Snapshot is not null
                ? Snapshot with { IsStale = true, Error = result.Error, StopReason = result.StopReason }
                : result with { WasSuccessful = !result.IsStale || result.WasSuccessful };
            Snapshot = visible;
            if (!visible.IsStale && visible.WasSuccessful)
            {
                var key = SnapshotKey(path, branch);
                lastSuccessful[key] = visible;
                snapshotOrder.Remove(key);
                snapshotOrder.Add(key);
                if (snapshotOrder.Count > 8)
                {
                    lastSuccessful.Remove(snapshotOrder[0]);
                    snapshotOrder.RemoveAt(0);
                }
            }
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
