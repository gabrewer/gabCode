using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class WorktreeTerminalRegistryTests
{
    [Fact]
    public async Task Creates_terminal_pairs_lazily_and_reuses_them_by_normalized_worktree_path()
    {
        await RunOnStaAsync(() =>
        {
            var registry = new WorktreeTerminalRegistry(CreateCmdResolution);
            Assert.Empty(registry.Pairs);

            var first = registry.GetOrCreate("C:\\repo\\Main");
            var same = registry.GetOrCreate("c:\\repo\\main");
            var second = registry.GetOrCreate("C:\\repo\\wt\\feature");

            Assert.Same(first, same);
            Assert.NotSame(first, second);
            Assert.Equal(2, registry.Pairs.Count());
            Assert.Equal(0, registry.ActiveTerminalCount);
            return Task.CompletedTask;
        });
    }

    private static TerminalProfileResolution CreateCmdResolution() => new("cmd", Environment.GetEnvironmentVariable("ComSpec")!, "/d /q", new Dictionary<string, string?>(), false, "Test shell: cmd.");

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
