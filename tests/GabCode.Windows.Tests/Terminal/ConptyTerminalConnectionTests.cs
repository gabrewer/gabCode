using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Conpty;

namespace GabCode.Windows.Tests.Terminal;

public sealed class ConptyTerminalConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Connection_starts_in_a_space_and_unicode_directory_round_trips_utf8_input_and_closes_idempotently()
    {
        var workingDirectory = CreateTemporaryDirectory();
        await using var connection = CreateCommandConnection(workingDirectory);
        var cwdSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.TerminalOutput += (_, output) =>
        {
            if (output.Data.Contains("WTR002_CWD=", StringComparison.Ordinal))
            {
                cwdSeen.TrySetResult(output.Data);
            }
        };

        await connection.StartAsync();
        var processId = Assert.IsType<int>(connection.ProcessId);
        Assert.Equal(TerminalSessionState.Running, connection.State);

        await connection.ResizeAsync(rows: 40, columns: 100);
        await connection.WriteInputAsync("echo WTR002_CWD=%CD% Ω 漢字\r");
        var output = await cwdSeen.Task.WaitAsync(Timeout);
        Assert.Contains(workingDirectory, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ω 漢字", output);

        await connection.CloseAsync();
        await connection.CloseAsync();
        Assert.Equal(TerminalSessionState.Closed, connection.State);
        Assert.False(IsProcessAlive(processId));
    }

    [Fact]
    public async Task Connection_completes_pending_input_writes_when_shutdown_races_the_input_queue()
    {
        await using var connection = CreateCommandConnection(CreateTemporaryDirectory());
        await connection.StartAsync();

        var writes = Enumerable.Range(0, 128)
            .Select(index => connection.WriteInputAsync($"echo WTR002_QUEUE_{index}\r").AsTask())
            .ToArray();
        var close = connection.CloseAsync();
        var allWrites = Task.WhenAll(writes);

        var completed = await Task.WhenAny(allWrites, Task.Delay(Timeout));
        Assert.Same(allWrites, completed);
        try
        {
            await allWrites;
        }
        catch (Exception)
        {
            // Writes that lose the race with shutdown may fail, but none may remain pending.
        }

        await close.WaitAsync(Timeout);
        Assert.Equal(TerminalSessionState.Closed, connection.State);
    }

    [Fact]
    public async Task Connection_releases_resources_after_startup_failure_and_close_is_concurrent_safe()
    {
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: "gabcode-missing-shell.exe",
            arguments: string.Empty,
            workingDirectory: CreateTemporaryDirectory(),
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));

        await Assert.ThrowsAnyAsync<Exception>(connection.StartAsync);
        Assert.Equal(TerminalSessionState.Failed, connection.State);

        var firstClose = connection.CloseAsync();
        var secondClose = connection.CloseAsync();
        Assert.Same(firstClose, secondClose);
        await Task.WhenAll(firstClose, secondClose).WaitAsync(Timeout);
        Assert.Equal(TerminalSessionState.Closed, connection.State);
    }

    [Fact]
    public async Task Connection_returns_one_start_operation_when_start_notification_reenters_start()
    {
        var missingDirectory = Path.Combine(CreateTemporaryDirectory(), "missing");
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
            arguments: "/d /q",
            workingDirectory: missingDirectory,
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));
        Task? reentrantStart = null;
        var reentered = 0;
        connection.StateChanged += (_, state) =>
        {
            if (state == TerminalSessionState.Starting && Interlocked.Exchange(ref reentered, 1) == 0)
            {
                reentrantStart = connection.StartAsync();
            }
        };

        var initialStart = connection.StartAsync();

        Assert.NotNull(reentrantStart);
        Assert.Same(initialStart, reentrantStart);
        await Assert.ThrowsAnyAsync<Exception>(async () => await initialStart);
        Assert.Equal(TerminalSessionState.Failed, connection.State);
    }

    [Fact]
    public async Task Connection_closes_a_start_reserved_before_its_start_notification_returns()
    {
        await using var connection = CreateCommandConnection(CreateTemporaryDirectory());
        Task? closeDuringStart = null;
        connection.StateChanged += (_, state) =>
        {
            if (state == TerminalSessionState.Starting)
            {
                closeDuringStart = connection.CloseAsync();
            }
        };

        await connection.StartAsync().WaitAsync(Timeout);
        var processId = Assert.IsType<int>(connection.ProcessId);
        Assert.NotNull(closeDuringStart);
        await closeDuringStart.WaitAsync(Timeout);

        Assert.Equal(TerminalSessionState.Closed, connection.State);
        Assert.False(IsProcessAlive(processId));
    }

    [Fact]
    public async Task Connection_returns_one_close_operation_when_closing_notification_reenters_close()
    {
        await using var connection = CreateCommandConnection(CreateTemporaryDirectory());
        Task? reentrantClose = null;
        var reentered = 0;
        connection.StateChanged += (_, state) =>
        {
            if (state == TerminalSessionState.Closing && Interlocked.Exchange(ref reentered, 1) == 0)
            {
                reentrantClose = connection.CloseAsync();
            }
        };

        await connection.StartAsync();
        var initialClose = connection.CloseAsync();

        Assert.NotNull(reentrantClose);
        Assert.Same(initialClose, reentrantClose);
        await initialClose.WaitAsync(Timeout);
        Assert.Equal(TerminalSessionState.Closed, connection.State);
    }

    [Fact]
    public async Task Connection_completes_queued_input_when_startup_fails()
    {
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: "gabcode-missing-shell.exe",
            arguments: string.Empty,
            workingDirectory: CreateTemporaryDirectory(),
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));
        var pendingWrite = connection.WriteInputAsync("echo must-not-remain-pending\r").AsTask();

        await Assert.ThrowsAnyAsync<Exception>(connection.StartAsync);
        await connection.CloseAsync().WaitAsync(Timeout);

        var completed = await Task.WhenAny(pendingWrite, Task.Delay(Timeout));
        Assert.Same(pendingWrite, completed);
        await Assert.ThrowsAnyAsync<Exception>(async () => await pendingWrite);
    }

    [Fact]
    public async Task Connection_does_not_search_the_worktree_for_a_missing_shell_executable()
    {
        var workingDirectory = CreateTemporaryDirectory();
        const string missingShell = "gabcode-missing-shell.exe";
        File.Copy(
            Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
            Path.Combine(workingDirectory, missingShell));
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: missingShell,
            arguments: "/d /q /c exit 23",
            workingDirectory: workingDirectory,
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));

        await Assert.ThrowsAnyAsync<Exception>(connection.StartAsync);
        Assert.Null(connection.ProcessId);
        Assert.Equal(TerminalSessionState.Failed, connection.State);
    }

    [Fact]
    public async Task Connection_uses_the_environment_snapshot_selected_with_its_process_options()
    {
        var variableName = $"GABCODE_WTR002_{Guid.NewGuid():N}";
        const string selectedValue = "selected Ω 漢字";
        const string laterAmbientValue = "later ambient value";
        Environment.SetEnvironmentVariable(variableName, selectedValue);
        try
        {
            var options = new TerminalProcessOptions(
                executablePath: Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
                arguments: $"/d /q /c echo WTR002_ENVIRONMENT=%{variableName}%",
                workingDirectory: CreateTemporaryDirectory(),
                gracefulShutdownTimeout: TimeSpan.FromSeconds(2));
            Environment.SetEnvironmentVariable(variableName, laterAmbientValue);
            await using var connection = new ConptyTerminalConnection(options);
            var capturedOutput = new StringBuilder();
            connection.TerminalOutput += (_, output) =>
            {
                lock (capturedOutput)
                {
                    _ = capturedOutput.Append(output.Data);
                }
            };

            await connection.StartAsync();
            await connection.WaitForExitAsync().WaitAsync(Timeout);
            await connection.CloseAsync().WaitAsync(Timeout);
            string output;
            lock (capturedOutput)
            {
                output = capturedOutput.ToString();
            }

            Assert.Contains($"WTR002_ENVIRONMENT={selectedValue}", output, StringComparison.Ordinal);
            Assert.DoesNotContain(laterAmbientValue, output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task Connection_applies_case_insensitive_environment_overrides_and_removals()
    {
        var overrideName = $"GABCODE_WTR002_OVERRIDE_{Guid.NewGuid():N}";
        var removalName = $"GABCODE_WTR002_REMOVE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(overrideName, "ambient");
        Environment.SetEnvironmentVariable(removalName, "ambient");
        try
        {
            var options = new TerminalProcessOptions(
                executablePath: Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
                arguments: $"/d /q /c echo WTR002_OVERRIDE=%{overrideName}%&if defined {removalName} (echo WTR002_REMOVAL=defined) else (echo WTR002_REMOVAL=missing)",
                workingDirectory: CreateTemporaryDirectory(),
                gracefulShutdownTimeout: TimeSpan.FromSeconds(2),
                environmentOverrides: new Dictionary<string, string?>
                {
                    [overrideName.ToLowerInvariant()] = "override Ω 漢字",
                    [removalName] = null,
                });
            await using var connection = new ConptyTerminalConnection(options);
            var capturedOutput = new StringBuilder();
            connection.TerminalOutput += (_, output) =>
            {
                lock (capturedOutput)
                {
                    _ = capturedOutput.Append(output.Data);
                }
            };

            await connection.StartAsync();
            await connection.WaitForExitAsync().WaitAsync(Timeout);
            await connection.CloseAsync().WaitAsync(Timeout);
            string output;
            lock (capturedOutput)
            {
                output = capturedOutput.ToString();
            }

            Assert.Contains("WTR002_OVERRIDE=override Ω 漢字", output, StringComparison.Ordinal);
            Assert.Contains("WTR002_REMOVAL=missing", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(overrideName, null);
            Environment.SetEnvironmentVariable(removalName, null);
        }
    }

    [Fact]
    public async Task Connection_does_not_publish_terminal_output_off_thread_after_its_owner_dispatcher_stops()
    {
        var ready = new TaskCompletionSource<(ConptyTerminalConnection Connection, Dispatcher Dispatcher, int ThreadId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.TrySetResult((CreateCommandConnection(CreateTemporaryDirectory()), dispatcher, Environment.CurrentManagedThreadId));
            Dispatcher.Run();
            ownerExited.TrySetResult();
        });
        ownerThread.SetApartmentState(ApartmentState.STA);
        ownerThread.Start();

        var (connection, dispatcher, ownerThreadId) = await ready.Task.WaitAsync(Timeout);
        await using (connection)
        {
            await connection.StartAsync();
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            await ownerExited.Task.WaitAsync(Timeout);

            var offThreadOutput = 0;
            connection.TerminalOutput += (_, _) =>
            {
                if (Environment.CurrentManagedThreadId != ownerThreadId)
                {
                    Interlocked.Exchange(ref offThreadOutput, 1);
                }
            };

            await connection.WriteInputAsync("echo WTR002_DISPATCHER_STOPPED&exit\r");
            await connection.WaitForExitAsync().WaitAsync(Timeout);
            await connection.CloseAsync().WaitAsync(Timeout);

            Assert.Equal(0, Volatile.Read(ref offThreadOutput));
        }

        Assert.True(ownerThread.Join(Timeout), "The terminal owner dispatcher thread did not stop.");
    }

    [Fact]
    public async Task Connection_marks_failed_and_releases_the_process_when_input_transport_breaks()
    {
        await using var connection = CreateCommandConnection(CreateTemporaryDirectory());
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += (_, state) =>
        {
            if (state == TerminalSessionState.Failed)
            {
                failed.TrySetResult();
            }
        };

        await connection.StartAsync();
        var processId = Assert.IsType<int>(connection.ProcessId);
        var session = typeof(ConptyTerminalConnection)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(connection) ?? throw new InvalidOperationException("The native session was not created.");
        var input = Assert.IsType<FileStream>(session.GetType()
            .GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(session));
        input.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await connection.WriteInputAsync("echo WTR002_BROKEN_INPUT\r"));
        await failed.Task.WaitAsync(Timeout);
        await WaitForProcessExitAsync(processId);

        Assert.Equal(TerminalSessionState.Failed, connection.State);
        Assert.NotNull(connection.Failure);
        Assert.False(IsProcessAlive(processId));
    }

    [Fact]
    public async Task Connection_marks_failed_and_releases_the_process_when_output_transport_breaks()
    {
        await using var connection = CreateCommandConnection(CreateTemporaryDirectory());
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += (_, state) =>
        {
            if (state == TerminalSessionState.Failed)
            {
                failed.TrySetResult();
            }
        };

        await connection.StartAsync();
        var processId = Assert.IsType<int>(connection.ProcessId);
        var session = typeof(ConptyTerminalConnection)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(connection) ?? throw new InvalidOperationException("The native session was not created.");
        var output = Assert.IsType<FileStream>(session.GetType()
            .GetProperty("Output", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(session));
        output.Dispose();

        await failed.Task.WaitAsync(Timeout);
        await WaitForProcessExitAsync(processId);

        Assert.Equal(TerminalSessionState.Failed, connection.State);
        Assert.NotNull(connection.Failure);
        Assert.False(IsProcessAlive(processId));
    }

    [Fact]
    public async Task Connection_reports_natural_exit_and_preserves_the_exit_code()
    {
        var workingDirectory = CreateTemporaryDirectory();
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
            arguments: "/d /q /c exit 37",
            workingDirectory: workingDirectory,
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));

        await connection.StartAsync();
        var exitCode = await connection.WaitForExitAsync().WaitAsync(Timeout);

        Assert.Equal(37, exitCode);
        Assert.Equal(TerminalSessionState.Exited, connection.State);
        await connection.CloseAsync();
        Assert.Equal(TerminalSessionState.Closed, connection.State);
    }

    [Fact]
    public async Task Connection_close_terminates_a_job_owned_descendant()
    {
        var workingDirectory = CreateTemporaryDirectory();
        await using var connection = new ConptyTerminalConnection(new TerminalProcessOptions(
            executablePath: "pwsh.exe",
            arguments: "-NoLogo -NoProfile",
            workingDirectory: workingDirectory,
            gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));
        var childIdSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.TerminalOutput += (_, output) =>
        {
            var match = Regex.Match(output.Data, "WTR002_CHILD=(?<pid>[0-9]+)");
            if (match.Success && int.TryParse(match.Groups["pid"].Value, out var childId))
            {
                childIdSeen.TrySetResult(childId);
            }
        };

        await connection.StartAsync();
        await connection.WriteInputAsync("$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d /q /c timeout /t 30 > nul' -PassThru; Write-Output ('WTR002_CHILD=' + $child.Id)\r");
        var childId = await childIdSeen.Task.WaitAsync(Timeout);
        using var child = Process.GetProcessById(childId);

        await connection.CloseAsync();
        await child.WaitForExitAsync().WaitAsync(Timeout);
        Assert.True(child.HasExited, $"Job-owned descendant {childId} survived connection cleanup.");
    }

    private static ConptyTerminalConnection CreateCommandConnection(string workingDirectory) => new(new TerminalProcessOptions(
        executablePath: Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is required."),
        arguments: "/d /q",
        workingDirectory: workingDirectory,
        gracefulShutdownTimeout: TimeSpan.FromSeconds(2)));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gabCode terminal Ω", $"session {Guid.NewGuid():N} 漢字");
        Directory.CreateDirectory(path);
        return path;
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

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
