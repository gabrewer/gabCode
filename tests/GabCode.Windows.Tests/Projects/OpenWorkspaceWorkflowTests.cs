using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class OpenWorkspaceWorkflowTests
{
    [Fact]
    public async Task Opening_a_valid_workspace_in_an_empty_window_activates_the_descriptor_worktree()
    {
        var root = CreateRepository("empty open Ω");
        var descriptor = WriteWorkspace(root, "Demo", "main");

        try
        {
            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow();
                try
                {
                    Assert.True(await window.OpenWorkspaceAsync(descriptor));
                    Assert.Equal(Path.GetFullPath(root), window.ProjectFolder);
                    Assert.Contains("Demo", window.Title, StringComparison.Ordinal);
                    Assert.NotNull(window.PiTerminal);
                    Assert.NotNull(window.CommandsTerminal);
                }
                finally { window.Close(); }
            });
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Opening_accepts_an_unattached_local_main_branch_and_uses_the_primary_worktree()
    {
        var root = CreateRepository("unattached main Ω");
        var descriptor = WriteWorkspace(root, "Demo", "main");
        RunGit(root, "checkout", "-b", "feature/primary");
        try
        {
            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow();
                try
                {
                    Assert.True(await window.OpenWorkspaceAsync(descriptor));
                    Assert.Equal(Path.GetFullPath(root), window.ProjectFolder);
                    Assert.Contains("Demo", window.Title, StringComparison.Ordinal);
                }
                finally { window.Close(); }
            });
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Opening_from_an_occupied_window_launches_once_and_preserves_the_current_project()
    {
        var current = CreateRepository("occupied current");
        var requested = CreateRepository("occupied requested");
        var descriptor = WriteWorkspace(requested, "Requested", "main");
        var launcher = new RecordingLauncher();

        try
        {
            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow(
                    new ProjectContext("Current", current),
                    CreateCmdResolver(),
                    new CloseTerminalsConfirmation(),
                    launcher);
                try
                {
                    var originalFolder = window.ProjectFolder;
                    Assert.True(await window.OpenWorkspaceAsync(descriptor));
                    Assert.Equal(originalFolder, window.ProjectFolder);
                    Assert.Equal(new[] { Path.GetFullPath(descriptor) }, launcher.Paths);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            TryDelete(current);
            TryDelete(requested);
        }
    }

    [Fact]
    public async Task Launcher_failure_preserves_the_current_project_and_shows_recovery()
    {
        var current = CreateRepository("launcher failure current");
        var requested = CreateRepository("launcher failure requested");
        var descriptor = WriteWorkspace(requested, "Requested", "main");
        var launcher = new RecordingLauncher(new InvalidOperationException("launcher unavailable"));

        try
        {
            await RunOnStaAsync(async () =>
            {
                var window = new MainWindow(
                    new ProjectContext("Current", current),
                    CreateCmdResolver(),
                    new CloseTerminalsConfirmation(),
                    launcher);
                try
                {
                    Assert.False(await window.OpenWorkspaceAsync(descriptor));
                    Assert.Equal(Path.GetFullPath(current), window.ProjectFolder);
                    Assert.Equal(Visibility.Visible, window.FindName("WorktreeFailureSurface") is FrameworkElement surface ? surface.Visibility : Visibility.Collapsed);
                    Assert.Contains("launcher unavailable", ((System.Windows.Controls.TextBlock)window.FindName("WorktreeFailureMessage")!).Text, StringComparison.Ordinal);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            TryDelete(current);
            TryDelete(requested);
        }
    }

    private static string CreateRepository(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode open workspace", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@gabcode.local");
        RunGit(root, "config", "user.name", "gabCode Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "fixture");
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "fixture");
        return root;
    }

    private static string WriteWorkspace(string project, string name, string branch)
    {
        var descriptor = Path.Combine(project, $"{name} Ω.gabcode-workspace");
        var json = $$"""
{
  "version": 1,
  "name": "{{name}}",
  "project": {
    "path": ".",
    "mainBranch": "{{branch}}"
  }
}
""";
        File.WriteAllText(descriptor, json);
        return descriptor;
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), $"git {string.Join(' ', arguments)} timed out.");
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
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

    private sealed class RecordingLauncher(Exception? failure = null) : IGabCodeInstanceLauncher
    {
        private readonly Exception? failure = failure;
        internal List<string> Paths { get; } = [];
        public void Launch(string workspacePath)
        {
            Paths.Add(workspacePath);
            if (failure is not null) throw failure;
        }
    }

    private sealed class CloseTerminalsConfirmation : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
