using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using GabCode.Windows;

namespace GabCode.Windows.Tests;

public sealed class MainWindowBootstrapTests
{
    [Fact]
    public async Task MainWindow_exposes_gabCode_product_identity()
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            Window? window = null;

            try
            {
                window = new MainWindow();

                Assert.Equal("gabCode", window.Title);
                Assert.Equal("gabCode", AutomationProperties.GetName(window));
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
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completion.Task, completed), "WPF window initialization did not complete within five seconds.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)), "The WPF test thread did not terminate after cleanup.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
