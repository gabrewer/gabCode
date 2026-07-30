using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using Microsoft.Win32.SafeHandles;

namespace GabCode.Windows.Terminal.Conpty;

internal sealed class ConptyTerminalConnection : ITerminalConnection, IAsyncDisposable
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private readonly object sync = new();
    private readonly TerminalProcessOptions options;
    private readonly Dispatcher? ownerDispatcher;
    private readonly Channel<PendingInput> input = Channel.CreateUnbounded<PendingInput>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalSessionState state = TerminalSessionState.Created;
    private NativeSession? session;
    private Task? startTask;
    private Task? closeTask;
    private Task? inputTask;
    private Task? outputTask;
    private Task? waitTask;
    private Exception? failure;
    private int? processId;

    public ConptyTerminalConnection(TerminalProcessOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public event EventHandler<TerminalSessionState>? StateChanged;

    public TerminalSessionState State
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (sync)
            {
                return processId;
            }
        }
    }

    public Exception? Failure
    {
        get
        {
            lock (sync)
            {
                return failure;
            }
        }
    }

    public void Start()
    {
        _ = StartAsync();
    }

    public Task StartAsync()
    {
        TaskCompletionSource? operation = null;
        Task result;
        lock (sync)
        {
            if (closeTask is not null || state is TerminalSessionState.Closing or TerminalSessionState.Closed)
            {
                return Task.FromException(new InvalidOperationException("A closed terminal connection cannot be started."));
            }

            if (startTask is null)
            {
                operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startTask = operation.Task;
                state = TerminalSessionState.Starting;
            }

            result = startTask;
        }

        if (operation is not null)
        {
            _ = CompleteStartOperationAsync(operation);
        }

        return result;
    }

    public void WriteInput(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!input.Writer.TryWrite(new PendingInput(data, null)))
        {
            throw new InvalidOperationException("The terminal input channel is closed.");
        }
    }

    public async ValueTask WriteInputAsync(string data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await input.Writer.WriteAsync(new PendingInput(data, written), cancellationToken).ConfigureAwait(false);
        await written.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(uint rows, uint columns)
    {
        ResizeCore(rows, columns);
    }

    public Task ResizeAsync(uint rows, uint columns)
    {
        ResizeCore(rows, columns);
        return Task.CompletedTask;
    }

    public void Close()
    {
        _ = CloseAsync();
    }

    public Task CloseAsync()
    {
        TaskCompletionSource? operation = null;
        Task result;
        lock (sync)
        {
            if (closeTask is null)
            {
                operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                closeTask = operation.Task;
            }

            result = closeTask;
        }

        if (operation is not null)
        {
            _ = CompleteCloseOperationAsync(operation);
        }

        return result;
    }

    public Task<int> WaitForExitAsync() => completion.Task;

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task CompleteStartOperationAsync(TaskCompletionSource operation)
    {
        try
        {
            await StartCoreAsync().ConfigureAwait(false);
            operation.TrySetResult();
        }
        catch (Exception exception)
        {
            operation.TrySetException(exception);
        }
    }

    private async Task CompleteCloseOperationAsync(TaskCompletionSource operation)
    {
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            operation.TrySetResult();
        }
        catch (Exception exception)
        {
            operation.TrySetException(exception);
        }
    }

    private async Task StartCoreAsync()
    {
        try
        {
            NotifyStateChanged(TerminalSessionState.Starting);
            if (!Directory.Exists(options.WorkingDirectory))
            {
                throw new DirectoryNotFoundException($"Terminal working directory does not exist: {options.WorkingDirectory}");
            }

            var createdSession = await Task.Run(() => NativeSession.Create(options)).ConfigureAwait(false);
            lock (sync)
            {
                session = createdSession;
                processId = checked((int)createdSession.ProcessId);
            }

            inputTask = Task.Run(() => PumpInputAsync(createdSession));
            outputTask = Task.Run(() => PumpOutputAsync(createdSession));
            SetState(TerminalSessionState.Running);
            waitTask = MonitorProcessAsync(createdSession);
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                failure = exception;
            }

            CompleteInputWithoutPump(exception);
            SetState(TerminalSessionState.Failed);
            completion.TrySetException(exception);
            throw;
        }
    }

    private async Task PumpInputAsync(NativeSession nativeSession)
    {
        try
        {
            await foreach (var pending in input.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(pending.Data);
                    await nativeSession.Input.WriteAsync(bytes).ConfigureAwait(false);
                    await nativeSession.Input.FlushAsync().ConfigureAwait(false);
                    pending.Written?.TrySetResult();
                }
                catch (Exception exception)
                {
                    pending.Written?.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            while (input.Reader.TryRead(out var pending))
            {
                pending.Written?.TrySetException(exception);
            }

            HandleUnexpectedTransportFailure(exception);
        }
    }

    private async Task PumpOutputAsync(NativeSession nativeSession)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (true)
            {
                var count = await nativeSession.Output.ReadAsync(bytes).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var characterCount = decoder.GetChars(bytes, 0, count, characters, 0, flush: false);
                if (characterCount != 0)
                {
                    PublishOutput(new string(characters, 0, characterCount));
                }
            }

            var finalCount = decoder.GetChars([], 0, 0, characters, 0, flush: true);
            if (finalCount != 0)
            {
                PublishOutput(new string(characters, 0, finalCount));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            HandleUnexpectedTransportFailure(exception);
        }
    }

    private async Task MonitorProcessAsync(NativeSession nativeSession)
    {
        try
        {
            var exitCode = await Task.Run(() => nativeSession.WaitForExit()).ConfigureAwait(false);
            completion.TrySetResult(exitCode);
            if (State is TerminalSessionState.Running or TerminalSessionState.Starting)
            {
                SetState(TerminalSessionState.Exited);
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                failure ??= exception;
            }

            completion.TrySetException(exception);
            if (State is not TerminalSessionState.Closing and not TerminalSessionState.Closed)
            {
                SetState(TerminalSessionState.Failed);
            }
        }
    }

    private async Task CloseCoreAsync()
    {
        Task? localStart;
        lock (sync)
        {
            localStart = startTask;
            if (state == TerminalSessionState.Created)
            {
                state = TerminalSessionState.Closed;
                completion.TrySetResult(0);
                CompleteInputWithoutPump(new ObjectDisposedException(nameof(ConptyTerminalConnection)));
                return;
            }
        }

        if (localStart is not null)
        {
            try
            {
                await localStart.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CompleteInputWithoutPump(exception);
                SetState(TerminalSessionState.Closed);
                return;
            }
        }

        var preserveFailedState = State == TerminalSessionState.Failed;
        if (!preserveFailedState)
        {
            SetState(TerminalSessionState.Closing);
        }

        input.Writer.TryComplete();

        NativeSession? nativeSession;
        lock (sync)
        {
            nativeSession = session;
        }

        if (nativeSession is null)
        {
            if (!preserveFailedState)
            {
                SetState(TerminalSessionState.Closed);
            }

            return;
        }

        nativeSession.CloseInput();
        var graceful = await Task.WhenAny(completion.Task, Task.Delay(options.GracefulShutdownTimeout)).ConfigureAwait(false);
        if (!ReferenceEquals(graceful, completion.Task))
        {
            nativeSession.TerminateJob();
            try
            {
                await completion.Task.WaitAsync(options.GracefulShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                lock (sync)
                {
                    failure ??= exception;
                }
            }
        }

        nativeSession.Dispose();
        if (inputTask is not null)
        {
            await IgnoreExpectedShutdownFailure(inputTask).ConfigureAwait(false);
        }

        if (outputTask is not null)
        {
            try
            {
                await outputTask.WaitAsync(options.GracefulShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
            {
                lock (sync)
                {
                    failure ??= exception;
                }
            }
        }

        if (!preserveFailedState)
        {
            SetState(TerminalSessionState.Closed);
        }
    }

    private void HandleUnexpectedTransportFailure(Exception exception)
    {
        lock (sync)
        {
            if (state is TerminalSessionState.Closing or TerminalSessionState.Closed or TerminalSessionState.Exited or TerminalSessionState.Failed)
            {
                return;
            }

            failure ??= exception;
            state = TerminalSessionState.Failed;
        }

        NotifyStateChanged(TerminalSessionState.Failed);
        _ = ObserveFailedTransportCleanupAsync();
    }

    private async Task ObserveFailedTransportCleanupAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                failure ??= exception;
            }
        }
    }

    private void CompleteInputWithoutPump(Exception exception)
    {
        input.Writer.TryComplete(exception);
        if (inputTask is not null)
        {
            return;
        }

        while (input.Reader.TryRead(out var pending))
        {
            pending.Written?.TrySetException(exception);
        }
    }

    private static async Task IgnoreExpectedShutdownFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or ChannelClosedException)
        {
        }
    }

    private void ResizeCore(uint rows, uint columns)
    {
        if (rows is 0 or > (uint)short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (columns is 0 or > (uint)short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        NativeSession? nativeSession;
        lock (sync)
        {
            nativeSession = session;
        }

        if (nativeSession is null)
        {
            return;
        }

        nativeSession.Resize(checked((short)columns), checked((short)rows));
    }

    private void PublishOutput(string data)
    {
        DispatchNotification(
            () => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data)),
            DispatcherPriority.Send);
    }

    private void SetState(TerminalSessionState nextState)
    {
        lock (sync)
        {
            state = nextState;
        }

        NotifyStateChanged(nextState);
    }

    private void NotifyStateChanged(TerminalSessionState nextState) =>
        DispatchNotification(() => StateChanged?.Invoke(this, nextState), DispatcherPriority.DataBind);

    private void DispatchNotification(Action notification, DispatcherPriority priority)
    {
        if (ownerDispatcher is null)
        {
            notification();
            return;
        }

        if (ownerDispatcher.HasShutdownStarted || ownerDispatcher.HasShutdownFinished)
        {
            return;
        }

        if (ownerDispatcher.CheckAccess())
        {
            notification();
            return;
        }

        try
        {
            _ = ownerDispatcher.BeginInvoke(notification, priority);
        }
        catch (InvalidOperationException) when (ownerDispatcher.HasShutdownStarted || ownerDispatcher.HasShutdownFinished)
        {
        }
    }

    private sealed record PendingInput(string Data, TaskCompletionSource? Written);

    private sealed class NativeSession : IDisposable
    {
        private readonly object disposeSync = new();
        private IntPtr pseudoConsole;
        private SafeKernelObjectHandle? process;
        private SafeKernelObjectHandle? job;
        private SafeFileHandle? pseudoConsoleInput;
        private SafeFileHandle? pseudoConsoleOutput;
        private bool disposed;

        private NativeSession(
            IntPtr pseudoConsole,
            SafeKernelObjectHandle process,
            SafeKernelObjectHandle job,
            SafeFileHandle input,
            SafeFileHandle output,
            SafeFileHandle pseudoConsoleInput,
            SafeFileHandle pseudoConsoleOutput,
            uint processId)
        {
            this.pseudoConsole = pseudoConsole;
            this.process = process;
            this.job = job;
            this.pseudoConsoleInput = pseudoConsoleInput;
            this.pseudoConsoleOutput = pseudoConsoleOutput;
            Input = new FileStream(input, FileAccess.Write, bufferSize: 4096, isAsync: false);
            Output = new FileStream(output, FileAccess.Read, bufferSize: 4096, isAsync: false);
            ProcessId = processId;
        }

        internal FileStream Input { get; }

        internal FileStream Output { get; }

        internal uint ProcessId { get; }

        internal static NativeSession Create(TerminalProcessOptions options)
        {
            SafeFileHandle? inputRead = null;
            SafeFileHandle? inputWrite = null;
            SafeFileHandle? outputRead = null;
            SafeFileHandle? outputWrite = null;
            SafeKernelObjectHandle? process = null;
            SafeKernelObjectHandle? thread = null;
            SafeKernelObjectHandle? job = null;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;
            try
            {
                if (!ConptyNativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                    !ConptyNativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                {
                    ConptyNativeMethods.ThrowLastWin32Error("Could not create ConPTY pipes.");
                }

                var result = ConptyNativeMethods.CreatePseudoConsole(
                    new ConptyNativeMethods.Coord(80, 24),
                    inputRead!,
                    outputWrite!,
                    0,
                    out pseudoConsole);
                Marshal.ThrowExceptionForHR(result);

                nuint attributeListSize = 0;
                _ = ConptyNativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
                if (!ConptyNativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    ConptyNativeMethods.ThrowLastWin32Error("Could not initialize the process attribute list.");
                }

                if (!ConptyNativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ConptyNativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    ConptyNativeMethods.ThrowLastWin32Error("Could not attach the pseudoconsole process attribute.");
                }

                job = CreateKillOnCloseJob();
                var startupInfo = new ConptyNativeMethods.StartupInfoEx
                {
                    StartupInfo = new ConptyNativeMethods.StartupInfo
                    {
                        Cb = Marshal.SizeOf<ConptyNativeMethods.StartupInfoEx>(),
                        Flags = ConptyNativeMethods.StartupInfoUseStdHandles,
                    },
                    AttributeList = attributeList,
                };
                var commandLine = new StringBuilder($"\"{options.ExecutablePath}\"");
                if (options.Arguments.Length != 0)
                {
                    _ = commandLine.Append(' ').Append(options.Arguments);
                }

                environmentBlock = Marshal.AllocHGlobal(checked(options.EnvironmentBlock.Length * sizeof(char)));
                Marshal.Copy(options.EnvironmentBlock.ToCharArray(), 0, environmentBlock, options.EnvironmentBlock.Length);
                if (!ConptyNativeMethods.CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    ConptyNativeMethods.ExtendedStartupInfoPresent |
                    ConptyNativeMethods.CreateUnicodeEnvironment |
                    ConptyNativeMethods.CreateSuspended,
                    environmentBlock,
                    options.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
                {
                    ConptyNativeMethods.ThrowLastWin32Error($"Could not start terminal process '{options.ExecutablePath}'.");
                }

                process = new SafeKernelObjectHandle(processInformation.Process);
                thread = new SafeKernelObjectHandle(processInformation.Thread);
                if (!ConptyNativeMethods.AssignProcessToJobObject(job, process))
                {
                    ConptyNativeMethods.ThrowLastWin32Error("Could not assign terminal process to its cleanup job.");
                }

                if (ConptyNativeMethods.ResumeThread(thread) == uint.MaxValue)
                {
                    ConptyNativeMethods.ThrowLastWin32Error("Could not resume terminal process.");
                }

                var created = new NativeSession(
                    pseudoConsole,
                    process,
                    job,
                    inputWrite!,
                    outputRead!,
                    inputRead!,
                    outputWrite!,
                    processInformation.ProcessId);
                pseudoConsole = IntPtr.Zero;
                process = null;
                job = null;
                inputWrite = null;
                outputRead = null;
                inputRead = null;
                outputWrite = null;
                return created;
            }
            finally
            {
                thread?.Dispose();
                process?.Dispose();
                job?.Dispose();
                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                if (attributeList != IntPtr.Zero)
                {
                    ConptyNativeMethods.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    ConptyNativeMethods.ClosePseudoConsole(pseudoConsole);
                }
            }
        }

        internal int WaitForExit()
        {
            var localProcess = process ?? throw new ObjectDisposedException(nameof(NativeSession));
            var wait = ConptyNativeMethods.WaitForSingleObject(localProcess, Infinite);
            if (wait != WaitObject0)
            {
                ConptyNativeMethods.ThrowLastWin32Error("Waiting for terminal process failed.");
            }

            if (!ConptyNativeMethods.GetExitCodeProcess(localProcess, out var exitCode))
            {
                ConptyNativeMethods.ThrowLastWin32Error("Reading terminal process exit code failed.");
            }

            return unchecked((int)exitCode);
        }

        internal void Resize(short columns, short rows)
        {
            var localPseudoConsole = pseudoConsole;
            if (localPseudoConsole == IntPtr.Zero)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(ConptyNativeMethods.ResizePseudoConsole(
                localPseudoConsole,
                new ConptyNativeMethods.Coord(columns, rows)));
        }

        internal void CloseInput()
        {
            try
            {
                Input.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal void TerminateJob()
        {
            var localJob = job;
            if (localJob is not null && !localJob.IsInvalid && !ConptyNativeMethods.TerminateJobObject(localJob, 1))
            {
                var error = Marshal.GetLastWin32Error();
                const int AccessDenied = 5;
                if (error != AccessDenied)
                {
                    throw new System.ComponentModel.Win32Exception(error, "Could not terminate terminal process job.");
                }
            }
        }

        public void Dispose()
        {
            lock (disposeSync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                Input.Dispose();
                if (pseudoConsole != IntPtr.Zero)
                {
                    ConptyNativeMethods.ClosePseudoConsole(pseudoConsole);
                    pseudoConsole = IntPtr.Zero;
                }

                Output.Dispose();
                pseudoConsoleInput?.Dispose();
                pseudoConsoleInput = null;
                pseudoConsoleOutput?.Dispose();
                pseudoConsoleOutput = null;
                process?.Dispose();
                process = null;
                job?.Dispose();
                job = null;
            }
        }

        private static SafeKernelObjectHandle CreateKillOnCloseJob()
        {
            var job = ConptyNativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                ConptyNativeMethods.ThrowLastWin32Error("Could not create terminal cleanup job.");
            }

            var information = new ConptyNativeMethods.JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = ConptyNativeMethods.JobObjectLimitKillOnJobClose;
            if (!ConptyNativeMethods.SetInformationJobObject(
                job,
                ConptyNativeMethods.JobObjectExtendedLimitInformationClass,
                ref information,
                checked((uint)Marshal.SizeOf<ConptyNativeMethods.JobObjectExtendedLimitInformation>())))
            {
                job.Dispose();
                ConptyNativeMethods.ThrowLastWin32Error("Could not configure terminal cleanup job.");
            }

            return job;
        }
    }
}
