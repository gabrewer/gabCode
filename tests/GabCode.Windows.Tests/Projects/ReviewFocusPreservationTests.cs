using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class ReviewFocusPreservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_review_restore_cannot_steal_focus_after_user_moves_to_another_control(bool moveWithinTree)
    {
        await OnSta(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "focus tree Ω " + Guid.NewGuid().ToString("N"));
            using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
            var outside = new Button { Content = "Terminal focus stand-in" };
            var panel = new DockPanel();
            panel.Children.Add(outside);
            panel.Children.Add(sidebar);
            var window = new Window { Content = panel, Width = 680, Height = 480, ShowInTaskbar = false };
            window.Show();
            try
            {
                typeof(WorktreeReviewSidebar).GetField("worktree", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidebar, root);
                var expanded = (HashSet<string>)typeof(WorktreeReviewSidebar).GetField("expanded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sidebar)!;
                expanded.UnionWith(["working", "staged", "unstaged"]);
                var file = new ReviewFile("one.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked);
                var otherFile = new ReviewFile("two.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked);
                var snapshot = new ReviewSnapshot(root, null, null, [file, otherFile], [], false, null);
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar).GetField("coordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sidebar)!;
                typeof(WorktreeChangeReviewCoordinator).GetProperty("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, snapshot);
                Render(sidebar, snapshot);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var button = Assert.Single(Buttons(sidebar), b => Equals(b.Tag, file.Identity));
                Assert.True(button.Focus());
                Render(sidebar, snapshot); // Captures button, replaces it and schedules focus restoration.
                window.UpdateLayout();
                var destination = moveWithinTree
                    ? Assert.Single(Buttons(sidebar), b => Equals(b.Tag, otherFile.Identity))
                    : outside;
                Assert.True(destination.Focus()); // Newer user intent before the scheduled callback.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(destination, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false, "create_schemas.sql", null)]
    [InlineData(true, "create_schemas.sql", null)]
    [InlineData(true, "create__schemas.sql", null)]
    [InlineData(true, "renamed.sql", "create_schemas.sql")]
    public async Task File_name_underscore_must_not_register_an_s_access_key_that_opens_a_diff(bool appChrome, string path, string? oldPath)
    {
        await OnSta(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "review mnemonic Ω " + Guid.NewGuid().ToString("N"));
            using var sidebar = new WorktreeReviewSidebar(new WorktreeChangeReviewService());
            Window window = appChrome ? new MainWindow() : new Window();
            window.Content = sidebar;
            window.Width = 680;
            window.Height = 480;
            window.ShowInTaskbar = false;
            window.Show();
            try
            {
                typeof(WorktreeReviewSidebar).GetField("worktree", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidebar, root);
                var expanded = (HashSet<string>)typeof(WorktreeReviewSidebar).GetField("expanded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sidebar)!;
                expanded.UnionWith(["working", "staged", "unstaged"]);
                var file = new ReviewFile(path, oldPath, ReviewGroup.Unstaged, oldPath is null ? ReviewStatus.Untracked : ReviewStatus.Renamed);
                var snapshot = new ReviewSnapshot(root, null, null, [file], [], false, null);
                var coordinator = (WorktreeChangeReviewCoordinator)typeof(WorktreeReviewSidebar).GetField("coordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sidebar)!;
                typeof(WorktreeChangeReviewCoordinator).GetProperty("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, snapshot);
                var activations = 0;
                sidebar.DiffRequested += (_, _) => activations++;
                Render(sidebar, snapshot);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var button = Assert.Single(Buttons(sidebar), b => Equals(b.Tag, file.Identity));
                button.ApplyTemplate();
                button.UpdateLayout();
                var scope = PresentationSource.FromVisual(window)!;
                AccessKeyManager.ProcessKey(scope, "S", false); // Scoped WPF mnemonic processing, not a desktop keystroke.
                Assert.Equal(0, activations);
                Assert.False(AccessKeyManager.IsKeyRegistered(scope, "S"));
                Assert.Contains(path, System.Windows.Automation.AutomationProperties.GetName(button));
                var literal = Assert.Single(Descendants<TextBlock>(button));
                Assert.Equal(button.Content, literal.Text); // Rendered underscores and old rename path stay literal.
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, activations); // Deliberately opening the file still works.
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Delayed_document_open_focus_cannot_override_newer_user_focus()
    {
        await OnSta(async () =>
        {
            var document = new ReadOnlyDiffView("read-only review", null);
            var outside = new Button { Content = "Later focus destination" };
            var panel = new StackPanel();
            panel.Children.Add(document);
            panel.Children.Add(outside);
            var window = new Window { Content = panel, Width = 680, Height = 480, ShowInTaskbar = false };
            document.FocusDocument(); // Queues focus for when the view loads.
            window.Show();
            try
            {
                Assert.True(outside.Focus());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(outside, Keyboard.FocusedElement);
            }
            finally { document.DisposeViewer(); window.Close(); }
        });
    }

    [Fact]
    public async Task Delayed_document_open_focus_cannot_override_later_navigation_within_document()
    {
        await OnSta(async () =>
        {
            var document = new ReadOnlyDiffView("read-only review", null);
            var later = new Button { Content = "Later in-document destination" };
            ((Grid)document.Content).Children.Add(later);
            Grid.SetRow(later, 3);
            var window = new Window { Content = document, Width = 680, Height = 480, ShowInTaskbar = false };
            document.FocusDocument();
            window.Show();
            try
            {
                Assert.True(later.Focus()); // Explicit later choice, despite remaining inside the opening view.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(later, Keyboard.FocusedElement);
            }
            finally { document.DisposeViewer(); window.Close(); }
        });
    }

    private static void Render(WorktreeReviewSidebar sidebar, ReviewSnapshot snapshot) =>
        typeof(WorktreeReviewSidebar).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(ReviewSnapshot)])!.Invoke(sidebar, [snapshot]);

    private static IEnumerable<Button> Buttons(DependencyObject root) => Descendants<Button>(root);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); done.TrySetResult(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
