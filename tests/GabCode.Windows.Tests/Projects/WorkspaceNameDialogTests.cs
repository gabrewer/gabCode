using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class WorkspaceNameDialogTests
{
    [Fact]
    public async Task Dialog_uses_folder_name_only_as_an_editable_suggestion()
    {
        await RunOnStaAsync(() =>
        {
            var dialog = new WorkspaceNameDialog("repository");
            Assert.Equal("repository", dialog.WorkspaceName);
            dialog.WorkspaceName = "  My Project  ";
            Assert.Equal("My Project", dialog.WorkspaceName);
        });
    }

    private static async Task RunOnStaAsync(Action action)
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); completed.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
