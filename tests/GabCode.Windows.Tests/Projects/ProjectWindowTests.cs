using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class ProjectWindowTests
{
    [Fact]
    public async Task Activated_workspace_sets_title_identity_and_terminal_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gabCode project Ω", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var workspace = new ProjectContext("Demo", directory);
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow(
                workspace,
                CreateCmdResolver(),
                new CloseTerminalsConfirmation());
            Assert.Equal($"Demo — {Path.GetFileName(directory)} — gabCode", window.Title);
            Assert.Equal(window.Title, AutomationProperties.GetName(window));
            Assert.Equal(directory, window.ProjectFolder);
            Assert.NotNull(window.PiTerminal);
            Assert.NotNull(window.CommandsTerminal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Invalid_workspace_recovery_keeps_empty_actions_and_starts_no_terminals()
    {
        await RunOnStaAsync(async () =>
        {
            var window = new MainWindow();
            var opened = await window.OpenWorkspaceAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gabcode-workspace"));
            Assert.False(opened);
            Assert.Null(window.PiTerminal);
            Assert.Equal(Visibility.Visible, window.FindName("EmptyProjectSurface") is FrameworkElement surface ? surface.Visibility : Visibility.Collapsed);
            Assert.Equal("Workspace could not be opened", ((System.Windows.Controls.TextBlock)window.FindName("EmptyProjectHeading")!).Text);
            var message = ((System.Windows.Controls.TextBlock)window.FindName("EmptyProjectMessage")!).Text;
            Assert.Contains("Reason: The workspace file could not be found.", message);
            Assert.Contains(".gabcode-workspace", message);
        });
    }

    [Fact]
    public async Task Empty_window_has_project_actions_and_starts_no_terminals()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            Assert.Null(window.PiTerminal);
            Assert.Null(window.CommandsTerminal);
            Assert.NotNull(window.FindName("OpenWorkspaceButton"));
            Assert.NotNull(window.FindName("CreateWorkspaceButton"));
            return Task.CompletedTask;
        });
    }

    private static TerminalProfileResolver CreateCmdResolver() => new(
        [],
        [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);

    private static async Task RunOnStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); completed.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CloseTerminalsConfirmation : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
