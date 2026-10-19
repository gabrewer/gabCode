using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
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
    public async Task Native_unavailable_keeps_retry_and_close_without_patch_fallback()
    {
        await OnSta(() =>
        {
            var view = new ReadOnlyDiffView("file.txt — Modified", null);
            var window = new Window { Content = view, Width = 500, Height = 300, ShowInTaskbar = false };
            window.Show();
            try
            {
                var retries = 0;
                view.RetryRequested += (_, _) => retries++;
                view.ShowUnavailable("unsupported encoding");
                view.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(view), block => block.Text.Contains("Diff unavailable: unsupported encoding", StringComparison.Ordinal));
                Assert.Empty(Descendants<TextBox>(view));
                Assert.Single(Descendants<Button>(view), button => AutomationProperties.GetName(button).StartsWith("Close diff", StringComparison.Ordinal));
                Assert.Single(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Retry selected review document")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, retries);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Compact_toolbar_keeps_search_code_and_close_right_aligned_with_or_without_current_file()
    {
        await OnSta(() =>
        {
            foreach (var currentFile in new string?[] { "C:\\review\\file.txt", null })
            {
                var view = new ReadOnlyDiffView("review/file.txt — HEAD → index", currentFile);
                var window = new Window { Content = view, Width = 340, Height = 320, ShowInTaskbar = false };
                window.Show();
                try
                {
                    view.UpdateLayout();
                    var buttons = Descendants<Button>(view).ToArray();
                    var search = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Find in read-only review document");
                    var code = Assert.Single(buttons, b => AutomationProperties.GetName(b).StartsWith("Open current worktree file in VS Code", StringComparison.Ordinal));
                    var close = Assert.Single(buttons, b => AutomationProperties.GetName(b).StartsWith("Close diff", StringComparison.Ordinal));
                    var next = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Next review change");
                    Assert.Equal(currentFile is not null, code.IsVisible);
                    Assert.True(search.TranslatePoint(new Point(), view).X > next.TranslatePoint(new Point(), view).X);
                    Assert.True(close.TranslatePoint(new Point(), view).X > search.TranslatePoint(new Point(), view).X);
                    Assert.InRange(view.ActualWidth - close.TranslatePoint(new Point(), view).X - close.ActualWidth, 9, 20);
                    if (code.IsVisible) Assert.True(close.TranslatePoint(new Point(), view).X > code.TranslatePoint(new Point(), view).X);
                    Assert.InRange(search.ActualWidth, 26, 29);
                    Assert.InRange(search.ActualHeight, 24, 27);
                    Assert.All(Descendants<System.Windows.Shapes.Path>(view), icon => Assert.Equal(16, icon.Width));
                }
                finally { window.Close(); }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Retained_native_document_marks_stale_without_silently_claiming_freshness()
    {
        await OnSta(() =>
        {
            var view = new ReadOnlyDiffView("file.txt — Modified", null);
            var window = new Window { Content = view, Width = 500, Height = 300, ShowInTaskbar = false };
            window.Show();
            try
            {
                view.MarkStale("Git read timed out; previous snapshot retained.");
                view.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(view), block => block.Text.Contains("stale", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Retry selected review document" && button.IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Review_mode_preference_round_trips_with_safe_default()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gabcode mode " + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "mode.txt");
        try
        {
            Assert.Equal("side-by-side", new DiffModePreference(path).Read());
            new DiffModePreference(path).Write("inline");
            Assert.Equal("inline", new DiffModePreference(path).Read());
            new DiffModePreference(path).Write("file");
            Assert.Equal("file", new DiffModePreference(path).Read());
            Assert.Throws<ArgumentOutOfRangeException>(() => new DiffModePreference(path).Write("edit"));
            File.WriteAllText(path, "invalid");
            Assert.Equal("side-by-side", new DiffModePreference(path).Read());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Staged_file_selection_opens_real_HEAD_to_index_in_embedded_viewer()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode staged viewer Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int? piPid = null, commandPid = null;
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "committed\n");
            await RunGit(root, "add", "selected.txt");
            await RunGit(root, "commit", "-m", "base");
            await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "staged\n");
            await RunGit(root, "add", "selected.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "worktree-only\n");
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
                    piPid = first.ProcessId; commandPid = second.ProcessId;
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(((ContentControl)window.FindName("ReviewSidebarRegion")).Content);
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Modified: selected.txt")));
                    var stage = Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Modified: selected.txt") &&
                        (b.Tag as string)?.StartsWith("Staged:", StringComparison.Ordinal) == true);
                    stage.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var view = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    await WaitUntil(() => view.HasStagedViewer);
                    var pair = await view.ReadViewerPairForTestAsync();
                    Assert.Equal("committed\n", pair.Before);
                    Assert.Equal("staged\n", pair.After);
                    Assert.Contains(Descendants<TextBlock>(view), block => block.Text.Contains("HEAD → index", StringComparison.Ordinal));
                    await WaitUntil(() => Descendants<TextBlock>(view).Any(block => block.Text == "1 of 1"));
                    Assert.Equal("worktree-only\n", await File.ReadAllTextAsync(Path.Combine(root, "selected.txt")));
                    var modes = Descendants<ToggleButton>(view).Where(button =>
                        AutomationProperties.GetName(button).StartsWith("Read-only diff mode:", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(3, modes.Length);
                    Assert.Single(modes, button => button.IsChecked is true);
                    Assert.All(modes, button => { Assert.NotNull(button.ToolTip); Assert.IsNotType<string>(button.Content); });
                    Assert.Contains(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Next review change");
                    Assert.Contains(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Find in read-only review document");
                    Assert.All(Descendants<Button>(view).Where(button => button.IsVisible), button => Assert.NotNull(button.ToolTip));
                    Assert.DoesNotContain(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Retry selected review document" && button.IsVisible);
                    Assert.DoesNotContain(Descendants<Button>(view), button => AutomationProperties.GetName(button) == "Focus read-only code comparison");
                    Assert.Equal(piPid, first.ProcessId); Assert.Equal(commandPid, second.ProcessId);
                    view.ApplyTemplate(); view.UpdateLayout();
                    Assert.Single(Descendants<Button>(view), b => AutomationProperties.GetName(b).StartsWith("Close diff", StringComparison.Ordinal))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.Equal(piPid, first.ProcessId); Assert.Equal(commandPid, second.ProcessId);
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
            await WaitUntil(() => HasExited(piPid!.Value) && HasExited(commandPid!.Value));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

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
                    var close = Assert.Single(Descendants<Button>(view), b => AutomationProperties.GetName(b).StartsWith("Close diff", StringComparison.Ordinal));
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
                    sidebar.RequestRefresh();
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => Equals(b.Header, "Unstaged (1)") && !ReferenceEquals(b, group)));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var refreshedGroup = Assert.Single(Descendants<Expander>(sidebar), b => Equals(b.Header, "Unstaged (1)"));
                    refreshedGroup.ApplyTemplate(); refreshedGroup.UpdateLayout();
                    var refreshedDisclosure = Assert.Single(Descendants<System.Windows.Controls.Primitives.ToggleButton>(refreshedGroup));
                    Assert.Same(refreshedDisclosure, Keyboard.FocusedElement);
                    Assert.True(refreshedGroup.IsExpanded);
                    PressSpace(refreshedDisclosure, window);
                    Assert.False(refreshedGroup.IsExpanded);
                    PressSpace(refreshedDisclosure, window);
                    Assert.True(refreshedGroup.IsExpanded);
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
                    Assert.Single(Descendants<Button>(diff), b => AutomationProperties.GetName(b).StartsWith("Close diff", StringComparison.Ordinal))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var restored = Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Untracked: added.txt"));
                    Assert.True(restored.IsKeyboardFocused);
                    restored.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var reopened = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    reopened.ApplyTemplate(); reopened.UpdateLayout();
                    await File.WriteAllTextAsync(Path.Combine(root, "added.txt"), "second\n");
                    await WaitUntil(() => reopened.HasStagedViewer);
                    var refreshedPair = await reopened.ReadViewerPairForTestAsync();
                    for (var attempt = 0; attempt < 100 && refreshedPair.After != "second\n"; attempt++)
                    {
                        await Task.Delay(50);
                        refreshedPair = await reopened.ReadViewerPairForTestAsync();
                    }
                    Assert.Equal("", refreshedPair.Before);
                    Assert.Equal("second\n", refreshedPair.After);
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
    public async Task Branch_changes_disclosure_opens_actual_base_to_tip_without_current_worktree_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode branch diff " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "base\n");
            await RunGit(root, "add", "file.txt");
            await RunGit(root, "commit", "-m", "base");
            await RunGit(root, "switch", "-c", "feature/review");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "tip\n");
            await RunGit(root, "commit", "-am", "tip");
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "working-only\n");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var sidebar = Assert.IsType<WorktreeReviewSidebar>(((ContentControl)window.FindName("ReviewSidebarRegion")).Content);
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => b.Header?.ToString()?.StartsWith("Branch changes", StringComparison.Ordinal) == true));
                    var section = Assert.Single(Descendants<Expander>(sidebar), b => b.Header?.ToString()?.StartsWith("Branch changes", StringComparison.Ordinal) == true);
                    section.IsExpanded = true;
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Modified: file.txt") &&
                        (b.Tag as string)?.StartsWith("Branch:", StringComparison.Ordinal) == true));
                    var button = Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Modified: file.txt") &&
                        (b.Tag as string)?.StartsWith("Branch:", StringComparison.Ordinal) == true);
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var view = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    await WaitUntil(() => view.HasStagedViewer);
                    var pair = await view.ReadViewerPairForTestAsync();
                    Assert.Equal("base\n", pair.Before);
                    Assert.Equal("tip\n", pair.After);
                    Assert.Contains(Descendants<TextBlock>(view), block => block.Text.Contains("base", StringComparison.OrdinalIgnoreCase) && block.Text.Contains("tip", StringComparison.OrdinalIgnoreCase));
                    await RunGit(root, "branch", "-f", "trunk", "HEAD");
                    sidebar.RequestRefresh();
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is TerminalSessionView);
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
                    Assert.True(commitToggle.Focus());
                    sidebar.RequestRefresh();
                    await WaitUntil(() => Descendants<Expander>(sidebar).Any(b => b.Tag is string key && key.StartsWith("commit:", StringComparison.Ordinal) && !ReferenceEquals(b, commit)));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var refreshedCommit = Assert.Single(Descendants<Expander>(sidebar), b => b.Tag is string key && key.StartsWith("commit:", StringComparison.Ordinal));
                    refreshedCommit.ApplyTemplate(); refreshedCommit.UpdateLayout();
                    var refreshedToggle = Assert.Single(Descendants<System.Windows.Controls.Primitives.ToggleButton>(refreshedCommit));
                    Assert.Same(refreshedToggle, Keyboard.FocusedElement);
                    Assert.True(refreshedCommit.IsExpanded);
                    PressSpace(refreshedToggle, window);
                    Assert.False(refreshedCommit.IsExpanded);
                    PressSpace(refreshedToggle, window);
                    Assert.True(refreshedCommit.IsExpanded);
                    await WaitUntil(() => Descendants<Button>(sidebar).Any(b => Equals(b.Content, "Modified: committed.txt")));
                    Assert.Single(Descendants<Button>(sidebar), b => Equals(b.Content, "Modified: committed.txt"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await WaitUntil(() => ((ContentControl)window.FindName("MainTerminalRegion")).Content is ReadOnlyDiffView);
                    var diff = (ReadOnlyDiffView)((ContentControl)window.FindName("MainTerminalRegion")).Content;
                    diff.ApplyTemplate(); diff.UpdateLayout();
                    Assert.Contains(Descendants<TextBlock>(diff), block => block.Text.Contains("first parent", StringComparison.Ordinal));
                    await WaitUntil(() => diff.HasStagedViewer);
                    var pair = await diff.ReadViewerPairForTestAsync();
                    Assert.Equal("base\n", pair.Before);
                    Assert.Equal("updated\n", pair.After);
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
    public async Task Watcher_failure_recovery_clears_warning_after_success_and_does_not_overlap_git_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode watcher recovery " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "trunk");
                Assert.Equal(1, sidebar.WatcherCount); // .git is already inside the recursive worktree watcher.
                await WaitUntil(() => sidebar.CurrentWatcherWarning is null);
                sidebar.RecoverWatcherFailure();
                Assert.Null(sidebar.CurrentWatcherWarning);
                Assert.Equal(1, sidebar.WatcherCount);
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Missing_watcher_root_stays_degraded_until_periodic_retry_can_watch_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode watcher retry Ω " + Guid.NewGuid().ToString("N"));
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                sidebar.Select(root, "trunk");
                Assert.NotNull(sidebar.CurrentWatcherWarning);
                Assert.Equal(0, sidebar.WatcherCount);
                sidebar.RecoverWatcherFailure();
                Assert.NotNull(sidebar.CurrentWatcherWarning);
                Assert.Equal(0, sidebar.WatcherCount);
                // Exercise the real periodic retry, accelerated without replacing its handler.
                var poll = (DispatcherTimer)typeof(WorktreeReviewSidebar)
                    .GetField("poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                Directory.CreateDirectory(root);
                poll.Interval = TimeSpan.FromMilliseconds(50);
                await WaitUntil(() => sidebar.CurrentWatcherWarning is null && sidebar.WatcherCount == 1);
                sidebar.Suspend();
                Assert.Equal(0, sidebar.WatcherCount);
                sidebar.RecoverWatcherFailure();
                Assert.Equal(0, sidebar.WatcherCount);
            });
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Queued_errors_from_retired_watchers_do_not_restart_current_watchers()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode stale watcher " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                var field = typeof(WorktreeReviewSidebar).GetField("watchers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var onError = typeof(FileSystemWatcher).GetMethod("OnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                sidebar.Select(root, "trunk");
                var old = Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!);
                onError.Invoke(old, [new ErrorEventArgs(new InternalBufferOverflowException("injected"))]);
                // Restart before the old error reaches the UI dispatcher.
                sidebar.RecoverWatcherFailure();
                var current = Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!);
                Assert.NotSame(old, current);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(current, Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!));
                Assert.Null(sidebar.CurrentWatcherWarning);
                onError.Invoke(current, [new ErrorEventArgs(new InternalBufferOverflowException("injected"))]);
                sidebar.Dispose();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, sidebar.WatcherCount);
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Real_file_bursts_reconcile_and_later_edits_are_still_observed()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode real burst Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await RunGit(root, "commit", "--allow-empty", "-m", "base");
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar)
                    .GetField("coordinator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                sidebar.Select(root, "trunk");
                await WaitUntil(() => coordinator.Snapshot?.IsStale is false);
                await Task.Run(() => Parallel.For(0, 500, i => File.WriteAllText(Path.Combine(root, $"file-{i}.txt"), "first\n")));
                await WaitUntil(() => coordinator.Snapshot is { IsStale: false, Working.Count: 500 });
                await Task.Run(() => Parallel.For(0, 500, i => File.Delete(Path.Combine(root, $"file-{i}.txt"))));
                await WaitUntil(() => coordinator.Snapshot is { IsStale: false, Working.Count: 0 });
                await File.WriteAllTextAsync(Path.Combine(root, "later.txt"), "still watching\n");
                await WaitUntil(() => coordinator.Snapshot?.Working.Any(f => f.Path == "later.txt") is true);
                Assert.Equal(1, sidebar.WatcherCount);
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Overflow_burst_does_not_cancel_an_inflight_read_or_queue_one_ui_callback_per_event()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode busy watcher " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar).GetField("coordinator", flags)!.GetValue(sidebar)!;
                var blocked = new TaskCompletionSource<ReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                CancellationToken busyToken = default;
                Func<string, string?, CancellationToken, Task<ReviewSnapshot>> read = (path, _, token) =>
                {
                    ++calls;
                    if (calls == 2) { busyToken = token; return blocked.Task; }
                    return Task.FromResult(new ReviewSnapshot(path, "read-" + calls, "main", [], [], false, null));
                };
                typeof(WorktreeChangeReviewCoordinator).GetField("read", flags)!.SetValue(coordinator, read);
                sidebar.Select(root, "trunk");
                await WaitUntil(() => coordinator.Snapshot?.Head == "read-1");
                sidebar.RequestRefresh();
                Assert.Equal(2, calls);
                var watcher = Assert.Single((List<FileSystemWatcher>)typeof(WorktreeReviewSidebar).GetField("watchers", flags)!.GetValue(sidebar)!);
                var onError = typeof(FileSystemWatcher).GetMethod("OnError", flags)!;
                var onChanged = typeof(FileSystemWatcher).GetMethod("OnChanged", flags)!;
                var posted = 0;
                System.Windows.Threading.DispatcherHookEventHandler hook = (_, _) => Interlocked.Increment(ref posted);
                Dispatcher.CurrentDispatcher.Hooks.OperationPosted += hook;
                try
                {
                    for (var i = 0; i < 10_000; ++i)
                        onChanged.Invoke(watcher, [new FileSystemEventArgs(WatcherChangeTypes.Changed, root, "file.txt")]);
                    for (var i = 0; i < 100; ++i)
                        onError.Invoke(watcher, [new ErrorEventArgs(new InternalBufferOverflowException("burst"))]);
                }
                finally { Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= hook; }
                Assert.InRange(posted, 0, 1);
                await WaitUntil(() => sidebar.CurrentWatcherWarning is not null);
                Assert.False(busyToken.IsCancellationRequested);
                Assert.Equal(2, calls);
                blocked.SetResult(new ReviewSnapshot(root, "busy-result", "main", [], [], false, null));
                await WaitUntil(() => calls >= 3 && sidebar.CurrentWatcherWarning is null);
                Assert.Equal("read-" + calls, coordinator.Snapshot?.Head);
                Assert.Same(watcher, Assert.Single((List<FileSystemWatcher>)typeof(WorktreeReviewSidebar).GetField("watchers", flags)!.GetValue(sidebar)!));
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Fatal_watcher_error_disables_watcher_until_periodic_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode fatal watcher " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                var field = typeof(WorktreeReviewSidebar).GetField("watchers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var onError = typeof(FileSystemWatcher).GetMethod("OnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var poll = (DispatcherTimer)typeof(WorktreeReviewSidebar)
                    .GetField("poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sidebar)!;
                sidebar.Select(root, "trunk");
                var watcher = Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!);
                for (var i = 0; i < 100; ++i)
                    onError.Invoke(watcher, [new ErrorEventArgs(new InternalBufferOverflowException("before fatal"))]);
                onError.Invoke(watcher, [new ErrorEventArgs(new IOException("injected fatal"))]);
                onError.Invoke(watcher, [new ErrorEventArgs(new InternalBufferOverflowException("after fatal"))]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, sidebar.WatcherCount);
                Assert.Contains("injected fatal", sidebar.CurrentWatcherWarning);
                poll.Interval = TimeSpan.FromMilliseconds(50);
                await WaitUntil(() => sidebar.CurrentWatcherWarning is null && sidebar.WatcherCount == 1);
                Assert.NotSame(watcher, Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!));
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Overflow_reconciles_from_git_without_restarting_a_live_watcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode overflow burst " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await RunGit(root, "config", "user.email", "test@example.invalid");
            await RunGit(root, "config", "user.name", "Test");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
            await RunGit(root, "add", "."); await RunGit(root, "commit", "-m", "base");
            await OnSta(async () =>
            {
                using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
                var field = typeof(WorktreeReviewSidebar).GetField("watchers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var onError = typeof(FileSystemWatcher).GetMethod("OnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                sidebar.Select(root, "trunk");
                await WaitUntil(() => sidebar.CurrentWatcherWarning is null && sidebar.WatcherCount == 1);
                var watcher = Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!);
                onError.Invoke(watcher, [new ErrorEventArgs(new InternalBufferOverflowException("injected"))]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(watcher, Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!));
                await WaitUntil(() => sidebar.CurrentWatcherWarning is null);
                Assert.Same(watcher, Assert.Single((List<FileSystemWatcher>)field.GetValue(sidebar)!));
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
    private static void PressSpace(UIElement target, Window window)
    {
        foreach (var routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = routed });
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
