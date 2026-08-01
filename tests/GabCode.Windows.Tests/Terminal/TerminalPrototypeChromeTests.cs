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
    public async Task MainWindow_exposes_named_terminal_regions_selectors_and_lifecycle_status()
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

                Assert.IsType<Button>(window.FindName("PiMainSelector"));
                Assert.IsType<Button>(window.FindName("CommandsMainSelector"));
                Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion"));
                Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion"));
                var status = Assert.IsType<TextBlock>(window.FindName("TerminalLifecycleStatus"));
                Assert.Contains("Terminal lifecycle", AutomationProperties.GetName(status), StringComparison.Ordinal);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                var piSelector = Assert.IsType<Button>(window.FindName("PiMainSelector"));
                var commandsSelector = Assert.IsType<Button>(window.FindName("CommandsMainSelector"));
                Assert.Equal("Show Pi in the main terminal region", AutomationProperties.GetName(piSelector));
                Assert.Equal("Show Commands in the main terminal region", AutomationProperties.GetName(commandsSelector));
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
