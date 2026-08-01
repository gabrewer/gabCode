using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class TerminalPrototypeChromeTests
{
    [Fact]
    public async Task MainWindow_exposes_compact_terminal_regions_and_swap_control()
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "gabCode terminal Ω", $"prototype {Guid.NewGuid():N} 漢字");
        Directory.CreateDirectory(workingDirectory);

        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new MainWindow(workingDirectory);

                var swapButton = Assert.IsType<Button>(window.FindName("SwapTerminalsButton"));
                Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion"));
                Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion"));
                Assert.Null(window.FindName("TerminalLifecycleStatus"));
                Assert.Equal("Swap the main and lower terminal regions", AutomationProperties.GetName(swapButton));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    window?.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
                finally
                {
                    completion.TrySetResult();
                }
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
