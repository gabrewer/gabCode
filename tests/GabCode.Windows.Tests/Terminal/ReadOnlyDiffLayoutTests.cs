using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class ReadOnlyDiffLayoutTests
{
    [Fact]
    public async Task Opening_replacing_and_closing_diff_keeps_both_view_instances_and_restores_prior_main()
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var main = new ContentControl(); var bottom = new ContentControl();
                var first = new Border(); var second = new Border();
                var tabs = new TabControl();
                var firstTab = new TabItem { Header = "First" }; var secondTab = new TabItem { Header = "Second" };
                tabs.Items.Add(firstTab); tabs.Items.Add(secondTab);
                var layout = new RetainedTerminalLayout(main, bottom, first, second);
                layout.ShowCommandsInMain();
                var diffA = new TextBlock { Text = "first diff" };
                layout.OpenDocument(diffA, tabs);
                Assert.Same(diffA, main.Content);
                Assert.Same(first, firstTab.Content);
                Assert.Same(second, secondTab.Content);
                Assert.Same(tabs, bottom.Content);
                tabs.SelectedIndex = 1;
                var diffB = new TextBlock { Text = "another diff" };
                layout.OpenDocument(diffB, tabs);
                Assert.Same(diffB, main.Content);
                Assert.Same(first, firstTab.Content);
                Assert.Same(second, secondTab.Content);
                layout.CloseDocument();
                Assert.Same(second, main.Content);
                Assert.Same(first, bottom.Content);
                Assert.Null(firstTab.Content); Assert.Null(secondTab.Content);
                Assert.False(layout.IsPiInMain);
            }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); completed.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
