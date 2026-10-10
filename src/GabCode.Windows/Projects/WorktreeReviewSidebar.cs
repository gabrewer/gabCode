using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly DockPanel root;
    private readonly TextBlock titleText;
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroller;
    private readonly Dictionary<string, Button> fileButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> sections = new(StringComparer.Ordinal);
    private readonly TextBlock status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, IReadOnlyList<ReviewFile>> commitFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> loadingCommits = new(StringComparer.Ordinal);
    private readonly List<ReviewCommit> history = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private string? watcherWarning;
    private CancellationTokenSource? lazyCancellation;
    private string? worktree;
    private string? mainBranch;
    private string? head;
    private string? mainHead;
    private string? activeFileIdentity;
    private bool moreAvailable = true;
    private long generation;
    private long renderEpoch;
    private bool disposed;
    internal int WatcherCount => watchers.Count;
    internal bool IsDisposed => disposed;
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
        debounce.Tick += (_, _) => { debounce.Stop(); _ = coordinator.RefreshAsync(); };
        poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        poll.Tick += (_, _) => { status.Text = "Refreshing changes…"; _ = coordinator.RefreshAsync(); };
        root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(29, 30, 36)) };
        var title = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        var button = new Button { Content = "Refresh changes", Padding = new Thickness(5, 2, 5, 2) };
        AutomationProperties.SetName(button, "Refresh selected worktree changes and commits");
        button.Click += (_, _) => { if (worktree is not null) status.Text = "Refreshing changes…"; _ = coordinator.RefreshAsync(); };
        DockPanel.SetDock(button, Dock.Right); title.Children.Add(button);
        titleText = new TextBlock { Text = "Changes and commits", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(titleText);
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
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
        }
    }

    private void ApplySystemColors()
    {
        var contrast = SystemParameters.HighContrast;
        root.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(29, 30, 36));
        titleText.Foreground = status.Foreground = contrast ? SystemColors.WindowTextBrush : Brushes.White;
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
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose(); lazyCancellation = null;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear(); watcherWarning = null;
        expanded.Clear(); expanded.UnionWith(["working", "staged", "unstaged"]);
        commitFiles.Clear(); loadingCommits.Clear(); history.Clear(); head = null; mainHead = null; moreAvailable = true;
        var hadSelection = activeFileIdentity is not null;
        activeFileIdentity = null;
        worktree = path; mainBranch = branch;
        if (hadSelection) SelectionInvalidated?.Invoke();
        coordinator.Select(path, branch);
        status.Text = "Loading changes…";
        rows.Children.Clear();
        StartWatchers(path);
        poll.Start();
        _ = coordinator.RefreshAsync();
    }

    internal void Suspend()
    {
        if (disposed) return;
        ++generation;
        debounce.Stop(); poll.Stop();
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose(); lazyCancellation = null;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        var hadSelection = activeFileIdentity is not null;
        activeFileIdentity = null;
        worktree = null; mainBranch = null;
        coordinator.Select(null, null);
        rows.Children.Clear(); fileButtons.Clear(); sections.Clear();
        status.Text = "Review unavailable for orphaned terminals. Select an available worktree.";
        if (hadSelection) SelectionInvalidated?.Invoke();
    }

    private void StartWatchers(string path)
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
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
            foreach (var directory in new[] { path, gitDir, commonDir }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += OnFileChanged; watcher.Created += OnFileChanged; watcher.Deleted += OnFileChanged; watcher.Renamed += OnFileRenamed;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (string.Equals(worktree, path, StringComparison.OrdinalIgnoreCase)) RecoverWatcherFailure();
                });
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }
        catch (Exception error) { watcherWarning = "File watcher unavailable; periodic and manual refresh remain active: " + error.Message; }
    }

    internal void RecoverWatcherFailure()
    {
        if (disposed || worktree is null) return;
        watcherWarning = "File watcher overflow or failure; recovering with polling and retry.";
        coordinator.MarkStale(watcherWarning);
        StartWatchers(worktree);
        _ = coordinator.RefreshAsync();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(RequestRefresh);
    private void OnFileRenamed(object sender, RenamedEventArgs e) => Dispatcher.BeginInvoke(RequestRefresh);

    internal void RequestRefresh()
    {
        if (disposed || worktree is null) return;
        status.Text = "Refreshing changes…";
        debounce.Stop(); debounce.Start();
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
            history.Clear(); commitFiles.Clear(); moreAvailable = true;
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
        status.Text = snapshot.IsStale ? $"Git review unavailable; showing last known state. {snapshot.Error}" :
            $"Selected worktree: {Path.GetFileName(worktree.TrimEnd(Path.DirectorySeparatorChar))}" +
            (watcherWarning is null ? string.Empty : ". " + watcherWarning);
        rows.Children.Clear(); fileButtons.Clear(); sections.Clear();
        if (snapshot.IsStale && snapshot.Head is null) { rows.Children.Add(Notice("No current review data. Retry refresh.")); return; }
        AddSection(rows, "Working changes", "working", content =>
        {
            if (snapshot.Working.Count == 0) content.Children.Add(Notice("Working tree clean"));
            AddGroup(content, "Staged", snapshot.Working.Where(f => f.Group == ReviewGroup.Staged).ToArray(), "staged");
            AddGroup(content, "Unstaged", snapshot.Working.Where(f => f.Group == ReviewGroup.Unstaged).ToArray(), "unstaged");
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
                    if (commitFiles.TryGetValue(commit.Sha, out var files)) AddFiles(filesPanel, files);
                    else
                    {
                        filesPanel.Children.Add(Notice("Loading files…"));
                        if (loadingCommits.Add(commit.Sha)) _ = LoadCommitFilesAsync(commit.Sha);
                    }
                });
                AutomationProperties.SetName(commitSection, "Commit " + caption);
            }
            if (moreAvailable && commits.Length > 0 && commits.Length % 100 == 0)
            {
                var more = new Button { Content = "Load more commits", Margin = new Thickness(12, 3, 0, 3) };
                more.Click += async (_, _) => await LoadMoreAsync();
                content.Children.Add(more);
            }
        });
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || epoch != renderEpoch || !string.Equals(worktree, renderedWorktree, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(coordinator.Snapshot, renderedSnapshot)) return;
            scroller.ScrollToVerticalOffset(offset);
            if (focusedKey is not null && fileButtons.TryGetValue(focusedKey, out var button)) button.Focus();
            else if (focusedSection is not null && sections.TryGetValue(focusedSection, out var section)) section.Focus();
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
        AutomationProperties.SetName(section, title);
        section.Expanded += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, section)) return;
            expanded.Add(key);
            content.Children.Clear(); renderChildren(content);
        };
        section.Collapsed += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, section)) return;
            expanded.Remove(key);
            content.Children.Clear();
        };
        target.Children.Add(section);
        sections[key] = section;
        if (section.IsExpanded) renderChildren(content);
        return section;
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
        catch (Exception error) { if (current == generation) { status.Text = "Commit files unavailable: " + error.Message; } }
        finally { if (current == generation) loadingCommits.Remove(sha); }
    }

    private async Task LoadMoreAsync()
    {
        var path = worktree;
        var current = generation;
        var offset = history.Count;
        var requestedHead = head;
        var requestedMainHead = mainHead;
        if (requestedHead is null || requestedMainHead is null) return;
        lazyCancellation ??= new CancellationTokenSource();
        try
        {
            var page = await service.GetCommitsAsync(path!, requestedHead, requestedMainHead, offset, lazyCancellation.Token);
            if (current != generation || disposed || head != requestedHead || mainHead != requestedMainHead || offset != history.Count) return;
            history.AddRange(page);
            moreAvailable = page.Count == 100;
            Render(coordinator.Snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (current == generation) status.Text = "Commit page unavailable: " + error.Message; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ++generation;
        SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
        debounce.Stop(); poll.Stop(); foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); coordinator.Dispose();
        lazyCancellation?.Cancel(); lazyCancellation?.Dispose();
    }
}
