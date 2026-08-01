using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Hosting;
using Microsoft.Terminal.Wpf;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class TerminalInputCompatibilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Focused_terminal_forwards_WPF_routed_navigation_editing_tab_and_application_cursor_input()
    {
        await RunOnStaAsync(async () =>
        {
            var connection = new RecordingConnection();
            var control = new TerminalControl { Focusable = true };
            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 500,
            };
            window.Show();
            using var forwarder = new TerminalKeyboardInputForwarder(control);
            control.Connection = connection;
            _ = control.Focus();
            forwarder.CaptureTerminalWindow();
            await Dispatcher.Yield(DispatcherPriority.Input);

            var source = PresentationSource.FromVisual(control) as HwndSource;
            Assert.NotNull(source);

            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);
            Assert.Equal("\u001b[A", await connection.NextInputAsync());

            SendKey(source.Handle, VirtualKey.Back, scanCode: 0x0e, character: '\b');
            Assert.Equal("\u007f", await connection.NextInputAsync());

            SendKey(source.Handle, VirtualKey.Tab, scanCode: 0x0f);
            Assert.Equal("\t", await connection.NextInputAsync());

            SendKeyWithModifier(source.Handle, VirtualKey.Shift, VirtualKey.Tab, scanCode: 0x0f);
            Assert.Equal("\u001b[Z", await connection.NextInputAsync());

            SendKey(source.Handle, VirtualKey.Delete, scanCode: 0x53, extended: true);
            Assert.Equal("\u001b[3~", await connection.NextInputAsync());

            SendKey(source.Handle, VirtualKey.F1, scanCode: 0x3b);
            Assert.Equal("\u001bOP", await connection.NextInputAsync());

            SendKeyWithModifier(source.Handle, VirtualKey.Control, VirtualKey.L, scanCode: 0x26, character: '\f');
            Assert.Equal("\f", await connection.NextInputAsync());

            connection.PublishOutput("\u001b[?1h");
            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);
            Assert.Equal("\u001bOA", await connection.NextInputAsync());

            window.Close();
        });
    }

    [Fact]
    public async Task Forwarder_attached_after_initial_terminal_focus_routes_the_first_key()
    {
        await RunOnStaAsync(async () =>
        {
            var connection = new RecordingConnection();
            var terminal = new TerminalControl { Focusable = true };
            var window = new Window { Content = terminal, Width = 900, Height = 500 };
            window.Show();
            terminal.Connection = connection;
            _ = terminal.Focus();

            using var forwarder = new TerminalKeyboardInputForwarder(terminal);
            var source = PresentationSource.FromVisual(terminal) as HwndSource;
            Assert.NotNull(source);
            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);

            Assert.Equal("\u001b[A", await connection.NextInputAsync());
            window.Close();
        });
    }

    [Fact]
    public async Task Forwarder_attached_while_a_sibling_is_focused_does_not_adopt_that_sibling()
    {
        await RunOnStaAsync(async () =>
        {
            var firstConnection = new RecordingConnection();
            var secondConnection = new RecordingConnection();
            var first = new TerminalControl { Focusable = true };
            var second = new TerminalControl { Focusable = true };
            var chromeButton = new Button { Content = "Chrome action" };
            var panel = new StackPanel();
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(chromeButton);
            var window = new Window { Content = panel, Width = 900, Height = 500 };
            window.Show();
            first.Connection = firstConnection;
            second.Connection = secondConnection;
            _ = first.Focus();

            using var firstForwarder = new TerminalKeyboardInputForwarder(first);
            using var secondForwarder = new TerminalKeyboardInputForwarder(second);
            _ = chromeButton.Focus();
            var source = PresentationSource.FromVisual(first) as HwndSource;
            Assert.NotNull(source);
            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);

            Assert.False(await firstConnection.HasInputAsync(), "WPF chrome input leaked into the focused sibling.");
            Assert.False(await secondConnection.HasInputAsync(), "An inactive forwarder adopted its focused sibling.");
            window.Close();
        });
    }

    [Fact]
    public async Task Only_the_focused_terminal_receives_each_forwarded_key_once()
    {
        await RunOnStaAsync(async () =>
        {
            var firstConnection = new RecordingConnection();
            var secondConnection = new RecordingConnection();
            var first = new TerminalControl { Focusable = true };
            var second = new TerminalControl { Focusable = true };
            var chromeButton = new Button { Content = "Chrome action" };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(first);
            Grid.SetColumn(second, 1);
            grid.Children.Add(second);
            Grid.SetRow(chromeButton, 1);
            Grid.SetColumnSpan(chromeButton, 2);
            grid.Children.Add(chromeButton);
            var window = new Window { Content = grid, Width = 900, Height = 500 };
            window.Show();
            using var firstForwarder = new TerminalKeyboardInputForwarder(first);
            using var secondForwarder = new TerminalKeyboardInputForwarder(second);
            first.Connection = firstConnection;
            second.Connection = secondConnection;

            _ = second.Focus();
            secondForwarder.CaptureTerminalWindow();
            _ = first.Focus();
            firstForwarder.CaptureTerminalWindow();
            await Dispatcher.Yield(DispatcherPriority.Input);

            var source = PresentationSource.FromVisual(first) as HwndSource;
            Assert.NotNull(source);
            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);

            Assert.Equal("\u001b[A", await firstConnection.NextInputAsync());
            Assert.False(await firstConnection.HasInputAsync(), "The active terminal received the same routed key more than once.");
            Assert.False(await secondConnection.HasInputAsync(), "The inactive terminal received input intended for its sibling.");

            _ = chromeButton.Focus();
            await Dispatcher.Yield(DispatcherPriority.Input);
            SendKey(source.Handle, VirtualKey.Up, scanCode: 0x48, extended: true);
            Assert.False(await firstConnection.HasInputAsync(), "WPF chrome input leaked into the previously focused terminal.");
            Assert.False(await secondConnection.HasInputAsync(), "WPF chrome input leaked into the inactive terminal.");
            window.Close();
        });
    }

    private static void SendKey(IntPtr sourceWindow, VirtualKey key, byte scanCode, bool extended = false, char? character = null)
    {
        _ = SendMessage(sourceWindow, KeyDownMessage, (IntPtr)(int)key, BuildKeyLParam(scanCode, extended, keyUp: false));
        _ = SendMessage(sourceWindow, KeyUpMessage, (IntPtr)(int)key, BuildKeyLParam(scanCode, extended, keyUp: true));
        if (character is not null)
        {
            _ = SendMessage(sourceWindow, CharacterMessage, (IntPtr)character.Value, BuildKeyLParam(scanCode, extended, keyUp: false));
        }
    }

    private static void SendKeyWithModifier(
        IntPtr sourceWindow,
        VirtualKey modifier,
        VirtualKey key,
        byte scanCode,
        char? character = null)
    {
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        var modified = (byte[])original.Clone();
        modified[(int)modifier] = 0x80;
        Assert.True(SetKeyboardState(modified));
        try
        {
            SendKey(sourceWindow, key, scanCode, character: character);
        }
        finally
        {
            Assert.True(SetKeyboardState(original));
        }
    }

    private static IntPtr BuildKeyLParam(byte scanCode, bool extended, bool keyUp)
    {
        var value = 1 | (scanCode << 16);
        if (extended)
        {
            value |= 1 << 24;
        }

        if (keyUp)
        {
            value |= unchecked((int)0xc0000000);
        }

        return (IntPtr)value;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "The WPF terminal test thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class RecordingConnection : ITerminalConnection
    {
        private readonly Queue<string> inputs = [];
        private readonly SemaphoreSlim inputAvailable = new(0);

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public void Start()
        {
        }

        public void WriteInput(string data)
        {
            lock (inputs)
            {
                inputs.Enqueue(data);
            }

            inputAvailable.Release();
        }

        public void Resize(uint rows, uint columns)
        {
        }

        public void Close()
        {
        }

        internal void PublishOutput(string data) => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));

        internal async Task<bool> HasInputAsync() => await inputAvailable.WaitAsync(TimeSpan.FromMilliseconds(250));

        internal async Task<string> NextInputAsync()
        {
            if (!await inputAvailable.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("The focused terminal did not forward the expected input to its connection.");
            }

            lock (inputs)
            {
                return inputs.Dequeue();
            }
        }
    }

    private enum VirtualKey
    {
        Back = 0x08,
        Tab = 0x09,
        Shift = 0x10,
        Control = 0x11,
        Up = 0x26,
        Delete = 0x2e,
        L = 0x4c,
        F1 = 0x70,
    }

    private const uint KeyDownMessage = 0x0100;
    private const uint KeyUpMessage = 0x0101;
    private const uint CharacterMessage = 0x0102;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] keyboardState);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keyboardState);
}
