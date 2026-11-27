using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class WindowsTerminalDetectedHyperlinkTests
{
    [Fact]
    public async Task Ctrl_click_activates_new_plain_url_but_not_replaced_text_or_ordinary_click()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                var connection = new TestConnection();
                var control = new TerminalControl { AutoResize = true, Connection = connection };
                var window = new Window { Width = 640, Height = 400, Content = control, ShowInTaskbar = false };
                var links = new List<string>();
                control.HyperlinkActivated += (_, uri) => links.Add(uri);
                try
                {
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var hwnd = FindTerminal(new WindowInteropHelper(window).Handle);
                    Assert.NotEqual(IntPtr.Zero, hwnd);
                    connection.Publish("https://example.");
                    connection.Publish("test/first");
                    using var streaming = new CancellationTokenSource();
                    var producer = Task.Run(async () =>
                    {
                        while (!streaming.IsCancellationRequested)
                        {
                            connection.Publish("\u001b7\u001b[2;1Hstreaming\u001b8");
                            await Task.Delay(25);
                        }
                    });
                    try
                    {
                    await Task.Delay(250);
                    Click(hwnd, false);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Empty(links);
                    Click(hwnd, true);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(new[] { "https://example.test/first" }, links);
                    connection.Publish("\u001b[1;1H\u001b[2Knot-a-link");
                    Click(hwnd, true);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Single(links);
                    connection.Publish("\r\u001b[2Khttps://example.test/second");
                    await Task.Delay(250);
                    Click(hwnd, true);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(new[] { "https://example.test/first", "https://example.test/second" }, links);
                    }
                    finally
                    {
                        streaming.Cancel();
                        await producer;
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { control.Connection = null!; window.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(3))); }
    }

    private static void Click(IntPtr hwnd, bool control)
    {
        var pos = (IntPtr)(3 | (3 << 16));
        SendMessage(hwnd, 0x0201, (IntPtr)(control ? 0x0009 : 0x0001), pos);
        SendMessage(hwnd, 0x0202, IntPtr.Zero, pos);
    }

    private static IntPtr FindTerminal(IntPtr parent)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "HwndTerminalClass") return true;
            result = hwnd;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class TestConnection : ITerminalConnection
    {
        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
        internal void Publish(string text) => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
        public void Start() { }
        public void WriteInput(string data) { }
        public void Resize(uint rows, uint columns) { }
        public void Close() { }
    }
}
