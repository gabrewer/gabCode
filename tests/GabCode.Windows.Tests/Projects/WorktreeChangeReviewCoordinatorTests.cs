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
        older.SetResult(new ReviewSnapshot("C:\\repo\\one", "old", "main", [], [], false, null));
        await old;
        newer.SetResult(new ReviewSnapshot("C:\\repo\\two", "new", "main", [], [], false, null));
        await current;
        Assert.Equal("C:\\repo\\two", coordinator.Snapshot?.Worktree);
        Assert.Equal("new", coordinator.Snapshot?.Head);
        Assert.False(SharedExpected("switch-worktree-rejects-late-result").GetProperty("oldResultApplied").GetBoolean());
    }

    [Fact]
    public async Task Switching_worktrees_does_not_start_replacement_until_old_worker_finishes()
    {
        var old = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator(async (path, _, _) =>
        {
            var running = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref peak, running);
            try
            {
                if (path.EndsWith("old", StringComparison.Ordinal)) { started.TrySetResult(); return await old.Task; }
                return new ReviewSnapshot(path, "new", "main", [], [], false, null);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        coordinator.Select("C:\\repo\\old", "trunk");
        var first = coordinator.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Select("C:\\repo\\new", "trunk");
        var second = coordinator.RefreshAsync();
        Assert.Equal(1, Volatile.Read(ref active));
        old.SetResult(new ReviewSnapshot("C:\\repo\\old", "old", "main", [], [], false, null));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, peak);
        Assert.Equal("new", coordinator.Snapshot?.Head);
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            int current;
            do { current = Volatile.Read(ref target); if (current >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
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
        old.SetResult(new ReviewSnapshot("C:\\repo\\one", "oldSha", "main", [], [], false, null));
        await first;
        newer.SetResult(new ReviewSnapshot("C:\\repo\\one", "newSha", "main", [], [], false, null));
        await second;
        var expected = SharedExpected("superseded-generation-cannot-overwrite-new");
        Assert.Equal(expected.GetProperty("history")[0].GetString(), coordinator.Snapshot?.Head);
        Assert.False(expected.GetProperty("oldResultApplied").GetBoolean());
    }

    [Fact]
    public async Task Budget_exhaustion_retries_silently_then_pauses_on_the_fourth_attempt()
    {
        var calls = 0;
        var delays = new List<int>();
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) => Task.FromResult(++calls == 1
            ? new ReviewSnapshot(path, "known-good", "main", [], [], false, null)
            : new ReviewSnapshot(path, null, null, [], [], true, "Eight-second budget exceeded", ReviewStopReason.Budget)),
            (attempt, _) => { delays.Add(attempt); return Task.CompletedTask; });
        coordinator.Select("C:\\repo\\one", "main");
        await coordinator.RefreshAsync();
        var updates = new List<ReviewSnapshot?>();
        coordinator.Changed += updates.Add;

        await coordinator.RefreshAsync();

        Assert.Equal(5, calls); // One successful baseline plus exactly four budgeted attempts.
        Assert.Equal([1, 2, 3], delays);
        Assert.Single(updates); // No intermediate error published while retrying.
        Assert.True(coordinator.IsPaused);
        Assert.Equal("known-good", coordinator.Snapshot?.Head);
        Assert.Equal(ReviewStopReason.Budget, coordinator.Snapshot?.StopReason);
    }

    [Fact]
    public async Task Successful_budget_retry_publishes_only_its_successful_snapshot()
    {
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) => Task.FromResult(++calls < 3
            ? new ReviewSnapshot(path, null, null, [], [], true, "budget", ReviewStopReason.Budget)
            : new ReviewSnapshot(path, "success", "main", [], [], false, null)),
            (_, _) => Task.CompletedTask);
        coordinator.Select("C:\\repo\\one", "main");
        var updates = new List<ReviewSnapshot?>();
        coordinator.Changed += updates.Add;

        await coordinator.RefreshAsync();

        Assert.Equal(3, calls);
        Assert.Single(updates);
        Assert.False(coordinator.IsPaused);
        Assert.Equal("success", coordinator.Snapshot?.Head);
    }

    [Fact]
    public async Task Switching_worktrees_cancels_a_pending_budget_retry()
    {
        var calls = 0;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
        {
            ++calls;
            return Task.FromResult(new ReviewSnapshot(path, null, null, [], [], true, "budget", ReviewStopReason.Budget));
        }, (_, _) => { delayStarted.TrySetResult(); return neverReleased.Task; });
        coordinator.Select("C:\\repo\\one", "main");
        var refresh = coordinator.RefreshAsync();
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Select("C:\\repo\\two", "main");

        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, calls);
        Assert.Null(coordinator.Snapshot);
    }

    [Fact]
    public async Task Cancel_during_budget_backoff_stops_retries_without_erasing_the_cancel_state()
    {
        var calls = 0;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
        {
            ++calls;
            return Task.FromResult(new ReviewSnapshot(path, null, null, [], [], true, "budget", ReviewStopReason.Budget));
        }, (_, _) => { delayStarted.TrySetResult(); return blockedDelay.Task; });
        coordinator.Select("C:\\repo\\one", "main");
        var refresh = coordinator.RefreshAsync();
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.CancelAndPause();

        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, calls);
        Assert.True(coordinator.IsPaused);
        Assert.Equal(ReviewStopReason.Cancelled, coordinator.Snapshot?.StopReason);
    }

    [Fact]
    public async Task Safety_stop_pauses_same_worktree_until_explicit_retry_and_retains_its_previous_snapshot()
    {
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
            Task.FromResult(++calls == 1
                ? new ReviewSnapshot(path, "good", "main", [], [], false, null)
                : new ReviewSnapshot(path, null, null, [], [], true, "Safety budget exceeded.", ReviewStopReason.Limit)));
        coordinator.Select("C:\\repo\\one", "trunk");
        await coordinator.RefreshAsync();
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsPaused);
        Assert.Equal("good", coordinator.Snapshot?.Head);
        Assert.True(coordinator.Snapshot?.IsStale);
        Assert.True(coordinator.Snapshot?.WasSuccessful);
        await coordinator.RefreshAsync();
        Assert.Equal(2, calls);
        coordinator.Select("C:\\repo\\two", "trunk");
        coordinator.Select("C:\\repo\\one", "trunk");
        Assert.True(coordinator.IsPaused);
        Assert.True(coordinator.Retry());
        await coordinator.RefreshAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Returning_to_the_same_worktree_retains_only_its_verified_snapshot()
    {
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
            Task.FromResult(new ReviewSnapshot(path, "head-of-one", "main", [], [], false, null)));
        coordinator.Select("C:\\repo\\one", "trunk");
        await coordinator.RefreshAsync();
        coordinator.Select("C:\\repo\\two", "trunk");
        Assert.Null(coordinator.Snapshot);
        coordinator.Select("C:\\repo\\one", "trunk");
        Assert.Equal("head-of-one", coordinator.Snapshot?.Head);
        Assert.True(coordinator.Snapshot?.WasSuccessful);
        Assert.True(coordinator.Snapshot?.IsStale);
        coordinator.Select("C:\\repo\\one", "other-branch");
        Assert.Null(coordinator.Snapshot);
    }

    [Fact]
    public async Task More_than_eight_paused_scopes_cannot_silently_restart_the_oldest_scope()
    {
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
        {
            ++calls;
            return Task.FromResult(new ReviewSnapshot(path, "head", "main", [], [], false, null));
        });
        for (var index = 0; index < 9; index++)
        {
            coordinator.Select($"C:\\repo\\scope-{index}", "trunk");
            coordinator.CancelAndPause();
        }
        coordinator.Select("C:\\repo\\scope-0", "trunk");
        Assert.True(coordinator.IsPaused);
        await coordinator.RefreshAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failed_retry_remains_paused_until_a_successful_retry()
    {
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) => Task.FromResult(++calls switch
        {
            1 => new ReviewSnapshot(path, null, null, [], [], true, "limit", ReviewStopReason.Limit),
            2 => new ReviewSnapshot(path, null, null, [], [], true, "Git unavailable"),
            _ => new ReviewSnapshot(path, "good", "main", [], [], false, null)
        }));
        coordinator.Select("C:\\repo\\one", "trunk");
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsPaused);
        Assert.True(coordinator.Retry());
        await coordinator.RefreshAsync();
        Assert.True(coordinator.IsPaused);
        Assert.Equal(2, calls);
        await coordinator.RefreshAsync();
        Assert.Equal(2, calls);
        Assert.True(coordinator.Retry());
        await coordinator.RefreshAsync();
        Assert.False(coordinator.IsPaused);
        Assert.Equal("good", coordinator.Snapshot?.Head);
    }

    [Fact]
    public async Task User_cancel_pauses_automatic_refresh_even_when_no_previous_snapshot_exists()
    {
        var running = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = new WorktreeChangeReviewCoordinator((path, _, _) =>
            ++calls == 1 ? running.Task : Task.FromResult(new ReviewSnapshot(path, "new", "main", [], [], false, null)));
        coordinator.Select("C:\\repo\\one", "trunk");
        var first = coordinator.RefreshAsync();
        coordinator.CancelAndPause();
        Assert.True(coordinator.IsPaused);
        Assert.True(coordinator.Snapshot?.IsStale);
        Assert.Null(coordinator.Snapshot?.Head);
        await coordinator.RefreshAsync();
        Assert.Equal(1, calls);
        running.SetResult(new ReviewSnapshot("C:\\repo\\one", "late", "main", [], [], false, null));
        await first;
        Assert.Null(coordinator.Snapshot?.Head);
        Assert.True(coordinator.Retry());
        await coordinator.RefreshAsync();
        Assert.Equal("new", coordinator.Snapshot?.Head);
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
