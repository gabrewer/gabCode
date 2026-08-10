using System.IO;

namespace GabCode.Windows.Projects;

enum WorktreeAvailability
{
    Available,
    Unavailable,
}

internal sealed record RegisteredWorktree(string Path, string Branch, bool IsPrimary)
{
    internal string NormalizedPath => WorktreePath.Normalize(Path);
}

internal sealed record WorktreeNavigationEntry(
    string Path,
    string Branch,
    bool IsPrimary,
    WorktreeAvailability Availability,
    int MissingRefreshes,
    bool HasTerminalPair)
{
    internal string FolderName => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}

internal sealed class WorktreeNavigationState
{
    private readonly Dictionary<string, WorktreeNavigationEntry> entries = new(WorktreePath.Comparer);
    private readonly Dictionary<string, WorktreeNavigationEntry> orphaned = new(WorktreePath.Comparer);

    internal WorktreeNavigationState(IEnumerable<RegisteredWorktree> initial)
    {
        Reconcile(initial);
    }

    internal IReadOnlyList<WorktreeNavigationEntry> Entries => Order(entries.Values);

    internal IReadOnlyList<WorktreeNavigationEntry> Orphaned => Order(orphaned.Values);

    internal bool HasTerminalPair(string path) =>
        entries.TryGetValue(WorktreePath.Normalize(path), out var entry) && entry.HasTerminalPair ||
        orphaned.TryGetValue(WorktreePath.Normalize(path), out entry) && entry.HasTerminalPair;

    internal void MarkTerminalPairCreated(string path)
    {
        var normalized = WorktreePath.Normalize(path);
        if (entries.TryGetValue(normalized, out var entry))
        {
            entries[normalized] = entry with { HasTerminalPair = true };
        }
        else if (orphaned.TryGetValue(normalized, out entry))
        {
            orphaned[normalized] = entry with { HasTerminalPair = true };
        }
    }

    internal void RemoveOrphan(string path)
    {
        orphaned.Remove(WorktreePath.Normalize(path));
    }

    internal void Reconcile(IEnumerable<RegisteredWorktree> discovered)
    {
        var current = discovered
            .Select(worktree => new RegisteredWorktree(WorktreePath.Normalize(worktree.Path), worktree.Branch, worktree.IsPrimary))
            .GroupBy(worktree => worktree.NormalizedPath, WorktreePath.Comparer)
            .Select(group => group.First())
            .ToDictionary(worktree => worktree.NormalizedPath, WorktreePath.Comparer);

        foreach (var worktree in current.Values)
        {
            if (entries.TryGetValue(worktree.NormalizedPath, out var existing))
            {
                entries[worktree.NormalizedPath] = existing with
                {
                    Path = worktree.NormalizedPath,
                    Branch = worktree.Branch,
                    IsPrimary = worktree.IsPrimary,
                    Availability = WorktreeAvailability.Available,
                    MissingRefreshes = 0,
                };
            }
            else if (orphaned.Remove(worktree.NormalizedPath, out var restored))
            {
                entries[worktree.NormalizedPath] = restored with
                {
                    Path = worktree.NormalizedPath,
                    Branch = worktree.Branch,
                    IsPrimary = worktree.IsPrimary,
                    Availability = WorktreeAvailability.Available,
                    MissingRefreshes = 0,
                };
            }
            else
            {
                entries[worktree.NormalizedPath] = new WorktreeNavigationEntry(
                    worktree.NormalizedPath,
                    worktree.Branch,
                    worktree.IsPrimary,
                    WorktreeAvailability.Available,
                    0,
                    false);
            }
        }

        foreach (var key in entries.Keys.Except(current.Keys, WorktreePath.Comparer).ToArray())
        {
            var missing = entries[key] with
            {
                Availability = WorktreeAvailability.Unavailable,
                MissingRefreshes = entries[key].MissingRefreshes + 1,
            };
            if (missing.MissingRefreshes < 2)
            {
                entries[key] = missing;
            }
            else
            {
                entries.Remove(key);
                if (missing.HasTerminalPair) orphaned[key] = missing;
            }
        }
    }

    private static IReadOnlyList<WorktreeNavigationEntry> Order(IEnumerable<WorktreeNavigationEntry> source) =>
        source.OrderByDescending(entry => entry.IsPrimary)
            .ThenBy(entry => entry.Availability == WorktreeAvailability.Available ? 0 : 1)
            .ThenBy(entry => entry.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed class WorktreeRefreshCoordinator
{
    private readonly WorktreeNavigationState state;
    private readonly object gate = new();
    private long latestGeneration;

    internal WorktreeRefreshCoordinator(WorktreeNavigationState state)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
    }

    internal long BeginRefresh()
    {
        lock (gate)
        {
            return ++latestGeneration;
        }
    }

    internal bool TryReconcile(long generation, IEnumerable<RegisteredWorktree> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        lock (gate)
        {
            if (generation != latestGeneration) return false;
            state.Reconcile(discovered);
            return true;
        }
    }
}

internal static class WorktreePath
{
    internal static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    internal static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
