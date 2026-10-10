using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class ReadOnlyDiffWindowTests
{
    [Fact]
    public async Task Opening_and_replacing_document_keeps_live_shells_and_close_restores_original_main()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode diff window Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int? ownedPi = null, ownedCommands = null;
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "one\n");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var first = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var second = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => first.ProcessId is not null && second.ProcessId is not null);
                    var firstPid = first.ProcessId;
                    var secondPid = second.ProcessId;
                    ownedPi = firstPid; ownedCommands = secondPid;
                    window.ShowCommandsInMain();
                    await window.OpenReviewDiffAsync(new ReviewFile("notes.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked));
                    var main = Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion"));
                    var bottom = Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion"));
                    Assert.IsType<ReadOnlyDiffView>(main.Content);
                    var tabs = Assert.IsType<TabControl>(bottom.Content);
                    Assert.Same(first, Assert.IsType<TabItem>(tabs.Items[0]).Content);
                    Assert.Same(second, Assert.IsType<TabItem>(tabs.Items[1]).Content);
                    tabs.SelectedIndex = 0;
                    await window.OpenReviewDiffAsync(new ReviewFile("notes.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked));
                    Assert.Same(tabs, bottom.Content);
                    Assert.Equal(firstPid, first.ProcessId);
                    Assert.Equal(secondPid, second.ProcessId);
                    var view = Assert.IsType<ReadOnlyDiffView>(main.Content);
                    view.ApplyTemplate();
                    view.UpdateLayout();
                    var close = Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Close Diff"));
                    await WaitUntil(() => close.IsKeyboardFocused);
                    close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.Same(second, main.Content);
                    Assert.Same(first, bottom.Content);
                    Assert.Equal(firstPid, first.ProcessId);
                    Assert.Equal(secondPid, second.ProcessId);
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
            Assert.NotNull(ownedPi); Assert.NotNull(ownedCommands);
            await WaitUntil(() => HasExited(ownedPi.Value) && HasExited(ownedCommands.Value));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static bool HasExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public async Task Sidebar_shows_selected_worktree_groups_and_opens_a_file_diff()
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"))));
        var missing = Assert.Single(cases.RootElement.GetProperty("cases").EnumerateArray(), row => row.GetProperty("id").GetString() == "selected-file-disappears-on-refresh");
        Assert.False(missing.GetProperty("expected").GetProperty("documentOpen").GetBoolean());
        var root = Path.Combine(Path.GetTempPath(), "gabcode sidebar " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await RunGit(root, "add", "base.txt");
            await RunGit(root, "commit", "-m", "base");
            await File.WriteAllTextAsync(Path.Combine(root, "added.txt"), "new\n");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var region = Assert.IsType<ContentControl>(window.FindName("ReviewSidebarRegion"));
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(region.Content);
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Unstaged (1)")));
                    var group = Assert.Single(Descendants<Expander>(sidebar), b => Equals(b.Header, "Unstaged (1)"));
                    Assert.Same(window.FindResource("CompactReviewExpanderStyle"), group.Style);
                    var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(group);
                    Assert.NotNull(peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse));
                    Assert.Equal("Unstaged (1)", peer.GetName());
                    group.ApplyTemplate(); group.UpdateLayout();
                    var disclosure = Assert.Single(Descendants<System.Windows.Controls.Primitives.ToggleButton>(group));
                    Assert.True(disclosure.Focus());
                    Assert.True(disclosure.IsKeyboardFocused);
                    disclosure.IsChecked = false;
                    Assert.False(group.IsExpanded);
                    disclosure.IsChecked = true;
                    Assert.True(group.IsExpanded);
                    Assert.True(disclosure.IsKeyboardFocused);
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Untracked: added.txt")));
                    var file = Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Untracked: added.txt"));
                    file.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => window.FindName("MainTerminalRegion") is ContentControl { Content: ReadOnlyDiffView });
                    var diff = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    diff.ApplyTemplate(); diff.UpdateLayout();
                    Assert.Contains(Descendants<TextBlock>(diff), text => text.Text.Contains("added.txt", StringComparison.Ordinal));
                    sidebar.RequestRefresh();
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Untracked: added.txt") && !ReferenceEquals(b, file)));
                    diff.ApplyTemplate(); diff.UpdateLayout();
                    Assert.Single(Descendants<Button>(diff), b => Equals(b.Content, "Close Diff"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var restored = Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Untracked: added.txt"));
                    Assert.True(restored.IsKeyboardFocused);
                    restored.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var reopened = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    reopened.ApplyTemplate(); reopened.UpdateLayout();
                    await File.WriteAllTextAsync(Path.Combine(root, "added.txt"), "second\n");
                    await WaitUntil(() => Descendants<TextBox>(reopened).Any(box => box.Text.Contains("+second", StringComparison.Ordinal)));
                    Assert.Same(reopened, ((ContentControl)window.FindName("MainTerminalRegion")).Content);
                    File.Delete(Path.Combine(root, "added.txt"));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is TerminalSessionView);
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Unstaged (0)") && b.IsExpanded));
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Sidebar_expands_commit_files_on_demand_and_opens_the_first_parent_diff()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode commit sidebar " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "committed.txt"), "base\n");
            await RunGit(root, "add", "committed.txt");
            await RunGit(root, "commit", "-m", "base");
            await RunGit(root, "checkout", "-b", "feature/review");
            await File.WriteAllTextAsync(Path.Combine(root, "committed.txt"), "updated\n");
            await RunGit(root, "commit", "-am", "change subject");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(((ContentControl)window.FindName("ReviewSidebarRegion")).Content);
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Commits ahead of main (1)")));
                    Assert.Single(Descendants<Expander>(sidebar), b => Equals(b.Header, "Commits ahead of main (1)")).IsExpanded = true;
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("change subject", StringComparison.Ordinal)));
                    var commit = Assert.Single(Descendants<Expander>(sidebar), b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("change subject", StringComparison.Ordinal));
                    Assert.Same(window.FindResource("CompactReviewExpanderStyle"), commit.Style);
                    commit.ApplyTemplate(); commit.UpdateLayout();
                    var commitToggle = Assert.Single(Descendants<System.Windows.Controls.Primitives.ToggleButton>(commit));
                    commitToggle.IsChecked = true;
                    Assert.True(commit.IsExpanded);
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Modified: committed.txt")));
                    Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Modified: committed.txt"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var diff = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    diff.ApplyTemplate(); diff.UpdateLayout();
                    Assert.Contains(Descendants<TextBlock>(diff), block => block.Text.Contains("first parent", StringComparison.Ordinal));
                    await WaitUntil(() => Descendants<TextBox>(diff).Any(box => box.Text.Contains("+updated", StringComparison.Ordinal)));
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Selected_worktree_watchers_do_not_accumulate_across_switches_and_dispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode watcher handles " + Guid.NewGuid().ToString("N"));
        var linked = root + " linked";
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await RunGit(root, "add", "."); await RunGit(root, "commit", "-m", "base");
            await RunGit(root, "worktree", "add", "-b", "feature/watch", linked);
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                for (var i = 0; i < 6; i++)
                {
                    sidebar.Select(i % 2 == 0 ? root : linked, "trunk");
                    Assert.InRange(sidebar.WatcherCount, 1, 3);
                    await Task.Delay(20);
                }
                sidebar.Suspend();
                Assert.Equal(0, sidebar.WatcherCount);
                sidebar.Select(root, "trunk");
                Assert.InRange(sidebar.WatcherCount, 1, 3);
                sidebar.Dispose();
                Assert.Equal(0, sidebar.WatcherCount);
                Assert.True(sidebar.IsDisposed);
                sidebar.RequestRefresh();
                Assert.Equal(0, sidebar.WatcherCount);
            });
        }
        finally
        {
            try { if (Directory.Exists(linked)) Directory.Delete(linked, true); Directory.Delete(root, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Shared_reconciliation_cases_keep_valid_commit_context_and_drop_rewritten_history()
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"))));
        var fixtureRows = cases.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!);
        Assert.True(fixtureRows["unrelated-refresh-preserves-review-context"].GetProperty("expected").GetProperty("documentOpen").GetBoolean());
        Assert.False(fixtureRows["rewritten-history-drops-old-selection"].GetProperty("expected").GetProperty("documentOpen").GetBoolean());
        var root = Path.Combine(Path.GetTempPath(), "gabcode rewritten review " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await RunGit(root, "add", "."); await RunGit(root, "commit", "-m", "base");
            await RunGit(root, "switch", "-c", "feature/rewrite");
            await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "committed\n");
            await RunGit(root, "add", "."); await RunGit(root, "commit", "-m", "old commit");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(((ContentControl)window.FindName("ReviewSidebarRegion")).Content);
                    var main = Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion"));
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Commits ahead of main (1)")));
                    Assert.Single(Descendants<Expander>(sidebar), b => Equals(b.Header, "Commits ahead of main (1)")).IsExpanded = true;
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("old commit")));
                    Assert.Single(Descendants<Expander>(sidebar), b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("old commit")).IsExpanded = true;
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Added: selected.txt")));
                    Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Added: selected.txt")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => main.Content is ReadOnlyDiffView);
                    var selectedDiff = main.Content;
                    await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "unrelated\n");
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Unstaged (1)")));
                    Assert.Same(selectedDiff, main.Content);
                    Assert.Contains(Descendants<Expander>(sidebar), b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("old commit") && b.IsExpanded);
                    await RunGit(root, "reset", "--hard", "trunk");
                    await WaitUntil(() => main.Content is TerminalSessionView && Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Commits ahead of main (0)")));
                    Assert.DoesNotContain(Descendants<Expander>(sidebar), b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("old commit"));
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Linked_worktree_review_reconciles_when_shared_local_main_ref_moves_externally()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode common refs " + Guid.NewGuid().ToString("N"));
        var linked = root + " linked";
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await RunGit(root, "add", "."); await RunGit(root, "commit", "-m", "base");
            await RunGit(root, "worktree", "add", "-b", "feature/review", linked);
            await File.WriteAllTextAsync(Path.Combine(linked, "change.txt"), "branch\n");
            await RunGit(linked, "add", "."); await RunGit(linked, "commit", "-m", "feature");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", linked, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(((ContentControl)window.FindName("ReviewSidebarRegion")).Content);
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Commits ahead of main (1)")));
                    await RunGit(root, "update-ref", "refs/heads/trunk", "feature/review");
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Commits ahead of main (0)")));
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally
        {
            try { if (Directory.Exists(linked)) Directory.Delete(linked, true); Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void External_open_requires_a_real_current_file_within_selected_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode diff paths " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "actual.txt");
            File.WriteAllText(existing, "x");
            Assert.Equal(existing, ReadOnlyDiffView.CurrentFile(root, new ReviewFile("actual.txt", null, ReviewGroup.Commit, ReviewStatus.Modified, "sha")));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("../escape.txt", null, ReviewGroup.Unstaged, ReviewStatus.Modified)));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("actual.txt", null, ReviewGroup.Commit, ReviewStatus.Deleted, "sha")));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("missing.txt", null, ReviewGroup.Staged, ReviewStatus.Added)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20); }
    }
    private static async Task RunGit(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
    }
    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); } catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); completion.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class AcceptClose : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
