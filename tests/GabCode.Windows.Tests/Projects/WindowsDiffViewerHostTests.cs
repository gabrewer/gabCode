using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class WindowsDiffViewerHostTests
{
    [Fact]
    public async Task Disposable_WPF_host_loads_bundled_page_and_receives_fixture_pair_without_a_workspace()
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../shared/native-diff-viewer/dist"));
        var profile = Path.Combine(Path.GetTempPath(), "gabcode-diff-host-test-" + Guid.NewGuid().ToString("N"));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Window? window = null;
                WindowsDiffViewerHost? host = null;
                try
                {
                    host = new WindowsDiffViewerHost(assets, profile);
                    window = new Window { Content = host, Width = 720, Height = 440, ShowInTaskbar = false };
                    window.Show();
                    await host.InitializeAsync();
                    host.Select(1, "fixture:staged");
                    await host.ShowAsync(new WindowsDiffViewerHost.Snapshot(1, "fixture:staged", "src/app.cs", "committed\n", "staged\n", "HEAD", "index", "side-by-side"));
                    var value = await host.ReadDocumentForTestAsync();
                    Assert.Equal("committed\n", value.Before);
                    Assert.Equal("staged\n", value.After);
                    finished.TrySetResult();
                }
                catch (Exception error) { finished.TrySetException(error); }
                finally
                {
                    window?.Close();
                    host?.Dispose();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Replacing_selection_and_disposal_invalidates_publication()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new WindowsDiffViewerHost("absent", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
                var old = new WindowsDiffViewerHost.Snapshot(1, "first", "x.cs", "a", "b", "HEAD", "index", "side-by-side");
                host.Select(1, "first");
                host.Select(2, "second");
                Assert.ThrowsAsync<ArgumentException>(() => host.ShowAsync(old)).GetAwaiter().GetResult();
                host.Dispose();
                Assert.Throws<ObjectDisposedException>(() => host.Select(3, "third"));
                Assert.ThrowsAsync<ObjectDisposedException>(() => host.ShowAsync(old)).GetAwaiter().GetResult();
                finished.TrySetResult();
            }
            catch (Exception error) { finished.TrySetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Fixture_snapshot_is_accepted_but_partial_large_or_stale_pairs_are_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/fixtures/native-diff-review/cases.json"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(root));
        var row = Assert.Single(fixture.RootElement.GetProperty("cases").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "staged-result-not-worktree");
        var expected = row.GetProperty("expected");
        var snapshot = new WindowsDiffViewerHost.Snapshot(7, "wt:a:staged:src/app.cs", "src/app.cs",
            expected.GetProperty("before").GetString()!, expected.GetProperty("after").GetString()!,
            "HEAD", "index", "side-by-side");
        Assert.True(WindowsDiffViewerHost.IsValidSnapshot(snapshot, 7, "wt:a:staged:src/app.cs"));
        Assert.False(WindowsDiffViewerHost.IsValidSnapshot(snapshot, 8, snapshot.Identity));
        Assert.False(WindowsDiffViewerHost.IsValidSnapshot(snapshot with { After = null! }, 7, snapshot.Identity));
        Assert.False(WindowsDiffViewerHost.IsValidSnapshot(snapshot with { Before = new string('x', 1024 * 1024 + 1) }, 7, snapshot.Identity));
        Assert.False(WindowsDiffViewerHost.IsValidSnapshot(snapshot, 7, "other-worktree:src/app.cs"));
    }

    [Fact]
    public void Only_exact_expected_origin_and_message_kinds_can_reach_host()
    {
        const string origin = "https://gabcode-diff.invalid/";
        Assert.True(WindowsDiffViewerHost.IsAllowedViewerMessage(origin, "{\"kind\":\"ready\"}", origin));
        Assert.False(WindowsDiffViewerHost.IsAllowedViewerMessage("https://evil.invalid/", "{\"kind\":\"ready\"}", origin));
        Assert.False(WindowsDiffViewerHost.IsAllowedViewerMessage(origin, "{\"kind\":\"save\"}", origin));
        Assert.False(WindowsDiffViewerHost.IsAllowedViewerMessage(origin, "{\"kind\":\"ready\",\"path\":\"secret\"}", origin));
        Assert.False(WindowsDiffViewerHost.IsAllowedViewerMessage(origin, "{\"kind\":\"failure\",\"message\":\"" + new string('z', 2049) + "\"}", origin));
        Assert.False(WindowsDiffViewerHost.IsAllowedViewerMessage(origin, "{", origin));
    }

    [Fact]
    public void Missing_or_corrupted_bundle_is_not_treated_as_a_valid_offline_page()
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../shared/native-diff-viewer/dist"));
        Assert.True(WindowsDiffViewerHost.VerifyBundle(assets, out var error), error);
        Assert.False(WindowsDiffViewerHost.VerifyBundle(Path.Combine(assets, "absent"), out _));
    }
}
