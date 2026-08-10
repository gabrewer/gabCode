using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeNavigationStateTests
{
    [Fact]
    public void Reconciles_missing_worktree_to_unavailable_then_orphan_and_restores_same_path()
    {
        var primary = new RegisteredWorktree("C:\\repo\\main", "trunk", IsPrimary: true);
        var feature = new RegisteredWorktree("C:\\repo\\wt\\feature", "feature/demo", IsPrimary: false);
        var state = new WorktreeNavigationState([primary, feature]);
        state.MarkTerminalPairCreated(feature.Path);

        state.Reconcile([primary]);
        var unavailable = Assert.Single(state.Entries, entry => entry.Path == feature.Path);
        Assert.Equal(WorktreeAvailability.Unavailable, unavailable.Availability);
        Assert.Empty(state.Orphaned);

        state.Reconcile([primary]);
        Assert.DoesNotContain(state.Entries, entry => entry.Path == feature.Path);
        Assert.Contains(state.Orphaned, entry => entry.Path == feature.Path);

        state.Reconcile([primary, feature]);
        var restored = Assert.Single(state.Entries, entry => entry.Path == feature.Path);
        Assert.Equal(WorktreeAvailability.Available, restored.Availability);
        Assert.Empty(state.Orphaned);
    }

    [Fact]
    public void Explicit_orphan_close_removes_only_the_orphan_entry()
    {
        var primary = new RegisteredWorktree("C:\\repo\\main", "trunk", true);
        var feature = new RegisteredWorktree("C:\\repo\\wt\\feature", "feature/demo", false);
        var state = new WorktreeNavigationState([primary, feature]);
        state.MarkTerminalPairCreated(feature.Path);
        state.Reconcile([primary]);
        state.Reconcile([primary]);

        state.RemoveOrphan("C:\\REPO\\WT\\FEATURE");

        Assert.Empty(state.Orphaned);
        Assert.Single(state.Entries);
    }

    [Fact]
    public void Orders_primary_then_available_folder_name_then_unavailable()
    {
        var entries = new WorktreeNavigationState([
            new RegisteredWorktree("C:\\repo\\wt\\zulu", "zulu", false),
            new RegisteredWorktree("C:\\repo\\main", "trunk", true),
            new RegisteredWorktree("C:\\repo\\wt\\alpha", "alpha", false),
        ]);
        entries.Reconcile([
            new RegisteredWorktree("C:\\repo\\wt\\zulu", "zulu", false),
            new RegisteredWorktree("C:\\repo\\main", "trunk", true),
            new RegisteredWorktree("C:\\repo\\wt\\alpha", "alpha", false),
        ]);

        Assert.Equal(["main", "alpha", "zulu"], entries.Entries.Select(entry => Path.GetFileName(entry.Path)).ToArray());
    }

    [Fact]
    public void Rejects_a_stale_refresh_generation_without_replacing_current_entries()
    {
        var primary = new RegisteredWorktree("C:\\repo\\main", "trunk", true);
        var feature = new RegisteredWorktree("C:\\repo\\wt\\feature", "feature/demo", false);
        var state = new WorktreeNavigationState([primary]);
        var refresh = new WorktreeRefreshCoordinator(state);
        var staleGeneration = refresh.BeginRefresh();
        var currentGeneration = refresh.BeginRefresh();

        Assert.True(refresh.TryReconcile(currentGeneration, [primary, feature]));
        Assert.False(refresh.TryReconcile(staleGeneration, [primary]));
        Assert.Contains(state.Entries, entry => entry.Path == feature.Path);
    }

    [Fact]
    public void Detached_entries_are_not_selectable()
    {
        var parsed = GitWorktreeDiscovery.ParseEntries("worktree C:\\repo\\main\nHEAD abc\nbranch refs/heads/trunk\n\nworktree C:\\repo\\detached\nHEAD abc\n\n");

        var entry = Assert.Single(parsed);
        Assert.Equal("trunk", entry.Branch);
    }

    [Fact]
    public void Detached_primary_does_not_make_a_linked_worktree_primary()
    {
        var parsed = GitWorktreeDiscovery.ParseEntries("worktree C:\\repo\\detached\nHEAD abc\n\nworktree C:\\repo\\main\nHEAD def\nbranch refs/heads/trunk\n\n");

        var entry = Assert.Single(parsed);
        Assert.False(entry.IsPrimary);
    }

    [Fact]
    public void Root_path_identity_retains_the_root_separator()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;

        Assert.Equal(Path.GetFullPath(root), WorktreePath.Normalize(root));
    }

    [Fact]
    public void Path_identity_is_normalized_case_insensitively()
    {
        var state = new WorktreeNavigationState([
            new RegisteredWorktree("C:\\Repo\\Main", "trunk", true),
        ]);

        state.MarkTerminalPairCreated("c:\\repo\\main");
        state.Reconcile([new RegisteredWorktree("c:\\repo\\main", "trunk", true)]);

        Assert.Empty(state.Orphaned);
        Assert.True(state.HasTerminalPair("C:\\REPO\\MAIN"));
    }
}
