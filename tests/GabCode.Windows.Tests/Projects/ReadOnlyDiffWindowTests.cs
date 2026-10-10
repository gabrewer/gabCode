using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class ReadOnlyDiffWindowTests
{
    [Fact]
    public async Task Opening_and_replacing_document_keeps_live_shells_and_close_restores_original_main()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode diff window Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGit(root, "init", "-b", "trunk");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "one\n");
            await OnSta(async () =>
            {
                var resolver = new TerminalProfileResolver([], [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd", "/d /q")]);
                var window = new MainWindow(new ProjectContext("Review", root, "trunk"), resolver, new AcceptClose());
                window.Show();
                try
                {
                    var first = Assert.IsType<TerminalSessionView>(window.PiTerminal);
                    var second = Assert.IsType<TerminalSessionView>(window.CommandsTerminal);
                    await WaitUntil(() => first.ProcessId is not null && second.ProcessId is not null);
                    var firstPid = first.ProcessId;
                    var secondPid = second.ProcessId;
                    window.ShowCommandsInMain();
                    await window.OpenReviewDiffAsync(new ReviewFile("notes.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked));
                    var main = Assert.IsType<ContentControl>(window.FindName("MainTerminalRegion"));
                    var bottom = Assert.IsType<ContentControl>(window.FindName("BottomTerminalRegion"));
                    Assert.IsType<ReadOnlyDiffView>(main.Content);
                    var tabs = Assert.IsType<TabControl>(bottom.Content);
                    Assert.Same(first, Assert.IsType<TabItem>(tabs.Items[0]).Content);
                    Assert.Same(second, Assert.IsType<TabItem>(tabs.Items[1]).Content);
                    tabs.SelectedIndex = 0;
                    await window.OpenReviewDiffAsync(new ReviewFile("notes.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked));
                    Assert.Same(tabs, bottom.Content);
                    Assert.Equal(firstPid, first.ProcessId);
                    Assert.Equal(secondPid, second.ProcessId);
                    var view = Assert.IsType<ReadOnlyDiffView>(main.Content);
                    view.ApplyTemplate();
                    view.UpdateLayout();
                    var close = Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Close Diff"));
                    close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Assert.Same(second, main.Content);
                    Assert.Same(first, bottom.Content);
                    Assert.Equal(firstPid, first.ProcessId);
                    Assert.Equal(secondPid, second.ProcessId);
                }
                finally { window.Close(); await WaitUntil(() => !window.IsVisible); }
            });
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void External_open_requires_a_real_current_file_within_selected_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabcode diff paths " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "actual.txt");
            File.WriteAllText(existing, "x");
            Assert.Equal(existing, ReadOnlyDiffView.CurrentFile(root, new ReviewFile("actual.txt", null, ReviewGroup.Commit, ReviewStatus.Modified, "sha")));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("../escape.txt", null, ReviewGroup.Unstaged, ReviewStatus.Modified)));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("actual.txt", null, ReviewGroup.Commit, ReviewStatus.Deleted, "sha")));
            Assert.Null(ReadOnlyDiffView.CurrentFile(root, new ReviewFile("missing.txt", null, ReviewGroup.Staged, ReviewStatus.Added)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20); }
    }
    private static async Task RunGit(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
    }
    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); } catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); completion.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class AcceptClose : ITerminalExitConfirmationService
    {
        public TerminalExitDecision Confirm(Window owner, int activeTerminalCount) => TerminalExitDecision.CloseAndStopTerminals;
    }
}
