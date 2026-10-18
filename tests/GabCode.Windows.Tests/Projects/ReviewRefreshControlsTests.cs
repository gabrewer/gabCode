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
    public async Task Routine_review_state_uses_worktree_activity_instead_of_a_button_or_status_label()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode quiet review Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                Assert.True(sidebar.IsRefreshing);
                var type = typeof(WorktreeReviewSidebar);
                var spinner = Assert.IsType<System.Windows.Shapes.Path>(type.GetField("refreshIcon",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar));
                Assert.Equal(Visibility.Visible, spinner.Visibility);
                var name = Assert.IsType<TextBlock>(type.GetField("worktreeNameText",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar));
                Assert.Equal(Path.GetFileName(root), name.Text);
                Assert.Equal(Visibility.Visible, name.Visibility);
                var nameRow = Assert.IsType<Grid>(name.Parent);
                Assert.Contains(spinner, nameRow.Children.OfType<System.Windows.Shapes.Path>());
                Assert.Same(nameRow, name.Parent);
                Assert.Equal(0, Grid.GetColumn(name));
                Assert.Equal(1, Grid.GetColumn(spinner));
                Assert.Equal(HorizontalAlignment.Right, spinner.HorizontalAlignment);
                Assert.Equal(GridUnitType.Star, nameRow.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(22, nameRow.ColumnDefinitions[1].Width.Value);
                var dock = Assert.IsType<DockPanel>(sidebar.Content);
                dock.Measure(new Size(380, 600));
                dock.Arrange(new Rect(0, 0, 380, 600));
                var nameTop = nameRow.TranslatePoint(new Point(0, 0), dock).Y;
                var scroller = Assert.Single(dock.Children.OfType<ScrollViewer>());
                var contentTop = scroller.TranslatePoint(new Point(0, 0), dock).Y;
                Assert.InRange(nameRow.ActualHeight, 24, 35);
                Assert.DoesNotContain(FindButtons((DependencyObject)sidebar.Content), button =>
                    AutomationProperties.GetName(button) == "Refresh selected worktree changes and commits");
                typeof(WorktreeReviewSidebar).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [typeof(ReviewSnapshot)], null)!.Invoke(sidebar,
                    [new ReviewSnapshot(root, "head", "main", [], [], false, null)]);
                var status = Assert.Single(LogicalTreeHelper.GetChildren((DependencyObject)sidebar.Content).OfType<TextBlock>(),
                    text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                Assert.True(string.IsNullOrEmpty(status.Text));
                Assert.Equal(Path.GetFileName(root), name.Text);
                Assert.Same(nameRow, name.Parent);
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                dock.Measure(new Size(380, 600));
                dock.Arrange(new Rect(0, 0, 380, 600));
                Assert.Equal(nameTop, nameRow.TranslatePoint(new Point(0, 0), dock).Y);
                Assert.Equal(contentTop, scroller.TranslatePoint(new Point(0, 0), dock).Y);
                var cancel = Assert.Single(FindButtons((DependencyObject)sidebar.Content), button =>
                    AutomationProperties.GetName(button) == "Cancel review refresh");
                cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                while (sidebar.IsRefreshing && DateTime.UtcNow < deadline) await Task.Delay(25);
                Assert.False(sidebar.IsRefreshing);
                Assert.Equal(Visibility.Hidden, spinner.Visibility);
                Assert.Equal(Path.GetFileName(root), name.Text);
                Assert.Same(nameRow, name.Parent);
                Assert.Equal(Visibility.Visible, Assert.Single(FindButtons((DependencyObject)sidebar.Content), button =>
                    AutomationProperties.GetName(button) == "Retry review refresh").Visibility);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Suspending_review_clears_busy_state_without_starting_another_refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode suspend review Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                Assert.True(sidebar.IsRefreshing);
                var spinner = Assert.IsType<System.Windows.Shapes.Path>(typeof(WorktreeReviewSidebar).GetField("refreshIcon",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar));
                sidebar.Suspend();
                Assert.False(sidebar.IsRefreshing);
                Assert.Equal(Visibility.Hidden, spinner.Visibility);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Repeated_load_more_requests_keep_only_one_page_read_in_flight()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode page gate Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "main");
                var expanded = (HashSet<string>)typeof(WorktreeReviewSidebar).GetField("expanded",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                expanded.Add("commits");
                var commits = Enumerable.Range(0, 100).Select(i => new ReviewCommit(
                    i.ToString("x40"), "commit", "author", DateTimeOffset.UtcNow, [], "summary")).ToArray();
                typeof(WorktreeReviewSidebar).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [typeof(ReviewSnapshot)], null)!.Invoke(sidebar,
                    [new ReviewSnapshot(root, "head", "main", [], commits, false, null)]);
                var more = Assert.Single(FindButtons((DependencyObject)sidebar.Content), b => b.Content?.ToString() == "Load more commits");
                more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(more.IsEnabled); // Additional clicks must not queue another Git read.
                var load = typeof(WorktreeReviewSidebar).GetMethod("LoadMoreAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                for (var i = 0; i < 100; i++)
                    Assert.True(((Task)load.Invoke(sidebar, null)!).IsCompletedSuccessfully,
                        "Duplicate lazy requests should return immediately, not queue for capacity.");
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
