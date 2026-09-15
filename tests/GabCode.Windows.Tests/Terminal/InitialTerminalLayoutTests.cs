using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class InitialTerminalLayoutTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Resize_requested_before_start_sets_the_childs_initial_console_geometry()
    {
        var output = new StringBuilder();
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: "pwsh.exe",
            arguments: "-NoLogo -NoProfile -Command \"[Console]::WriteLine(('WTIL_INITIAL_SIZE={0}x{1}' -f [Console]::WindowWidth,[Console]::WindowHeight))\"",
            workingDirectory: CreateTemporaryDirectory(),
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.TerminalOutput += (_, chunk) =>
        {
            lock (output)
            {
                _ = output.Append(chunk.Data);
                if (output.ToString().Contains("WTIL_INITIAL_SIZE=", StringComparison.Ordinal))
                {
                    observed.TrySetResult(output.ToString());
                }
            }
        };

        await connection.ResizeAsync(rows: 40, columns: 100);
        await connection.StartAsync();

        Assert.Contains("WTIL_INITIAL_SIZE=100x40", await observed.Task.WaitAsync(Timeout), StringComparison.Ordinal);
        await connection.CloseAsync();
    }

    [Fact]
    public async Task Hosted_session_replays_its_measured_geometry_before_the_shell_starts()
    {
        await RunOnStaAsync(async () =>
        {
            var session = new TerminalHostedSession(
                TerminalSessionKind.First,
                CreateTemporaryDirectory(),
                new TerminalProfileResolution("PowerShell", "pwsh.exe", "-NoLogo -NoProfile", new Dictionary<string, string?>(), false, "Test shell: PowerShell."));
            var window = new Window { Content = session.Control, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await session.StartAsync().WaitAsync(Timeout);
                var expected = $"WTIL_HOST_SIZE={session.Control.Columns}x{session.Control.Rows}";
                Assert.True(session.Control.Columns > 80 || session.Control.Rows > 24, $"The hosted control did not receive a usable measured size: {session.Control.Columns}x{session.Control.Rows}.");

                var output = new StringBuilder();
                var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs>? handler = null;
                handler = (_, chunk) =>
                {
                    _ = output.Append(chunk.Data);
                    if (!Regex.IsMatch(output.ToString(), @"WTIL_HOST_SIZE=\d+x\d+")) return;
                    session.Connection.TerminalOutput -= handler;
                    received.TrySetResult(output.ToString());
                };
                session.Connection.TerminalOutput += handler;
                await session.WriteInputAsync("[Console]::WriteLine(('WTIL_HOST_SIZE={0}x{1}' -f [Console]::WindowWidth,[Console]::WindowHeight))\r");

                Assert.Contains(expected, await received.Task.WaitAsync(Timeout), StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await session.DisposeAsync();
            }
        });
    }

    [Fact]
    public void Generic_fixture_exercises_alternate_screen_cursor_addressing_and_erase_without_naming_pi()
    {
        var fixturePath = Path.Combine(GetRepositoryRoot(), "tests", "fixtures", "windows-terminal-initial-vt-layout", "initial-vt-layout.ps1");
        var fixture = File.ReadAllText(fixturePath);

        Assert.Contains("$esc[?1049h", fixture, StringComparison.Ordinal);
        Assert.Contains("$esc[2J", fixture, StringComparison.Ordinal);
        Assert.Contains("$esc[2;4H", fixture, StringComparison.Ordinal);
        Assert.Contains("WTIL_INITIAL", fixture, StringComparison.Ordinal);
        Assert.Contains("WTIL_UPDATE", fixture, StringComparison.Ordinal);
        Assert.Contains("WTIL_INPUT", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("pi", fixture, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "The WPF test thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gabCode terminal Ω", $"initial layout {Guid.NewGuid():N} 漢字");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GabCode.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate the repository root from the test output directory.");
    }
}
