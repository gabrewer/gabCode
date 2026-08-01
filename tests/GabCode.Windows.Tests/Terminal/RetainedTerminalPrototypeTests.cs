using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class RetainedTerminalPrototypeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Prototype_runs_two_independent_shells_and_preserves_their_views_and_processes_across_region_swaps()
    {
        await RunOnStaAsync(async () =>
        {
            var directory = CreateTemporaryDirectory();
            var confirmation = new TestExitConfirmationService(TerminalExitDecision.CloseAndStopTerminals);
            var window = new MainWindow(directory, CreateCmdResolver(), confirmation);
            window.Show();

            var pi = Assert.IsType<TerminalSessionView>(window.PiTerminal);
            var commands = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
            await WaitForStateAsync(pi, TerminalSessionState.Running);
            await WaitForStateAsync(commands, TerminalSessionState.Running);
            var piControl = Assert.IsAssignableFrom<FrameworkElement>(pi.TerminalControlInstance);
            var commandsControl = Assert.IsAssignableFrom<FrameworkElement>(commands.TerminalControlInstance);
            var piState = Assert.IsType<TextBlock>(pi.FindName("SessionStateText"));
            var commandsState = Assert.IsType<TextBlock>(commands.FindName("SessionStateText"));
            var accessibleNames = new[]
            {
                AutomationProperties.GetName(pi),
                AutomationProperties.GetName(commands),
                AutomationProperties.GetName(piControl),
                AutomationProperties.GetName(commandsControl),
                AutomationProperties.GetName(piState),
                AutomationProperties.GetName(commandsState),
            };
            Assert.All(accessibleNames, name =>
            {
                Assert.DoesNotContain("Pi", name, StringComparison.Ordinal);
                Assert.DoesNotContain("Commands", name, StringComparison.Ordinal);
            });
            Assert.NotEqual(AutomationProperties.GetName(pi), AutomationProperties.GetName(commands));
            Assert.NotEqual(AutomationProperties.GetName(piControl), AutomationProperties.GetName(commandsControl));
            Assert.NotEqual(AutomationProperties.GetName(piState), AutomationProperties.GetName(commandsState));
            var piPid = Assert.IsType<int>(pi.ProcessId);
            var commandsPid = Assert.IsType<int>(commands.ProcessId);
            Assert.NotEqual(piPid, commandsPid);

            var piMarker = $"WTR003_PI_{Guid.NewGuid():N}";
            var commandsMarker = $"WTR003_COMMANDS_{Guid.NewGuid():N}";
            var piOutput = WaitForOutputAsync(pi, piMarker);
            var commandsOutput = WaitForOutputAsync(commands, commandsMarker);
            await pi.Session!.WriteInputAsync($"echo {piMarker} %CD% Ω 漢字\r");
            await commands.Session!.WriteInputAsync($"echo {commandsMarker} %CD% Ω 漢字\r");
            Assert.Contains(piMarker, await piOutput.WaitAsync(Timeout), StringComparison.Ordinal);
            Assert.Contains(commandsMarker, await commandsOutput.WaitAsync(Timeout), StringComparison.Ordinal);

            window.ShowCommandsInMain();
            Assert.False(window.IsPiInMain);
            Assert.Same(commands, Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion")).Content);
            Assert.Same(pi, Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion")).Content);
            Assert.Same(piControl, pi.TerminalControlInstance);
            Assert.Same(commandsControl, commands.TerminalControlInstance);
            Assert.Equal(piPid, pi.ProcessId);
            Assert.Equal(commandsPid, commands.ProcessId);
            Assert.False(HasExited(piPid));
            Assert.False(HasExited(commandsPid));

            window.ShowPiInMain();
            Assert.True(window.IsPiInMain);
            Assert.Same(pi, Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion")).Content);
            Assert.Same(commands, Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion")).Content);
            Assert.Same(piControl, pi.TerminalControlInstance);
            Assert.Same(commandsControl, commands.TerminalControlInstance);
            Assert.Equal(piPid, pi.ProcessId);
            Assert.Equal(commandsPid, commands.ProcessId);

            window.Close();
            await WaitForWindowClosedAsync(window);
            await WaitForProcessExitAsync(piPid);
            await WaitForProcessExitAsync(commandsPid);
            Assert.Equal(1, confirmation.CallCount);
        });
    }

    [Fact]
    public async Task Terminal_and_fallback_status_expose_their_current_values_to_UI_Automation()
    {
        await RunOnStaAsync(async () =>
        {
            var window = new MainWindow(
                CreateTemporaryDirectory(),
                CreateCmdResolver(),
                new TestExitConfirmationService(TerminalExitDecision.CloseAndStopTerminals));
            window.Show();
            var pi = Assert.IsType<TerminalSessionView>(window.PiTerminal);
            var commands = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
            await WaitForStateAsync(pi, TerminalSessionState.Running);
            await WaitForStateAsync(commands, TerminalSessionState.Running);

            try
            {
                var profileStatus = Assert.IsType<TextBlock>(pi.FindName("ProfileStatusText"));
                var sessionStatus = Assert.IsType<TextBlock>(pi.FindName("SessionStateText"));
                Assert.Contains("fallback", AutomationProperties.GetName(profileStatus), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Ready", AutomationProperties.GetName(sessionStatus), StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await WaitForWindowClosedAsync(window);
            }
        });
    }

    [Fact]
    public async Task Natural_exit_remains_visible_without_replacing_or_relaunching_the_session()
    {
        await RunOnStaAsync(async () =>
        {
            var view = new TerminalSessionView(TerminalSessionKind.First, CreateTemporaryDirectory(), CreateCmdResolution);
            var window = new Window { Content = view };
            window.Show();
            await WaitForStateAsync(view, TerminalSessionState.Running);
            var originalSession = view.Session;
            var originalControl = view.TerminalControlInstance;
            var processId = Assert.IsType<int>(view.ProcessId);

            await view.Session!.WriteInputAsync("exit 41\r");
            Assert.Equal(41, await view.Session.WaitForExitAsync().WaitAsync(Timeout));
            await WaitForStateAsync(view, TerminalSessionState.Exited);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Same(originalSession, view.Session);
            Assert.Same(originalControl, view.TerminalControlInstance);
            Assert.Equal(processId, view.ProcessId);
            Assert.Equal(TerminalSessionState.Exited, view.State);
            Assert.True(HasExited(processId));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(view.FindName("FailureSurface")).Visibility);

            await view.CloseAsync();
            window.Close();
        });
    }

    [Fact]
    public async Task Focused_terminal_failure_moves_keyboard_focus_to_its_retry_action()
    {
        await RunOnStaAsync(async () =>
        {
            var view = new TerminalSessionView(TerminalSessionKind.First, CreateTemporaryDirectory(), CreateCmdResolution);
            var window = new Window { Content = view };
            window.Show();
            await WaitForStateAsync(view, TerminalSessionState.Running);
            try
            {
                view.FocusTerminal();
                var nativeSession = typeof(ConptyTerminalConnection)
                    .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(view.Session!.Connection) ?? throw new InvalidOperationException("The native session was not created.");
                var input = Assert.IsType<FileStream>(nativeSession.GetType()
                    .GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(nativeSession));
                input.Dispose();

                await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await view.Session.WriteInputAsync("echo WTR003_BROKEN_INPUT\r"));
                await WaitForStateAsync(view, TerminalSessionState.Failed);
                await Dispatcher.Yield(DispatcherPriority.Input);

                var retry = Assert.IsType<Button>(view.FindName("RetryButton"));
                var terminalSurface = Assert.IsType<ContentControl>(view.FindName("TerminalSurfaceHost"));
                Assert.True(retry.IsKeyboardFocused || retry.IsKeyboardFocusWithin, "The retry action did not receive focus after the terminal failed.");
                Assert.Equal(Visibility.Collapsed, terminalSurface.Visibility);
            }
            finally
            {
                await view.CloseAsync();
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Failed_session_retries_without_replacing_the_healthy_session()
    {
        await RunOnStaAsync(async () =>
        {
            var directory = CreateTemporaryDirectory();
            var attempts = 0;
            var pi = new TerminalSessionView(
                TerminalSessionKind.First,
                directory,
                () => Interlocked.Increment(ref attempts) == 1
                    ? new TerminalProfileResolution("Broken test shell", "missing-gabcode-shell.exe", string.Empty, new Dictionary<string, string?>(), false, "Broken test shell")
                    : CreateCmdResolution());
            var commands = new TerminalSessionView(TerminalSessionKind.Second, directory, CreateCmdResolution);
            var window = new Window
            {
                Content = new Grid
                {
                    Children = { pi, commands },
                },
            };
            window.Show();

            await WaitForStateAsync(pi, TerminalSessionState.Failed);
            await WaitForStateAsync(commands, TerminalSessionState.Running);
            var commandsPid = Assert.IsType<int>(commands.ProcessId);
            var commandsControl = commands.TerminalControlInstance;

            await pi.RetryAsync();
            await WaitForStateAsync(pi, TerminalSessionState.Running);

            Assert.Equal(2, attempts);
            Assert.Equal(commandsPid, commands.ProcessId);
            Assert.Same(commandsControl, commands.TerminalControlInstance);
            Assert.False(HasExited(commandsPid));

            await Task.WhenAll(pi.CloseAsync(), commands.CloseAsync()).WaitAsync(Timeout);
            window.Close();
            await WaitForProcessExitAsync(commandsPid);
        });
    }

    [Fact]
    public async Task Exit_cancel_keeps_both_active_sessions_alive_until_a_confirmed_close()
    {
        await RunOnStaAsync(async () =>
        {
            var confirmation = new TestExitConfirmationService(TerminalExitDecision.Cancel);
            var window = new MainWindow(CreateTemporaryDirectory(), CreateCmdResolver(), confirmation);
            window.Show();
            var pi = Assert.IsType<TerminalSessionView>(window.PiTerminal);
            var commands = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
            await WaitForStateAsync(pi, TerminalSessionState.Running);
            await WaitForStateAsync(commands, TerminalSessionState.Running);
            var piPid = Assert.IsType<int>(pi.ProcessId);
            var commandsPid = Assert.IsType<int>(commands.ProcessId);

            window.ShowCommandsInMain();
            commands.FocusTerminal();
            window.Close();
            await Task.Yield();

            Assert.True(window.IsVisible);
            Assert.Equal(1, confirmation.CallCount);
            Assert.False(HasExited(piPid));
            Assert.False(HasExited(commandsPid));
            Assert.Equal(TerminalSessionState.Running, pi.State);
            Assert.Equal(TerminalSessionState.Running, commands.State);

            confirmation.Decision = TerminalExitDecision.CloseAndStopTerminals;
            window.Close();
            await WaitForWindowClosedAsync(window);
            await WaitForProcessExitAsync(piPid);
            await WaitForProcessExitAsync(commandsPid);
            Assert.Equal(2, confirmation.CallCount);
        });
    }

    private static async Task RunOnStaAsync(Func<Task> operation)
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await operation();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    completion.TrySetResult();
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(Timeout);
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "The WPF prototype test thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static TerminalProfileResolver CreateCmdResolver() => new(
        [],
        [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);

    private static TerminalProfileResolution CreateCmdResolution() => new(
        "cmd",
        Environment.GetEnvironmentVariable("ComSpec")!,
        "/d /q",
        new Dictionary<string, string?>(),
        usedFallback: false,
        "Test shell: cmd.");

    private static async Task WaitForStateAsync(TerminalSessionView session, TerminalSessionState expected)
    {
        if (session.State == expected)
        {
            return;
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (session.State == expected)
            {
                session.SessionChanged -= handler;
                reached.TrySetResult();
            }
        };
        session.SessionChanged += handler;
        await reached.Task.WaitAsync(Timeout);
    }

    private static Task<string> WaitForOutputAsync(TerminalSessionView session, string marker)
    {
        var output = new StringBuilder();
        var matched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs>? handler = null;
        handler = (_, data) =>
        {
            _ = output.Append(data.Data);
            if (output.ToString().Contains(marker, StringComparison.Ordinal))
            {
                session.Session!.Connection.TerminalOutput -= handler;
                matched.TrySetResult(output.ToString());
            }
        };
        session.Session!.Connection.TerminalOutput += handler;
        return matched.Task;
    }

    private static async Task WaitForWindowClosedAsync(Window window)
    {
        if (!window.IsVisible)
        {
            return;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        await closed.Task.WaitAsync(Timeout);
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(Timeout);
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gabCode terminal Ω", $"prototype {Guid.NewGuid():N} 漢字");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestExitConfirmationService(TerminalExitDecision decision) : ITerminalExitConfirmationService
    {
        internal int CallCount { get; private set; }

        internal TerminalExitDecision Decision { get; set; } = decision;

        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount)
        {
            Assert.Same(owner, Application.Current?.MainWindow ?? owner);
            Assert.InRange(activeTerminalCount, 1, 2);
            CallCount++;
            return Decision;
        }
    }
}
