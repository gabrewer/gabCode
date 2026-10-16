using System.IO;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class ReviewRefreshControlsTests
{
    [Fact]
    public async Task Cancel_and_retry_are_native_keyboard_reachable_controls_with_paused_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode review controls Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await RunGit(root, "init", "-b", "main");
        await RunGit(root, "config", "user.name", "Fixture");
        await RunGit(root, "config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "root");
        await RunGit(root, "add", ".");
        await RunGit(root, "commit", "-m", "root");
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                var buttons = FindButtons((DependencyObject)sidebar.Content).ToArray();
                var cancel = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Cancel review refresh");
                var retry = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Retry review refresh");
                Assert.True(cancel.Focusable);
                cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar)
                    .GetField("coordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                Assert.True(coordinator.IsPaused);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Contains("paused", coordinator.Snapshot?.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(retry.Focusable);
                retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                // A retry cannot be admitted before the cancelled reader has actually stopped.
                var limit = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (coordinator.IsPaused && DateTime.UtcNow < limit)
                {
                    await Task.Delay(25);
                    retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                Assert.False(coordinator.IsPaused);
            });
        }
        finally
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public async Task Large_working_change_snapshot_never_materializes_all_rows_on_dispatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode rows Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                var working = Enumerable.Range(0, 500)
                    .Select(i => new ReviewFile($"file-{i:D4}.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked)).ToArray();
                typeof(WorktreeReviewSidebar).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, [typeof(ReviewSnapshot)], null)!
                    .Invoke(sidebar, [new ReviewSnapshot(root, "head", "main", working, [], false, null)]);
                var controls = FindButtons((DependencyObject)sidebar.Content).ToArray();
                Assert.InRange(controls.Length, 1, 205);
                var status = LogicalTreeHelper.GetChildren((DependencyObject)sidebar.Content).OfType<TextBlock>();
                Assert.Contains(status, text => text.Text.Contains("not all", StringComparison.OrdinalIgnoreCase));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task First_partial_result_never_claims_previous_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode first partial Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                typeof(WorktreeReviewSidebar).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [typeof(ReviewSnapshot)], null)!.Invoke(sidebar,
                    [new ReviewSnapshot(root, "unverified-head", null, [], [], true, "Git status stopped", ReviewStopReason.Limit)]);
                var status = Assert.Single(LogicalTreeHelper.GetChildren((DependencyObject)sidebar.Content).OfType<TextBlock>(),
                    text => text.Text.Contains("Git refresh stopped", StringComparison.Ordinal));
                Assert.DoesNotContain("Showing previous results", status.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("No current review data", status.Text, StringComparison.OrdinalIgnoreCase);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Sustained_refresh_requests_pause_instead_of_running_forever()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode sustained Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fake = Path.Combine(root, "slow-git.cmd");
            await File.WriteAllTextAsync(fake, "@echo off\r\nping -n 2 127.0.0.1 > nul\r\necho fake\r\n");
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService(fake));
                sidebar.Select(root, "main");
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar)
                    .GetField("coordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(22);
                while (!coordinator.IsPaused && DateTime.UtcNow < deadline)
                {
                    sidebar.RequestRefresh();
                    await Task.Delay(90);
                }
                Assert.True(coordinator.IsPaused, "Sustained notifications did not stop automatic refresh.");
                Assert.Equal(ReviewStopReason.Limit, coordinator.Snapshot?.StopReason);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Expanded_commit_files_are_paged_instead_of_creating_hundreds_of_buttons()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode commit rows Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                var type = typeof(WorktreeReviewSidebar);
                type.GetField("head", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sidebar, "head");
                type.GetField("mainHead", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sidebar, "main");
                var commits = new[] { new ReviewCommit(new string('a', 40), "big commit", "Fixture", DateTimeOffset.Now, [], "many files") };
                var expanded = (HashSet<string>)type.GetField("expanded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                expanded.Add("commits");
                expanded.Add("commit:" + commits[0].Sha);
                var cache = (Dictionary<string, IReadOnlyList<ReviewFile>>)type.GetField("commitFiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                cache[commits[0].Sha] = Enumerable.Range(0, 500).Select(i => new ReviewFile($"file-{i:D4}.txt", null, ReviewGroup.Commit, ReviewStatus.Modified)).ToArray();
                type.GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [typeof(ReviewSnapshot)], null)!
                    .Invoke(sidebar, [new ReviewSnapshot(root, "head", "main", [], commits, false, null)]);
                var controls = FindButtons((DependencyObject)sidebar.Content).ToArray();
                Assert.InRange(controls.Length, 1, 205);
                Assert.Contains(controls, button => AutomationProperties.GetName(button) == "Next commit files");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }

    private static async Task RunGit(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        _ = await output;
    }

    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); } catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); done.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
