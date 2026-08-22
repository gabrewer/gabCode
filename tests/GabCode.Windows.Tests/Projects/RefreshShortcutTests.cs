using System.Runtime.ExceptionServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class RefreshShortcutTests
{
    [Fact]
    public async Task Main_window_registers_f5_key_binding_for_refresh()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                var binding = Assert.Single(
                    window.InputBindings.OfType<KeyBinding>(),
                    candidate => candidate.Key == Key.F5 && candidate.Modifiers == ModifierKeys.None);
                Assert.DoesNotContain(
                    window.InputBindings.OfType<KeyBinding>(),
                    candidate => candidate.Key == Key.R && candidate.Modifiers == ModifierKeys.Control);

                Assert.NotNull(binding.Command);
                Assert.Same(window, binding.CommandTarget);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static async Task RunOnStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); done.TrySetResult(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
