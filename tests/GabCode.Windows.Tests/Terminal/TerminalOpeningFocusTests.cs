using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class TerminalOpeningFocusTests
{
    [Fact]
    public async Task Newly_opened_workspace_focuses_ready_top_pane_without_reclaiming_a_later_bottom_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode opening Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Opening", root), resolver, new AcceptClose());
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    var top = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var bottom = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => top.State == TerminalSessionState.Running && bottom.State == TerminalSessionState.Running);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (probe.IsForeground)
                    {
                        Assert.True(top.Session?.HasNativeFocus is true,
                            "The new top terminal did not own native keyboard focus after both panes became ready.");
                        Assert.False(bottom.Session?.HasNativeFocus is true);
                        bottom.FocusTerminal();
                        Assert.True(bottom.Session?.HasNativeFocus is true);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.True(bottom.Session?.HasNativeFocus is true,
                            "Late opening work reclaimed focus after the user chose the bottom pane.");
                    }
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pane_readiness_order_does_not_change_the_opening_top_target(bool delayTop)
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode readiness Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Readiness", root), resolver, new AcceptClose());
                var top = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                var bottom = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                var delayed = delayTop ? top : bottom;
                typeof(TerminalSessionView).GetField("startAttempted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(delayed, true);
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    await WaitUntil(() => (delayTop ? bottom : top).State == TerminalSessionState.Running);
                    Assert.False(delayed.State == TerminalSessionState.Running);
                    if (delayTop && probe.IsForeground) Assert.False(bottom.Session?.HasNativeFocus is true);
                    typeof(TerminalSessionView).GetField("startAttempted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(delayed, false);
                    await delayed.EnsureStartedAsync();
                    await WaitUntil(() => delayed.State == TerminalSessionState.Running);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (probe.IsForeground)
                    {
                        Assert.True(top.Session?.HasNativeFocus is true, "Top did not receive focus after the delayed pane became ready.");
                        Assert.False(bottom.Session?.HasNativeFocus is true);
                    }
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Swap_during_opening_tracks_the_new_top_pane_until_it_is_ready()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode swap Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Swap", root), resolver, new AcceptClose());
                window.ShowCommandsInMain(); // Intentional swap before either shell is attached to its native host.
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    var first = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var chosen = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => first.State == TerminalSessionState.Running && chosen.State == TerminalSessionState.Running);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (probe.IsForeground)
                    {
                        Assert.True(chosen.Session?.HasNativeFocus is true, "Opening followed the old top region rather than the user's swap.");
                        Assert.False(first.Session?.HasNativeFocus is true);
                    }
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Obsolete_background_opening_callback_does_not_activate_its_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode background Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Background", root), resolver, new AcceptClose());
                window.Show();
                var second = new Window { Content = new Button { Content = "New foreground" }, ShowInTaskbar = false, Width = 200, Height = 100 };
                try
                {
                    await WaitUntil(() => window.PiTerminal?.State == TerminalSessionState.Running);
                    second.Show();
                    using var probe = new NativeFocusProbe(second);
                    if (!probe.IsForeground) return;
                    typeof(MainWindow).GetField("openingFocusTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, window.PiTerminal);
                    typeof(MainWindow).GetMethod("TryApplyOpeningFocus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(probe.IsForeground, "An obsolete opening callback activated its former window.");
                }
                finally { second.Close(); window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Real_start_failure_presents_top_retry_instead_of_focusing_bottom()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode failed opening Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate("missing-gabcode-shell.exe", "broken")]);
                var window = new MainWindow(new ProjectContext("Failed", root), resolver, new AcceptClose());
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    var top = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var bottom = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => top.State == TerminalSessionState.Failed && bottom.State == TerminalSessionState.Failed);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if (probe.IsForeground)
                    {
                        Assert.True(Assert.IsType<Button>(top.FindName("RetryButton")).IsKeyboardFocused,
                            "Failed initial top shell did not offer its own focused Retry action.");
                        Assert.False(Assert.IsType<Button>(bottom.FindName("RetryButton")).IsKeyboardFocused);
                    }
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Bottom_choice_cancels_pending_top_focus_before_a_late_ready_signal()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode delayed top Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var registryResolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Delayed", root), registryResolver, new AcceptClose());
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    var top = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var bottom = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => top.State == TerminalSessionState.Running && bottom.State == TerminalSessionState.Running);
                    if (!probe.IsForeground) return;
                    // Re-create the pending readiness state without injecting keys or replacing the live shells.
                    typeof(MainWindow).GetField("openingFocusTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, top);
                    bottom.FocusTerminal();
                    Assert.True(bottom.Session?.HasNativeFocus is true);
                    Assert.Null(typeof(MainWindow).GetField("openingFocusTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
                    typeof(MainWindow).GetMethod("TryApplyOpeningFocus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(bottom.Session?.HasNativeFocus is true, "A late top readiness signal overrode the newer bottom choice.");
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Explicit_retry_focus_follows_the_replacement_native_terminal_when_ready()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode retry ready Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var attempts = 0;
                var view = new TerminalSessionView(TerminalSessionKind.First, root,
                    () => Interlocked.Increment(ref attempts) == 1
                        ? new TerminalProfileResolution("broken", "missing-gabcode-shell.exe", "", new Dictionary<string, string?>(), false, "broken")
                        : new TerminalProfileResolution("cmd", Environment.GetEnvironmentVariable("ComSpec")!, "/d /q", new Dictionary<string, string?>(), false, "cmd"));
                var window = new Window { Content = view, ShowInTaskbar = false, Width = 600, Height = 400 };
                window.Show();
                try
                {
                    using var probe = new NativeFocusProbe(window);
                    await WaitUntil(() => view.State == TerminalSessionState.Failed);
                    var retry = Assert.IsType<Button>(view.FindName("RetryButton"));
                    if (!probe.IsForeground) return;
                    Assert.True(retry.Focus());
                    await view.RetryAsync();
                    await WaitUntil(() => view.State == TerminalSessionState.Running);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(view.Session?.HasNativeFocus is true, "Explicit Retry did not hand input to its new terminal.");
                }
                finally { await view.CloseAsync(); window.Close(); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public async Task Failed_terminal_retry_focus_does_not_override_later_user_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode retry Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await OnSta(async () =>
            {
                var view = new TerminalSessionView(TerminalSessionKind.First, root,
                    () => new TerminalProfileResolution("cmd", Environment.GetEnvironmentVariable("ComSpec")!, "/d /q", new Dictionary<string, string?>(), false, "cmd"));
                var later = new Button { Content = "Later destination" };
                var panel = new StackPanel();
                panel.Children.Add(view);
                panel.Children.Add(later);
                var window = new Window { Content = panel, ShowInTaskbar = false, Width = 600, Height = 400 };
                window.Show();
                try
                {
                    await WaitUntil(() => view.State == TerminalSessionState.Running);
                    using var probe = new NativeFocusProbe(window);
                    if (!probe.IsForeground) return;
                    view.FocusTerminal();
                    Assert.True(view.Session?.HasNativeFocus is true);
                    typeof(TerminalSessionView).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, TerminalSessionState.Failed);
                    typeof(TerminalSessionView).GetMethod("ShowFailure", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [new InvalidOperationException("fixture failure")]);
                    Assert.True(later.Focus());
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Same(later, Keyboard.FocusedElement);
                }
                finally { await view.CloseAsync(); window.Close(); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Terminal opening did not reach the expected state."); await Task.Delay(20); }
    }

    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); completed.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class AcceptClose : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
