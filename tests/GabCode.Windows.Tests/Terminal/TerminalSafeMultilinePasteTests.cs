using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class TerminalSafeMultilinePasteTests
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Controller_forwards_single_line_text_immediately_without_requesting_confirmation()
    {
        var confirmation = new TestPasteConfirmationService(approve: false);
        var writes = new List<string>();
        var controller = new TerminalSafePasteController(confirmation);

        controller.Paste(null, "WMP001_SINGLE_LINE", writes.Add);

        Assert.Equal(0, confirmation.CallCount);
        Assert.Equal(["WMP001_SINGLE_LINE"], writes);
    }

    [Fact]
    public void Controller_cancels_multiline_text_without_writing_and_preserves_the_exact_snapshot_for_approval()
    {
        const string clipboardSnapshot = "WMP001_FIRST\r\nWMP001_SECOND\u001B";
        var confirmation = new TestPasteConfirmationService(approve: false);
        var writes = new List<string>();
        var controller = new TerminalSafePasteController(confirmation);

        controller.Paste(null, clipboardSnapshot, writes.Add);

        Assert.Equal(1, confirmation.CallCount);
        Assert.Empty(writes);

        confirmation.Approve = true;
        controller.Paste(null, clipboardSnapshot, writes.Add);

        Assert.Equal(2, confirmation.CallCount);
        Assert.Equal(["\u001B[200~" + clipboardSnapshot + "\u001B[201~"], writes);
    }

    [Fact]
    public void Controller_rejects_multiline_text_with_embedded_bracketed_paste_markers()
    {
        var confirmation = new TestPasteConfirmationService(approve: true);
        var writes = new List<string>();
        var controller = new TerminalSafePasteController(confirmation);

        controller.Paste(null, "WMP001_FIRST\n\u001B[201~WMP001_SECOND", writes.Add);

        Assert.Equal(0, confirmation.CallCount);
        Assert.Equal(1, confirmation.UnsafeContentCallCount);
        Assert.Empty(writes);
    }

    [Fact]
    public void Preview_bounds_lines_and_characters_while_visibly_representing_control_characters()
    {
        var preview = TerminalPastePreview.Create("one\n\u001Btwo\nthree\nfour\nfive\nsix" + new string('x', 600));

        Assert.True(preview.IsTruncated);
        Assert.Contains("␛", preview.Text, StringComparison.Ordinal);
        Assert.Contains("Preview truncated", preview.AccessibleDescription, StringComparison.Ordinal);
        Assert.True(preview.Text.Length <= 500, $"Preview length was {preview.Text.Length}.");
        Assert.True(preview.Text.Split(Environment.NewLine).Length <= 5, preview.Text);
    }

    [Fact]
    public async Task Native_right_click_paste_currently_writes_multiline_clipboard_text_without_a_confirmation()
    {
        await RunOnStaAsync(async () =>
        {
            const string multilineClipboardText = "WMP001_FIRST\r\nWMP001_SECOND";
            var connection = new RecordingTerminalConnection();
            var terminal = new TerminalControl { AutoResize = true };
            var window = new Window
            {
                Width = 640,
                Height = 400,
                Content = terminal,
            };

            try
            {
                await SetClipboardTextWithRetryAsync(multilineClipboardText);
                terminal.Connection = connection;
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                var terminalWindow = FindDescendantWindow(new WindowInteropHelper(window).Handle, "HwndTerminalClass");
                Assert.NotEqual(IntPtr.Zero, terminalWindow);

                _ = SendMessage(terminalWindow, WmRButtonDown, IntPtr.Zero, IntPtr.Zero);
                _ = SendMessage(terminalWindow, WmRButtonUp, IntPtr.Zero, IntPtr.Zero);

                var forwarded = await connection.Input.Task.WaitAsync(Timeout);
                Assert.Equal(multilineClipboardText, forwarded);
            }
            finally
            {
                terminal.Connection = null!;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Native_interceptor_cancels_then_forwards_the_same_multiline_snapshot_only_after_approval()
    {
        await RunOnStaAsync(async () =>
        {
            const string multilineClipboardText = "WMP001_GUARDED_FIRST\r\nWMP001_GUARDED_SECOND";
            var connection = new RecordingTerminalConnection();
            var confirmation = new TestPasteConfirmationService(approve: false);
            var controller = new TerminalSafePasteController(confirmation);
            var terminal = new TerminalControl { AutoResize = true };
            var window = new Window
            {
                Width = 640,
                Height = 400,
                Content = terminal,
            };

            try
            {
                await SetClipboardTextWithRetryAsync(multilineClipboardText);
                terminal.Connection = connection;
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                using var interceptor = new TerminalNativePasteInterceptor(
                    terminal,
                    snapshot => controller.Paste(window, snapshot, connection.WriteInput),
                    () => throw new Xunit.Sdk.XunitException("The controlled clipboard was unexpectedly unavailable."));
                interceptor.Attach();

                var terminalWindow = FindDescendantWindow(new WindowInteropHelper(window).Handle, "HwndTerminalClass");
                Assert.NotEqual(IntPtr.Zero, terminalWindow);
                _ = SendMessage(terminalWindow, WmRButtonDown, IntPtr.Zero, IntPtr.Zero);
                _ = SendMessage(terminalWindow, WmRButtonUp, IntPtr.Zero, IntPtr.Zero);
                var cancelled = await Task.WhenAny(connection.Input.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
                Assert.NotSame(connection.Input.Task, cancelled);
                Assert.Equal(1, confirmation.CallCount);

                confirmation.Approve = true;
                _ = SendMessage(terminalWindow, WmRButtonDown, IntPtr.Zero, IntPtr.Zero);
                _ = SendMessage(terminalWindow, WmRButtonUp, IntPtr.Zero, IntPtr.Zero);
                Assert.Equal("\u001B[200~" + multilineClipboardText + "\u001B[201~", await connection.Input.Task.WaitAsync(Timeout));
                Assert.Equal(2, confirmation.CallCount);
            }
            finally
            {
                terminal.Connection = null!;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Native_interceptor_leaves_right_click_with_an_active_selection_to_the_terminal()
    {
        await RunOnStaAsync(async () =>
        {
            const string multilineClipboardText = "WMP001_SELECTION_FIRST\r\nWMP001_SELECTION_SECOND";
            var connection = new RecordingTerminalConnection();
            var confirmation = new TestPasteConfirmationService(approve: true);
            var terminal = new TerminalControl { AutoResize = true };
            var window = new Window
            {
                Width = 640,
                Height = 400,
                Content = terminal,
            };

            try
            {
                await SetClipboardTextWithRetryAsync(multilineClipboardText);
                terminal.Connection = connection;
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                connection.PublishOutput("WMP001_SELECTED_TEXT");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                using var interceptor = new TerminalNativePasteInterceptor(
                    terminal,
                    snapshot => new TerminalSafePasteController(confirmation).Paste(window, snapshot, connection.WriteInput),
                    () => throw new Xunit.Sdk.XunitException("The controlled clipboard was unexpectedly unavailable."),
                    terminal.GetSelectedText);
                interceptor.Attach();

                var terminalWindow = FindDescendantWindow(new WindowInteropHelper(window).Handle, "HwndTerminalClass");
                Assert.NotEqual(IntPtr.Zero, terminalWindow);
                _ = SendMessage(terminalWindow, WmLButtonDown, (IntPtr)1, CreateMouseLParam(8, 8));
                _ = SendMessage(terminalWindow, WmMouseMove, (IntPtr)1, CreateMouseLParam(220, 8));
                _ = SendMessage(terminalWindow, WmLButtonUp, IntPtr.Zero, CreateMouseLParam(220, 8));

                _ = SendMessage(terminalWindow, WmRButtonDown, IntPtr.Zero, IntPtr.Zero);
                _ = SendMessage(terminalWindow, WmRButtonUp, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(TimeSpan.FromMilliseconds(250));

                Assert.DoesNotContain(multilineClipboardText, connection.Inputs);
                Assert.Equal(0, confirmation.CallCount);
            }
            finally
            {
                terminal.Connection = null!;
                window.Close();
            }
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
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(Timeout);
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)), "The WPF test thread did not terminate.");
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task SetClipboardTextWithRetryAsync(string text)
    {
        COMException? lastFailure = null;
        for (var attempt = 0; attempt != 20; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x800401D0))
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not acquire the Windows clipboard for the controlled native-paste test: {lastFailure?.Message}");
    }

    private static IntPtr CreateMouseLParam(short x, short y) => (IntPtr)((ushort)x | ((uint)(ushort)y << 16));

    private static IntPtr FindDescendantWindow(IntPtr parent, string className)
    {
        IntPtr terminalWindow = IntPtr.Zero;
        _ = EnumChildWindows(
            parent,
            (window, _) =>
            {
                var name = new StringBuilder(256);
                _ = GetClassName(window, name, name.Capacity);
                if (string.Equals(name.ToString(), className, StringComparison.Ordinal))
                {
                    terminalWindow = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return terminalWindow;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private sealed class TestPasteConfirmationService(bool approve) : ITerminalMultilinePasteConfirmationService
    {
        internal bool Approve { get; set; } = approve;

        internal int CallCount { get; private set; }

        internal int UnsafeContentCallCount { get; private set; }

        public bool Confirm(Window? owner, TerminalPastePreview preview)
        {
            CallCount++;
            return Approve;
        }

        public void ShowUnsafePasteContent(Window? owner) => UnsafeContentCallCount++;
    }

    private sealed class RecordingTerminalConnection : ITerminalConnection
    {
        private int inputCount;

        internal TaskCompletionSource<string> Input { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int InputCount => Volatile.Read(ref inputCount);

        internal List<string> Inputs { get; } = [];

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public void Start() => _ = TerminalOutput;

        public void WriteInput(string data)
        {
            Interlocked.Increment(ref inputCount);
            lock (Inputs)
            {
                Inputs.Add(data);
            }

            Input.TrySetResult(data);
        }

        internal void PublishOutput(string data) => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));

        public void Resize(uint rows, uint columns)
        {
        }

        public void Close()
        {
        }
    }
}
