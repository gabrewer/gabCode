using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class CloseWorkspaceDialogTests
{
    [Fact]
    public async Task Close_dialog_identifies_target_and_preserves_Git_worktree_when_no_terminals_are_active()
    {
        await RunOnStaAsync(() =>
        {
            var entry = new WorktreeNavigationEntry("C:\\repo\\wt\\feature", "feature/close", false, WorktreeAvailability.Available, 0, false);
            var dialog = new CloseWorkspaceDialog(entry, activeTerminals: 0);
            var content = Assert.IsType<StackPanel>(dialog.Content);
            var text = string.Join("\n", content.Children.OfType<TextBlock>().Select(item => item.Text));
            var buttons = content.Children.OfType<StackPanel>().Single().Children.OfType<Button>();
            var close = Assert.Single(buttons, button => (string)button.Content == "Close Workspace");

            Assert.Contains("feature", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("feature/close", text, StringComparison.Ordinal);
            Assert.Contains("Git worktree, branch, and files will remain", text, StringComparison.Ordinal);
            Assert.Contains("No active gabCode terminals", text, StringComparison.Ordinal);
            Assert.True(close.IsDefault);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Close_dialog_warns_when_target_has_active_terminals()
    {
        await RunOnStaAsync(() =>
        {
            var entry = new WorktreeNavigationEntry("C:\\repo\\wt\\feature", "feature/close", false, WorktreeAvailability.Available, 0, true);
            var dialog = new CloseWorkspaceDialog(entry, activeTerminals: 2);
            var text = string.Join("\n", ((StackPanel)dialog.Content).Children.OfType<TextBlock>().Select(item => item.Text));

            Assert.Contains("2 active gabCode terminal process", text, StringComparison.Ordinal);
            Assert.Contains("Running shell work will be interrupted", text, StringComparison.Ordinal);
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
