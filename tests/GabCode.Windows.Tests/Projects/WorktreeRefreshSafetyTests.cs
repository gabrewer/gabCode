using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using GabCode.Windows.Projects;
using Xunit.Abstractions;

namespace GabCode.Windows.Tests.Projects;

/// <summary>Small, disposable baselines. Load tests require an independently supervised runner.</summary>
public sealed class WorktreeRefreshSafetyTests
{
    private readonly ITestOutputHelper output;
    public WorktreeRefreshSafetyTests(ITestOutputHelper output) => this.output = output;
    [Theory]
    [InlineData("equal", false, false)]
    [InlineData("lagging", true, false)]
    [InlineData("divergent", true, true)]
    public async Task Remote_lag_alone_does_not_change_local_main_comparison(string scenario, bool advanceRemote, bool diverge)
    {
        using var repo = new DisposableRepo();
        await repo.Initialize();
        var baseSha = await repo.Git("rev-parse", "HEAD");
        if (advanceRemote)
        {
            await repo.Git("switch", "-c", "remote-fixture");
            await repo.ChangeAndCommit("remote-only.txt", "remote", "remote advancement");
            var remoteSha = await repo.Git("rev-parse", "HEAD");
            await repo.Git("update-ref", "refs/remotes/origin/main", remoteSha);
            await repo.Git("switch", "main");
        }
        else await repo.Git("update-ref", "refs/remotes/origin/main", baseSha);
        if (diverge) await repo.ChangeAndCommit("local-only.txt", "local", "local advancement");
        var local = await repo.Git("rev-parse", "refs/heads/main");
        var remote = await repo.Git("rev-parse", "refs/remotes/origin/main");
        var snapshot = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "main");
        Assert.False(snapshot.IsStale, $"{scenario}: {snapshot.Error}");
        Assert.Equal(local, snapshot.MainHead);
        Assert.Empty(snapshot.Commits); // Selected HEAD is local main; remote divergence is irrelevant.
        Assert.Equal(remote, await repo.Git("rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(local, await repo.Git("rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Changed_tree_history_ahead_of_local_main_is_paged_without_ref_mutation()
    {
        using var repo = new DisposableRepo();
        await repo.Initialize();
        var main = await repo.Git("rev-parse", "refs/heads/main");
        await repo.Git("switch", "-c", "feature");
        for (var i = 0; i < 5; i++) await repo.ChangeAndCommit($"changed-{i}.txt", $"content-{i}", $"feature-{i}");
        await repo.Git("update-ref", "refs/remotes/origin/main", await repo.Git("rev-parse", "HEAD"));
        var snapshot = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "main");
        Assert.False(snapshot.IsStale, snapshot.Error);
        Assert.Equal(main, snapshot.MainHead);
        Assert.Equal(5, snapshot.Commits.Count);
        Assert.All(snapshot.Commits, commit => Assert.NotEqual("Summary unavailable", commit.Summary));
        Assert.Equal(main, await repo.Git("rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Small_real_review_refresh_leaves_WPF_dispatcher_servicing_input()
    {
        using var repo = new DisposableRepo();
        await repo.Initialize();
        await repo.Git("switch", "-c", "feature");
        for (var i = 0; i < 20; ++i) await repo.ChangeAndCommit($"commit-{i}.txt", $"content-{i}", $"feature-{i}");
        for (var i = 0; i < 100; ++i) await File.WriteAllTextAsync(System.IO.Path.Combine(repo.Path, $"untracked-{i}.txt"), "fixture");
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var maxBeat = TimeSpan.Zero;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                var last = Stopwatch.StartNew();
                var beat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                beat.Tick += (_, _) => { maxBeat = TimeSpan.FromMilliseconds(Math.Max(maxBeat.TotalMilliseconds, last.Elapsed.TotalMilliseconds)); last.Restart(); };
                try
                {
                    beat.Start();
                    sidebar.Select(repo.Path, "main");
                    var end = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                    while (DateTime.UtcNow < end)
                    {
                        if (!sidebar.IsRefreshing && sidebar.HasFreshSnapshot) break;
                        await Task.Delay(25);
                    }
                    Assert.True(maxBeat > TimeSpan.Zero, "No dispatcher heartbeat ran.");
                    Assert.True(DateTime.UtcNow < end, "Review did not finish in the bounded fixture window.");
                }
                catch (Exception ex) { failure = ex; }
                finally { beat.Stop(); sidebar.Dispose(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); complete.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        output.WriteLine($"Small fixture: 20 changed-tree commits, 100 untracked files; max dispatcher heartbeat {maxBeat.TotalMilliseconds:F0} ms.");
        Assert.InRange(maxBeat.TotalMilliseconds, 1, 1000); // Baseline envelope, not the final responsiveness budget.
    }

    private sealed class DisposableRepo : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gabCode refresh Ω " + Guid.NewGuid().ToString("N"));
        internal DisposableRepo() => Directory.CreateDirectory(Path);
        internal async Task Initialize()
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Fixture");
            await Git("config", "user.email", "fixture@example.invalid");
            await ChangeAndCommit("root.txt", "root", "root");
        }
        internal async Task ChangeAndCommit(string name, string value, string subject)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, name), value);
            await Git("add", "--", name);
            await Git("commit", "-m", subject);
        }
        internal async Task<string> Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {await stderr}");
            return (await stdout).Trim();
        }
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }
}
