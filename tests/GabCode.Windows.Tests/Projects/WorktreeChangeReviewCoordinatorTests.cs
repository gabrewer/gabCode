using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeChangeReviewCoordinatorTests
{
    [Fact]
    public async Task Late_result_for_an_old_worktree_or_generation_cannot_replace_visible_state()
    {
        var older = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, branch, _) =>
            ++calls == 1 ? older.Task : newer.Task);
        coordinator.Select("C:\\repo\\one", "trunk");
        var old = coordinator.RefreshAsync();
        coordinator.Select("C:\\repo\\two", "trunk");
        var current = coordinator.RefreshAsync();
        newer.SetResult(new ReviewSnapshot("C:\\repo\\two", "new", "main", [], [], false, null));
        await current;
        older.SetResult(new ReviewSnapshot("C:\\repo\\one", "old", "main", [], [], false, null));
        await old;
        Assert.Equal("C:\\repo\\two", coordinator.Snapshot?.Worktree);
        Assert.Equal("new", coordinator.Snapshot?.Head);
        Assert.False(SharedExpected("switch-worktree-rejects-late-result").GetProperty("oldResultApplied").GetBoolean());
    }

    [Fact]
    public async Task Same_worktree_newer_refresh_rejects_a_late_older_result()
    {
        var old = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) => ++calls == 1 ? old.Task : newer.Task);
        coordinator.Select("C:\\repo\\one", "trunk");
        var first = coordinator.RefreshAsync();
        var second = coordinator.RefreshAsync();
        newer.SetResult(new ReviewSnapshot("C:\\repo\\one", "newSha", "main", [], [], false, null));
        await second;
        old.SetResult(new ReviewSnapshot("C:\\repo\\one", "oldSha", "main", [], [], false, null));
        await first;
        var expected = SharedExpected("superseded-generation-cannot-overwrite-new");
        Assert.Equal(expected.GetProperty("history")[0].GetString(), coordinator.Snapshot?.Head);
        Assert.False(expected.GetProperty("oldResultApplied").GetBoolean());
    }

    [Fact]
    public async Task Watcher_failure_marks_previous_data_stale_until_a_successful_refresh()
    {
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
            Task.FromResult(new ReviewSnapshot(path, "head", "main", [], [], false, null)));
        coordinator.Select("C:\\repo\\one", "trunk");
        await coordinator.RefreshAsync();
        coordinator.MarkStale("watcher overflow");
        Assert.True(coordinator.Snapshot?.IsStale);
        Assert.Equal("head", coordinator.Snapshot?.Head);
        Assert.Equal("watcher overflow", coordinator.Snapshot?.Error);
        Assert.True(SharedExpected("watcher-overflow-recovers-from-focus-or-explicit-refresh").GetProperty("errorVisible").GetBoolean());
        await coordinator.RefreshAsync();
        Assert.False(coordinator.Snapshot?.IsStale);
    }

    [Fact]
    public async Task Failure_keeps_last_good_snapshot_visible_as_stale()
    {
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, branch, _) =>
            Task.FromResult(++calls == 1
                ? new ReviewSnapshot(path, "good", "main", [], [], false, null)
                : new ReviewSnapshot(path, null, null, [], [], true, "Git unavailable")));
        coordinator.Select("C:\\repo\\one", "trunk");
        await coordinator.RefreshAsync();
        await coordinator.RefreshAsync();
        Assert.Equal("good", coordinator.Snapshot?.Head);
        Assert.True(coordinator.Snapshot?.IsStale);
        Assert.Equal("Git unavailable", coordinator.Snapshot?.Error);
        Assert.True(SharedExpected("query-failure-retains-stale-snapshot").GetProperty("stale").GetBoolean());
    }

    private static JsonElement SharedExpected(string id)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return Assert.Single(json.RootElement.GetProperty("cases").EnumerateArray(), row => row.GetProperty("id").GetString() == id)
            .GetProperty("expected").Clone();
    }
}
