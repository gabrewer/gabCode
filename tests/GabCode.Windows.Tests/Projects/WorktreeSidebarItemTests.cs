using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class WorktreeSidebarItemTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Renders_running_icon_and_keeps_selection_in_accessible_state(bool selected, bool running)
    {
        await RunOnStaAsync(() =>
        {
            var entry = new WorktreeNavigationEntry(
                "C:\\repo\\wt\\feature",
                "feature/demo",
                false,
                WorktreeAvailability.Available,
                0,
                HasTerminalPair: running);

            var item = WorktreeSidebarItem.Create(entry, selected, running);

            Assert.Equal(running ? Visibility.Visible : Visibility.Collapsed, item.RunningIcon.Visibility);
            Assert.NotNull(item.RunningIcon.Data);
            Assert.Equal("feature, feature/demo" + (selected ? ", selected" : string.Empty) + (running ? ", running terminals" : string.Empty), AutomationProperties.GetName(item));
            Assert.Empty(AutomationProperties.GetName(item.RunningIcon));

            item.UpdateState(!selected, !running);
            Assert.Equal(!running ? Visibility.Visible : Visibility.Collapsed, item.RunningIcon.Visibility);
            Assert.Equal("feature, feature/demo" + (!selected ? ", selected" : string.Empty) + (!running ? ", running terminals" : string.Empty), AutomationProperties.GetName(item));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Busy_circle_appears_only_for_the_selected_refreshing_worktree_and_is_named()
    {
        await RunOnStaAsync(() =>
        {
            var entry = new WorktreeNavigationEntry("C:\\repo\\wt\\feature", "feature/demo", false,
                WorktreeAvailability.Available, 0, HasTerminalPair: false);
            var item = WorktreeSidebarItem.Create(entry, selected: true, hasRunningTerminals: false);
            item.UpdateState(selected: true, hasRunningTerminals: false, isRefreshingChanges: true);
            Assert.Equal(Visibility.Visible, item.RefreshIcon.Visibility);
            Assert.Contains("refreshing changes", AutomationProperties.GetName(item), StringComparison.OrdinalIgnoreCase);
            item.UpdateState(selected: false, hasRunningTerminals: false, isRefreshingChanges: true);
            Assert.Equal(Visibility.Collapsed, item.RefreshIcon.Visibility);
            Assert.DoesNotContain("refreshing changes", AutomationProperties.GetName(item), StringComparison.OrdinalIgnoreCase);
            item.UpdateState(selected: true, hasRunningTerminals: false, isRefreshingChanges: false);
            Assert.Equal(Visibility.Collapsed, item.RefreshIcon.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Icon_rendering_does_not_create_or_start_a_terminal_pair()
    {
        await RunOnStaAsync(() =>
        {
            var registry = new WorktreeTerminalRegistry(() =>
                new TerminalProfileResolution(
                    "cmd",
                    Environment.GetEnvironmentVariable("ComSpec")!,
                    "/d /q",
                    new Dictionary<string, string?>(),
                    false,
                    "test"));
            var entry = new WorktreeNavigationEntry(
                "C:\\repo\\wt\\feature",
                "feature/demo",
                false,
                WorktreeAvailability.Available,
                0,
                HasTerminalPair: false);

            _ = WorktreeSidebarItem.Create(entry, selected: true, hasRunningTerminals: false);

            Assert.Empty(registry.Pairs);
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
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
