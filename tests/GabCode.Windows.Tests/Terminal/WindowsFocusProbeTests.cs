using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class WindowsFocusProbeTests
{
    [Fact]
    public async Task Probe_refuses_to_certify_a_hidden_or_nonforeground_window()
    {
        await OnSta(async () =>
        {
            var window = new Window { Content = new TextBox(), Width = 240, Height = 100,
                Left = -2000, Top = -2000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                using var probe = new NativeFocusProbe(window);
                Assert.NotEqual(IntPtr.Zero, probe.WindowHandle);
                window.Hide();
                Assert.False(probe.CanTargetKeyboardInput());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(probe.CanTargetKeyboardInput());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Probe_does_not_report_input_ownership_from_remembered_WPF_focus_alone()
    {
        await OnSta(async () =>
        {
            var input = new TextBox();
            var window = new Window { Content = input, Width = 240, Height = 100,
                Left = -2000, Top = -2000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                using var probe = new NativeFocusProbe(window);
                _ = Keyboard.Focus(input);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // A real foreground HWND is required regardless of WPF's remembered focus.
                if (!probe.IsForeground) Assert.False(probe.CanTargetKeyboardInput());
                window.Hide();
                Assert.False(probe.CanTargetKeyboardInput());
            }
            finally { window.Close(); }
        });
    }

    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); done.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
