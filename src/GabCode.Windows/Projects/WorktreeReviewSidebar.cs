using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;

namespace GabCode.Windows.Projects;

/// <summary>Selected-worktree-only, read-only review. UI state is ephemeral and never persisted as Git authority.</summary>
internal sealed class WorktreeReviewSidebar : UserControl, IDisposable
{
    private readonly WorktreeChangeReviewService service;
    private readonly WorktreeChangeReviewCoordinator coordinator;
    private readonly DispatcherTimer debounce;
    private readonly DispatcherTimer poll;
    private readonly WatcherInvalidation invalidation = new();
    private readonly object errorGate = new();
    private (FileSystemWatcher Watcher, Exception Error, string Directory, long Generation)? pendingWatcherError;
    private bool errorPosted;
    private bool overflowPending;
    private long overflowVersion;
    private readonly DockPanel root;
    private readonly TextBlock titleText;
    private readonly TextBlock worktreeNameText;
    private readonly System.Windows.Shapes.Path refreshIcon;
    private bool refreshAnimationActive;
    private readonly Button cancelReviewRefresh;
    private readonly Button retryReviewRefresh;
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroller;
    private readonly Dictionary<string, Button> fileButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> sections = new(StringComparer.Ordinal);
    private readonly TextBlock status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, IReadOnlyList<ReviewFile>> commitFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> commitFileOffsets = new(StringComparer.Ordinal);
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> loadingCommits = new(StringComparer.Ordinal);
    private readonly List<ReviewCommit> history = [];
    private int workingOffset;
    private const int WorkingPageSize = 200;
    private const int MaximumHistoryPages = 4;
    private readonly List<FileSystemWatcher> watchers = [];
    private string? watcherWarning;
    private CancellationTokenSource? lazyCancellation;
    private string? worktree;
    private string? mainBranch;
    private string? head;
    private string? mainHead;
    private string? activeFileIdentity;
    private bool moreAvailable = true;
    private bool loadingHistoryPage;
    private Button? loadMoreButton;
    private bool refreshRunning;
    private bool refreshPending;
    private long refreshEpoch;
    private long generation;
    private long renderEpoch;
    private bool disposed;
    internal int WatcherCount => watchers.Count;
    internal string? CurrentWatcherWarning => watcherWarning;
    internal bool IsDisposed => disposed;
    internal bool IsRefreshing => refreshRunning && !disposed;
    internal bool HasFreshSnapshot => coordinator.Snapshot is { IsStale: false };
    internal event Action<ReviewFile, UIElement>? DiffRequested;
    internal event Action<ReviewFile>? SelectionRetained;
    internal event Action? SelectionInvalidated;

