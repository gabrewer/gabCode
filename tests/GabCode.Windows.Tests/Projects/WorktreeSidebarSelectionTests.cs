using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class WorktreeSidebarSelectionTests
{
    [Fact]
    public async Task Selecting_another_worktree_moves_the_checkmark_and_project_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode sidebar selection Ω", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "primary");
        var feature = Path.Combine(root, "feature worktree");
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "-b", "trunk");
            RunGit(repository, "config", "user.email", "tests@gabcode.local");
            RunGit(repository, "config", "user.name", "gabCode Tests");
            File.WriteAllText(Path.Combine(repository, "README.md"), "fixture");
            RunGit(repository, "add", "README.md");
            RunGit(repository, "commit", "-m", "fixture");
            RunGit(repository, "worktree", "add", "-b", "feature/sidebar", feature);

            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow(
                    new ProjectContext("Demo", repository),
                    CreateCmdResolver(),
                    new CloseTerminalsConfirmation());
                try
                {
                    var list = Assert.IsType<ListBox>(window.FindName("WorktreeList"));
                    await WaitUntilAsync(() => WorktreeItems(list).Count == 2);

                    var initialPrimary = FindItem(list, repository);
                    Assert.Contains("selected", AutomationProperties.GetName(SidebarContent(initialPrimary)), StringComparison.Ordinal);

                    list.SelectedItem = FindItem(list, feature);

                    Assert.Equal(WorktreePath.Normalize(feature), window.ProjectFolder);
                    var selectedFeature = FindItem(list, feature);
                    var deselectedPrimary = FindItem(list, repository);
                    Assert.Same(selectedFeature, list.SelectedItem);
                    Assert.Contains("selected", AutomationProperties.GetName(SidebarContent(selectedFeature)), StringComparison.Ordinal);
                    Assert.DoesNotContain("selected", AutomationProperties.GetName(SidebarContent(deselectedPrimary)), StringComparison.Ordinal);
                    Assert.Contains(Path.GetFileName(feature), window.Title, StringComparison.Ordinal);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            try { if (Directory.Exists(feature)) RunGit(repository, "worktree", "remove", "--force", feature); }
            catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Selected_worktree_pair_becomes_orphaned_after_two_missing_reconciliations()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode orphan ownership Ω", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "primary");
        var feature = Path.Combine(root, "feature worktree");
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "-b", "trunk");
            RunGit(repository, "config", "user.email", "tests@gabcode.local");
            RunGit(repository, "config", "user.name", "gabCode Tests");
            File.WriteAllText(Path.Combine(repository, "README.md"), "fixture");
            RunGit(repository, "add", "README.md");
            RunGit(repository, "commit", "-m", "fixture");
            RunGit(repository, "worktree", "add", "-b", "feature/orphan", feature);

            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow(
                    new ProjectContext("Demo", repository),
                    CreateCmdResolver(),
                    new CloseTerminalsConfirmation());
                try
                {
                    var list = Assert.IsType<ListBox>(window.FindName("WorktreeList"));
                    await WaitUntilAsync(() => WorktreeItems(list).Count == 2);
                    list.SelectedItem = FindItem(list, feature);
                    await WaitUntilAsync(() => WorktreePath.Comparer.Equals(window.ProjectFolder, feature));

                    var stateField = typeof(MainWindow).GetField("worktreeState", BindingFlags.Instance | BindingFlags.NonPublic);
                    var state = Assert.IsType<WorktreeNavigationState>(stateField?.GetValue(window));
                    var primary = new RegisteredWorktree(repository, "trunk", IsPrimary: true);

                    state.Reconcile([primary]);
                    state.Reconcile([primary]);

                    var orphan = Assert.Single(state.Orphaned, entry => WorktreePath.Comparer.Equals(entry.Path, feature));
                    Assert.True(orphan.HasTerminalPair);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            try { if (Directory.Exists(feature)) RunGit(repository, "worktree", "remove", "--force", feature); }
            catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<ListBoxItem> WorktreeItems(ListBox list) =>
        list.Items.OfType<ListBoxItem>().Where(item => item.Tag is WorktreeNavigationEntry).ToArray();

    private static ListBoxItem FindItem(ListBox list, string path) =>
        Assert.Single(WorktreeItems(list), item => item.Tag is WorktreeNavigationEntry entry && WorktreePath.Comparer.Equals(entry.Path, path));

    private static WorktreeSidebarItem SidebarContent(ListBoxItem item) =>
        Assert.IsType<WorktreeSidebarItem>(item.Content);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The worktree sidebar did not populate within ten seconds.");
            await Task.Delay(25);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), $"git {string.Join(' ', arguments)} timed out.");
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {standardError}{standardOutput}");
    }

    private static TerminalProfileResolver CreateCmdResolver() => new(
        [],
        [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);

    private static async Task RunOnStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); done.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CloseTerminalsConfirmation : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
