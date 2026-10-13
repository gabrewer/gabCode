namespace GabCode.Windows.Projects;

/// <summary>Constant-space, thread-safe invalidation hint. No Git or dispatcher work runs in watcher callbacks.</summary>
internal sealed class WatcherInvalidation
{
    // Generation and pending bit change atomically so an old callback cannot signal a new selection.
    private long state;

    internal long Generation => Interlocked.Read(ref state) >> 1;

    internal void Signal(long watcherGeneration)
    {
        while (true)
        {
            var current = Interlocked.Read(ref state);
            if (current >> 1 != watcherGeneration || (current & 1) != 0) return;
            if (Interlocked.CompareExchange(ref state, current | 1, current) == current) return;
        }
    }

    internal void Signal() => Signal(Generation);

    internal bool Consume()
    {
        while (true)
        {
            var current = Interlocked.Read(ref state);
            if ((current & 1) == 0) return false;
            if (Interlocked.CompareExchange(ref state, current & ~1L, current) == current) return true;
        }
    }

    internal void Reset()
    {
        while (true)
        {
            var current = Interlocked.Read(ref state);
            if (Interlocked.CompareExchange(ref state, ((current >> 1) + 1) << 1, current) == current) return;
        }
    }
}