    internal WorktreeReviewSidebar(WorktreeChangeReviewService service)
    {
        this.service = service;
        coordinator = new WorktreeChangeReviewCoordinator((path, branch, token) => service.GetSnapshotAsync(path, branch, token));
        coordinator.Changed += snapshot => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(coordinator.Snapshot, snapshot)) Render(snapshot);
        });
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        debounce.Tick += (_, _) =>
        {
            // Fixed-window batching: continued file writes cannot postpone reconciliation forever.
            if (invalidation.Consume()) _ = RefreshReviewAsync();
        };
        poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        poll.Tick += (_, _) => { if (worktree is null) return; if (watchers.Count == 0) StartWatchers(worktree); _ = RefreshReviewAsync(); };
        root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(29, 30, 36)) };
        var title = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        cancelReviewRefresh = new Button { Content = "Cancel", Padding = new Thickness(5, 2, 5, 2), Visibility = Visibility.Hidden };
        retryReviewRefresh = new Button { Content = "Retry", Padding = new Thickness(5, 2, 5, 2), Visibility = Visibility.Hidden };
        AutomationProperties.SetName(cancelReviewRefresh, "Cancel review refresh");
        cancelReviewRefresh.Click += (_, _) =>
        {
            refreshPending = false;
            coordinator.CancelAndPause();
            cancelReviewRefresh.Visibility = Visibility.Hidden;
            retryReviewRefresh.Visibility = Visibility.Visible;
            retryReviewRefresh.Focus();
        };
        AutomationProperties.SetName(retryReviewRefresh, "Retry review refresh");
        retryReviewRefresh.Click += (_, _) =>
        {
            if (!coordinator.Retry()) { SetStatus(coordinator.StopMessage ?? "Waiting for the previous Git refresh to stop. Retry again shortly."); return; }
            var returnFocus = retryReviewRefresh.IsKeyboardFocused;
            retryReviewRefresh.Visibility = Visibility.Hidden;
            _ = RefreshReviewAsync();
            if (returnFocus)
            {
                if (cancelReviewRefresh.Visibility == Visibility.Visible) cancelReviewRefresh.Focus();
                else scroller?.Focus();
            }
        };
        DockPanel.SetDock(cancelReviewRefresh, Dock.Right); title.Children.Add(cancelReviewRefresh);
        DockPanel.SetDock(retryReviewRefresh, Dock.Right); title.Children.Add(retryReviewRefresh);
        titleText = new TextBlock { Text = "Changes and commits", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(titleText);
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        // Keep the worktree name and the spinner's footprint in the same permanent row.
        var worktreeRow = new Grid { MinHeight = 24, Margin = new Thickness(0, 0, 0, 4) };
        worktreeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        worktreeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        refreshIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9,2 A7,7 0 1 1 2,9"),
            Width = 16, Height = 16, StrokeThickness = 2,
            Stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White,
            Fill = Brushes.Transparent, Stretch = Stretch.Uniform,
            RenderTransform = new RotateTransform(), RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(refreshIcon, string.Empty);
        refreshIcon.Loaded += (_, _) => UpdateRefreshIndicator();
        refreshIcon.Unloaded += (_, _) => StopRefreshAnimation();
        worktreeNameText = new TextBlock
        {
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(refreshIcon, 1);
        worktreeRow.Children.Add(refreshIcon);
        worktreeRow.Children.Add(worktreeNameText);
        DockPanel.SetDock(worktreeRow, Dock.Top); root.Children.Add(worktreeRow);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows };
        AutomationProperties.SetName(scroller, "Selected worktree changes and commits");
        root.Children.Add(scroller);
        Content = root;
        ApplySystemColors();
        SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            ApplySystemColors();
            Render(coordinator.Snapshot);
            UpdateRefreshIndicator();
        }
        else if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation))
            UpdateRefreshIndicator();
    }

    private void ApplySystemColors()
    {
        var contrast = SystemParameters.HighContrast;
        root.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(29, 30, 36));
        titleText.Foreground = status.Foreground = worktreeNameText.Foreground = contrast ? SystemColors.WindowTextBrush : Brushes.White;
        if (contrast)
        {
            var systemButton = new Style(typeof(Button));
            systemButton.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.ControlTextBrush));
            systemButton.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.ControlBrush));
            systemButton.Setters.Add(new Setter(Control.BorderBrushProperty, SystemColors.ControlTextBrush));
            root.Resources[typeof(Button)] = systemButton;
        }
        else root.Resources.Remove(typeof(Button));
    }

    internal void Select(string path, string? branch)
    {
        if (disposed) return;
        if (string.Equals(path, worktree, StringComparison.OrdinalIgnoreCase) && branch == mainBranch) { RequestRefresh(); return; }
        ++generation;
        ++refreshEpoch; refreshRunning = refreshPending = false;
        UpdateRefreshIndicator();
        overflowPending = false;
        debounce.Stop(); invalidation.Reset();
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose(); lazyCancellation = null;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear(); watcherWarning = null;
        expanded.Clear(); expanded.UnionWith(["working", "staged", "unstaged"]);
        commitFiles.Clear(); commitFileOffsets.Clear(); loadingCommits.Clear(); history.Clear(); head = null; mainHead = null; moreAvailable = true; loadingHistoryPage = false; loadMoreButton = null; workingOffset = 0;
        var hadSelection = activeFileIdentity is not null;
        activeFileIdentity = null;
        worktree = path; mainBranch = branch;
        worktreeNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;
        AutomationProperties.SetName(worktreeNameText, "Worktree " + worktreeNameText.Text);
        if (hadSelection) SelectionInvalidated?.Invoke();
        coordinator.Select(path, branch);
        SetStatus(coordinator.StopMessage ?? (coordinator.IsPaused ? "Git refresh stopped. Automatic refresh is paused. Retry to update." : string.Empty));
        rows.Children.Clear();
        retryReviewRefresh.Visibility = coordinator.IsPaused ? Visibility.Visible : Visibility.Hidden;
        cancelReviewRefresh.Visibility = Visibility.Hidden;
        StartWatchers(path);
        poll.Start();
        _ = RefreshReviewAsync();
    }

    internal void Suspend()
    {
        if (disposed) return;
        ++generation;
        ++refreshEpoch; refreshRunning = refreshPending = false;
        UpdateRefreshIndicator();
        overflowPending = false;
        invalidation.Reset();
        debounce.Stop(); poll.Stop();
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose(); lazyCancellation = null;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        var hadSelection = activeFileIdentity is not null;
        activeFileIdentity = null;
        worktree = null; mainBranch = null;
        worktreeNameText.Text = string.Empty;
        AutomationProperties.SetName(worktreeNameText, string.Empty);
        coordinator.Select(null, null);
        rows.Children.Clear(); fileButtons.Clear(); sections.Clear();
        SetStatus("Review unavailable for orphaned terminals. Select an available worktree.");
        if (hadSelection) SelectionInvalidated?.Invoke();
    }

    private void StartWatchers(string path)
    {
        invalidation.Reset();
        lock (errorGate) { pendingWatcherError = null; errorPosted = false; }
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        overflowPending = false;
        watcherWarning = "File watcher unavailable; periodic refresh remains active.";
        var watcherGeneration = invalidation.Generation;
        try
        {
            var gitEntry = Path.Combine(path, ".git");
            string? gitDir = null;
            if (Directory.Exists(gitEntry)) gitDir = gitEntry;
            else if (File.Exists(gitEntry))
            {
                var line = File.ReadLines(gitEntry).FirstOrDefault();
                if (line is not null && line.StartsWith("gitdir: ", StringComparison.OrdinalIgnoreCase))
                    gitDir = Path.GetFullPath(Path.Combine(path, line[8..].Trim()));
            }
            string? commonDir = null;
            if (gitDir is not null && File.Exists(Path.Combine(gitDir, "commondir")))
                commonDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(Path.Combine(gitDir, "commondir")).Trim()));
            var roots = new[] { path, gitDir, commonDir }.OfType<string>()
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var directory in roots.Where(candidate => !roots.Any(parent =>
                         !string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) &&
                         IsInside(candidate, parent))))
            {
                if (!Directory.Exists(directory)) continue;
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += (_, _) => invalidation.Signal(watcherGeneration);
                watcher.Created += (_, _) => invalidation.Signal(watcherGeneration);
                watcher.Deleted += (_, _) => invalidation.Signal(watcherGeneration);
                watcher.Renamed += (_, _) => invalidation.Signal(watcherGeneration);
                watcher.Error += (sender, args) => QueueWatcherError((FileSystemWatcher)sender, args.GetException(), directory, watcherGeneration);
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            if (watchers.Count > 0) { watcherWarning = null; debounce.Start(); }
        }
        catch (Exception error)
        {
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();
            watcherWarning = "File watcher unavailable; periodic and manual refresh remain active: " + error.Message;
        }
        if (coordinator.Snapshot is not null) Render(coordinator.Snapshot);
    }

    private static bool IsInside(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private void QueueWatcherError(FileSystemWatcher watcher, Exception error, string directory, long watcherGeneration)
    {
        lock (errorGate)
        {
            if (watcherGeneration != invalidation.Generation) return;
            // Preserve a fatal failure over a recoverable overflow within this batch.
            if (pendingWatcherError is null ||
                pendingWatcherError.Value.Error is InternalBufferOverflowException && error is not InternalBufferOverflowException)
                pendingWatcherError = (watcher, error, directory, watcherGeneration);
            if (errorPosted) return;
            errorPosted = true;
        }
        Dispatcher.BeginInvoke(ProcessWatcherError);
    }

    private void ProcessWatcherError()
    {
        (FileSystemWatcher Watcher, Exception Error, string Directory, long Generation)? next;
        lock (errorGate) { next = pendingWatcherError; pendingWatcherError = null; errorPosted = false; }
        if (next is not { } fault || disposed || fault.Generation != invalidation.Generation ||
            !watchers.Contains(fault.Watcher)) return;
        if (fault.Error is InternalBufferOverflowException)
        {
            // Leave the watcher live. Git is the source of truth after dropped notifications.
            watcherWarning = $"File watcher overflow ({fault.Directory}, InternalBufferOverflowException); reconciling from Git.";
            overflowPending = true;
            ++overflowVersion;
            if (!refreshRunning) coordinator.MarkStale(watcherWarning);
            else Render(coordinator.Snapshot);
            _ = RefreshReviewAsync();
        }
        else
        {
            var error = $"File watcher failed ({fault.Directory}, {fault.Error.GetType().Name}): {fault.Error.Message}";
            foreach (var active in watchers) active.Dispose();
            watchers.Clear();
            invalidation.Reset();
            debounce.Stop();
            watcherWarning = error;
            coordinator.MarkStale(error);
            _ = RefreshReviewAsync(); // Git stays authoritative even without a live watcher.
        }
    }

    internal void RecoverWatcherFailure(string? error = null)
    {
        if (disposed || worktree is null) return;
        watcherWarning = error ?? "File watcher failed; retrying with polling.";
        coordinator.MarkStale(watcherWarning);
        StartWatchers(worktree);
        _ = RefreshReviewAsync();
    }

    private async Task RefreshReviewAsync()
    {
        if (disposed || worktree is null) return;
        if (!coordinator.CanRefresh) { Render(coordinator.Snapshot); return; }
        if (refreshRunning) { refreshPending = true; return; }
        refreshRunning = true;
        UpdateRefreshIndicator();
        cancelReviewRefresh.Visibility = Visibility.Visible;
        retryReviewRefresh.Visibility = Visibility.Hidden;
        var epoch = refreshEpoch;
        var followups = 0;
        try
        {
            do
            {
                refreshPending = false;
                var readVersion = overflowVersion;
                await coordinator.RefreshAsync();
                if (disposed || epoch != refreshEpoch) return;
                if (coordinator.IsPaused && !coordinator.CanRefresh) { refreshPending = false; Render(coordinator.Snapshot); break; }
                // A read that began before an overflow cannot repair the lost notifications.
                if (overflowPending && coordinator.Snapshot?.IsStale is false && watchers.Count > 0 &&
                    readVersion == overflowVersion)
                {
                    overflowPending = false;
                    watcherWarning = null;
                    Render(coordinator.Snapshot);
                }
                if (refreshPending && coordinator.CanRefresh)
                {
                    if (++followups > 2)
                    {
                        refreshPending = false;
                        coordinator.PauseForSafety("Git refresh stopped to keep the app responsive after sustained changes. Automatic refresh is paused; Retry to reconcile.");
                        break;
                    }
                    // Coalesce continuing watcher/focus hints instead of immediately repeating expensive Git reads.
                    await Task.Delay(TimeSpan.FromSeconds(followups));
                    if (disposed || epoch != refreshEpoch) return;
                }
            } while (refreshPending && coordinator.CanRefresh);
        }
        finally
        {
            if (epoch == refreshEpoch)
            {
                refreshRunning = false;
                UpdateRefreshIndicator();
                var returnFocus = cancelReviewRefresh.IsKeyboardFocused;
                cancelReviewRefresh.Visibility = Visibility.Hidden;
                retryReviewRefresh.Visibility = coordinator.IsPaused ? Visibility.Visible : Visibility.Hidden;
                if (returnFocus)
                {
                    if (retryReviewRefresh.Visibility == Visibility.Visible) retryReviewRefresh.Focus();
                    else scroller.Focus();
                }
            }
        }
    }

    internal void RequestRefresh()
    {
        if (disposed || worktree is null) return;
        if (!coordinator.CanRefresh) { Render(coordinator.Snapshot); return; }
        _ = RefreshReviewAsync();
    }

    internal bool FocusFile(ReviewFile file)
    {
        return fileButtons.TryGetValue(file.Identity, out var button) && button.IsVisible && button.Focus();
    }

    private void Render(ReviewSnapshot? snapshot)
    {
        if (disposed || worktree is null || snapshot is not null && !string.Equals(snapshot.Worktree, worktree, StringComparison.OrdinalIgnoreCase)) return;
        var focusedKey = (Keyboard.FocusedElement as Button)?.Tag as string;
        var focusedSection = FocusedSectionKey(Keyboard.FocusedElement as DependencyObject);
        var offset = scroller.VerticalOffset;
        var renderedWorktree = worktree;
        var renderedSnapshot = coordinator.Snapshot;
        var epoch = ++renderEpoch;
        if (snapshot is null) { rows.Children.Clear(); return; }
        var newHead = snapshot.Head;
        if (newHead != head || snapshot.MainHead != mainHead)
        {
            ++generation;
            lazyCancellation?.Cancel(); lazyCancellation?.Dispose(); lazyCancellation = null;
            loadingCommits.Clear();
            head = newHead; mainHead = snapshot.MainHead;
            history.Clear(); commitFiles.Clear(); commitFileOffsets.Clear(); moreAvailable = true;
            loadingHistoryPage = false; loadMoreButton = null;
            if (activeFileIdentity is { } selected && selected.StartsWith("Commit:", StringComparison.Ordinal) &&
                !snapshot.IsStale && newHead is not null && mainHead is not null &&
                !snapshot.Commits.Any(commit => selected.StartsWith("Commit:" + commit.Sha + ":", StringComparison.Ordinal)))
                _ = ValidateSelectedCommitAsync(selected, newHead, mainHead);
        }
        if (activeFileIdentity is { } identity && !snapshot.IsStale && !identity.StartsWith("Commit:", StringComparison.Ordinal))
        {
            var retained = snapshot.Working.FirstOrDefault(file => file.Identity == identity);
            if (retained is null) { activeFileIdentity = null; SelectionInvalidated?.Invoke(); }
            else SelectionRetained?.Invoke(retained);
        }
        if (history.Count == 0) history.AddRange(snapshot.Commits);
        else if (!snapshot.IsStale)
        {
            // The head is stable; keep fetched pages, but refresh the first page from Git.
            history.RemoveRange(0, Math.Min(snapshot.Commits.Count, history.Count));
            history.InsertRange(0, snapshot.Commits);
        }
        if (workingOffset >= snapshot.Working.Count) workingOffset = 0;
        var workingPage = snapshot.Working.Skip(workingOffset).Take(WorkingPageSize).ToArray();
        var message = snapshot.IsStale
            ? $"Git refresh stopped. {(!snapshot.WasSuccessful ? "No current review data." : "Showing previous results.")} {(coordinator.IsPaused ? "Automatic refresh is paused. " : string.Empty)}{snapshot.Error}"
            : watcherWarning ?? string.Empty;
        if (snapshot.Working.Count > WorkingPageSize)
            message += $" Showing working changes {workingOffset + 1}–{workingOffset + workingPage.Length} of {snapshot.Working.Count}; not all rows are visible.";
        SetStatus(message.Trim());
        rows.Children.Clear(); fileButtons.Clear(); sections.Clear(); loadMoreButton = null;
        if (snapshot.IsStale && !snapshot.WasSuccessful) { rows.Children.Add(Notice("No current review data. Retry refresh.")); return; }
        AddSection(rows, "Working changes", "working", content =>
        {
            if (snapshot.Working.Count == 0) content.Children.Add(Notice("Working tree clean"));
            AddGroup(content, "Staged", workingPage.Where(f => f.Group == ReviewGroup.Staged).ToArray(), "staged");
            AddGroup(content, "Unstaged", workingPage.Where(f => f.Group == ReviewGroup.Unstaged).ToArray(), "unstaged");
            if (workingOffset > 0)
            {
                var previous = new Button { Content = "Previous working changes" };
                AutomationProperties.SetName(previous, "Previous working changes");
                previous.Click += (_, _) => { workingOffset -= WorkingPageSize; Render(coordinator.Snapshot ?? snapshot); };
                content.Children.Add(previous);
            }
            if (workingOffset + WorkingPageSize < snapshot.Working.Count)
            {
                var next = new Button { Content = "Next working changes" };
                AutomationProperties.SetName(next, "Next working changes");
                next.Click += (_, _) => { workingOffset += WorkingPageSize; Render(coordinator.Snapshot ?? snapshot); };
                content.Children.Add(next);
            }
        });
        var commits = history.ToArray();
        AddSection(rows, $"Commits ahead of main ({commits.Length})", "commits", content =>
        {
            if (commits.Length == 0) content.Children.Add(Notice(snapshot.IsStale ? "Branch commits unavailable until Git references can be read." : "No commits unique to this branch."));
            foreach (var commit in commits)
            {
                var key = "commit:" + commit.Sha;
                var caption = $"{commit.Sha[..Math.Min(8, commit.Sha.Length)]} {commit.Subject} — {commit.Author} — {commit.Timestamp:yyyy-MM-dd HH:mm} — {commit.Summary}" +
                    (commit.Parents.Count > 1 ? " — Merge (compared with first parent)" : string.Empty);
                var commitSection = AddSection(content, caption, key, filesPanel =>
                {
                    if (commitFiles.TryGetValue(commit.Sha, out var files)) AddCommitFiles(filesPanel, commit.Sha, files);
                    else
                    {
                        filesPanel.Children.Add(Notice("Loading files…"));
                        if (loadingCommits.Add(commit.Sha)) _ = LoadCommitFilesAsync(commit.Sha);
                    }
                });
                AutomationProperties.SetName(commitSection, "Commit " + caption);
            }
            if (!moreAvailable && commits.Length >= 100 * MaximumHistoryPages)
                content.Children.Add(Notice("Only the first 400 commits are shown; more history may exist."));
            if (moreAvailable && commits.Length > 0 && commits.Length % 100 == 0 && commits.Length < 100 * MaximumHistoryPages)
            {
                var more = new Button { Content = "Load more commits", Margin = new Thickness(12, 3, 0, 3), IsEnabled = !loadingHistoryPage };
                loadMoreButton = more;
                more.Click += async (_, _) => await LoadMoreAsync();
                content.Children.Add(more);
            }
        });
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || epoch != renderEpoch || !string.Equals(worktree, renderedWorktree, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(coordinator.Snapshot, renderedSnapshot)) return;
            scroller.ScrollToVerticalOffset(offset);
            if (focusedKey is not null && fileButtons.TryGetValue(focusedKey, out var button)) button.Focus();
            else if (focusedSection is not null && sections.TryGetValue(focusedSection, out var section))
            {
                section.ApplyTemplate();
                if (section.Template.FindName("Disclosure", section) is ToggleButton disclosure) disclosure.Focus();
                else section.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }, DispatcherPriority.Loaded);
    }

    private static string? FocusedSectionKey(DependencyObject? focused)
    {
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Expander section) return section.Tag as string;
        return null;
    }

    private void AddGroup(Panel parent, string label, ReviewFile[] files, string key)
    {
        AddSection(parent, $"{label} ({files.Length})", key, content =>
        {
            if (files.Length == 0) content.Children.Add(Notice($"No {label.ToLowerInvariant()} files."));
            AddFiles(content, files);
        });
    }

    private Expander AddSection(Panel target, string title, string key, Action<StackPanel> renderChildren)
    {
        var content = new StackPanel();
        object header = title;
        if (key.StartsWith("commit:", StringComparison.Ordinal))
        {
            var graph = new StackPanel { Orientation = Orientation.Horizontal };
            graph.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), Fill = new SolidColorBrush(Color.FromRgb(96, 165, 250)) });
            graph.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White });
            header = graph;
        }
        var section = new Expander { Header = header, Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White, Tag = key, IsExpanded = expanded.Contains(key), Content = content, Margin = new Thickness(key.StartsWith("commit:") ? 10 : 0, 3, 0, 2), ToolTip = title };
        if (TryFindResource("CompactReviewExpanderStyle") is Style compactStyle) section.Style = compactStyle;
        AutomationProperties.SetName(section, title);
        section.Expanded += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, section)) return;
            if (key.StartsWith("commit:", StringComparison.Ordinal))
            {
                foreach (var oldKey in expanded.Where(value => value.StartsWith("commit:", StringComparison.Ordinal) && value != key).ToArray())
                {
                    expanded.Remove(oldKey);
                    commitFiles.Remove(oldKey[7..]);
                    commitFileOffsets.Remove(oldKey[7..]);
                    if (sections.TryGetValue(oldKey, out var oldSection)) oldSection.IsExpanded = false;
                }
            }
            expanded.Add(key);
            content.Children.Clear(); renderChildren(content);
        };
        section.Collapsed += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, section)) return;
            expanded.Remove(key);
            if (key.StartsWith("commit:", StringComparison.Ordinal))
            {
                commitFiles.Remove(key[7..]);
                commitFileOffsets.Remove(key[7..]);
            }
            content.Children.Clear();
        };
        target.Children.Add(section);
        sections[key] = section;
        if (section.IsExpanded) renderChildren(content);
        return section;
    }

    private void AddCommitFiles(Panel target, string sha, IReadOnlyList<ReviewFile> files)
    {
        var offset = commitFileOffsets.GetValueOrDefault(sha);
        if (offset >= files.Count) offset = 0;
        AddFiles(target, files.Skip(offset).Take(WorkingPageSize));
        if (files.Count <= WorkingPageSize) return;
        target.Children.Add(Notice($"Showing commit files {offset + 1}–{Math.Min(offset + WorkingPageSize, files.Count)} of {files.Count}; not all rows are visible."));
        if (offset > 0)
        {
            var previous = new Button { Content = "Previous commit files" };
            AutomationProperties.SetName(previous, "Previous commit files");
            previous.Click += (_, _) => { commitFileOffsets[sha] = offset - WorkingPageSize; Render(coordinator.Snapshot); };
            target.Children.Add(previous);
        }
        if (offset + WorkingPageSize < files.Count)
        {
            var next = new Button { Content = "Next commit files" };
            AutomationProperties.SetName(next, "Next commit files");
            next.Click += (_, _) => { commitFileOffsets[sha] = offset + WorkingPageSize; Render(coordinator.Snapshot); };
            target.Children.Add(next);
        }
    }

    private void AddFiles(Panel target, IEnumerable<ReviewFile> files)
    {
        foreach (var file in files)
        {
                var label = $"{file.Status}: {(file.OldPath is null ? file.Path : file.OldPath + " → " + file.Path)}";
            var row = new Grid { Margin = new Thickness(12, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rail = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Background = file.Group == ReviewGroup.Commit ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(156, 163, 175)), IsHitTestVisible = false };
            row.Children.Add(rail);
            var button = new Button { Content = label, Tag = file.Identity, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(3), ToolTip = label };
            AutomationProperties.SetName(button, $"Read-only diff, {label}, {file.Group}");
            button.Click += (_, _) => { activeFileIdentity = file.Identity; DiffRequested?.Invoke(file, button); };
            Grid.SetColumn(button, 1); row.Children.Add(button);
            target.Children.Add(row);
            fileButtons[file.Identity] = button;
        }
    }

    private void UpdateRefreshIndicator()
    {
        var busy = IsRefreshing;
        refreshIcon.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        refreshIcon.Stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White;
        AutomationProperties.SetHelpText(worktreeNameText, busy ? "Refreshing changes" : string.Empty);
        var animate = busy && refreshIcon.IsLoaded && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        if (refreshAnimationActive == animate) return;
        ((RotateTransform)refreshIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            animate ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        refreshAnimationActive = animate;
    }

    private void StopRefreshAnimation()
    {
        if (!refreshAnimationActive) return;
        ((RotateTransform)refreshIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        refreshAnimationActive = false;
    }

    private void SetStatus(string message)
    {
        status.Text = message;
        status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Notice(string message) => new() { Text = message, Margin = new Thickness(10, 3, 0, 3), TextWrapping = TextWrapping.Wrap, Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White };

    private async Task ValidateSelectedCommitAsync(string identity, string requestedHead, string requestedMainHead)
    {
        var current = generation;
        var path = worktree;
        var sha = identity.Split(':', 3)[1];
        lazyCancellation ??= new CancellationTokenSource();
        try
        {
            var valid = await service.IsCommitUniqueToHeadAsync(path!, sha, requestedHead, requestedMainHead, lazyCancellation.Token);
            if (current != generation || disposed || activeFileIdentity != identity || coordinator.Snapshot?.IsStale is true) return;
            if (!valid) { activeFileIdentity = null; SelectionInvalidated?.Invoke(); }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadCommitFilesAsync(string sha)
    {
        var path = worktree;
        var current = generation;
        var requestedHead = head;
        lazyCancellation ??= new CancellationTokenSource();
        try
        {
            var files = await service.GetCommitFilesAsync(path!, sha, lazyCancellation.Token);
            if (current != generation || disposed || requestedHead != head || !history.Any(commit => commit.Sha == sha) || !expanded.Contains("commit:" + sha)) return;
            commitFiles[sha] = files;
            Render(coordinator.Snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (current == generation) SetStatus("Commit files unavailable: " + error.Message); }
        finally { if (current == generation) loadingCommits.Remove(sha); }
    }

    private async Task LoadMoreAsync()
    {
        if (loadingHistoryPage || disposed) return;
        var path = worktree;
        var current = generation;
        var offset = history.Count;
        var requestedHead = head;
        var requestedMainHead = mainHead;
        if (requestedHead is null || requestedMainHead is null) return;
        loadingHistoryPage = true;
        if (loadMoreButton is not null) loadMoreButton.IsEnabled = false;
        lazyCancellation ??= new CancellationTokenSource();
        try
        {
            var page = await service.GetCommitsAsync(path!, requestedHead, requestedMainHead, offset, lazyCancellation.Token);
            if (current != generation || disposed || head != requestedHead || mainHead != requestedMainHead || offset != history.Count) return;
            history.AddRange(page);
            moreAvailable = page.Count == 100 && history.Count < 100 * MaximumHistoryPages;
            Render(coordinator.Snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (current == generation) SetStatus("Commit page unavailable: " + error.Message); }
        finally
        {
            if (current == generation)
            {
                loadingHistoryPage = false;
                if (loadMoreButton is not null) loadMoreButton.IsEnabled = true;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        refreshRunning = refreshPending = false;
        loadingHistoryPage = false; loadMoreButton = null;
        UpdateRefreshIndicator();
        ++generation;
        ++refreshEpoch; invalidation.Reset();
        SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
        debounce.Stop(); poll.Stop(); foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); coordinator.Dispose();
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose();
    }
}
