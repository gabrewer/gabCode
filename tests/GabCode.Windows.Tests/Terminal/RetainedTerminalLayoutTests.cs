using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class RetainedTerminalLayoutTests
{
    [Fact]
    public async Task Layout_moves_the_same_pi_and_commands_views_between_regions_without_replacement()
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var mainRegion = new ContentControl();
                var bottomRegion = new ContentControl();
                var pi = new Border { Name = "PiSurface" };
                var commands = new Border { Name = "CommandsSurface" };
                var layout = new RetainedTerminalLayout(mainRegion, bottomRegion, pi, commands);

                layout.ShowPiInMain();
                Assert.Same(pi, mainRegion.Content);
                Assert.Same(commands, bottomRegion.Content);

                layout.ShowCommandsInMain();
                Assert.Same(commands, mainRegion.Content);
                Assert.Same(pi, bottomRegion.Content);
                Assert.Same(pi, layout.PiView);
                Assert.Same(commands, layout.CommandsView);

                layout.ShowPiInMain();
                Assert.Same(pi, mainRegion.Content);
                Assert.Same(commands, bottomRegion.Content);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completion.TrySetResult();
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)), "The WPF test thread did not terminate.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
