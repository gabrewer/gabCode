using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GabCode.Windows.Projects;

/// <summary>Native controls and focus for the immutable bundled review document.</summary>
internal sealed class ReadOnlyDiffView : UserControl
{
    private readonly Grid root;
    private readonly TextBlock heading;
    private readonly TextBlock status;
    private readonly Button closeButton;
    private readonly Button openButton;
    private readonly Button retryButton;
    private readonly DiffModePreference preference;
    private WindowsDiffViewerHost? viewer;
    private Task? initialization;
    private long generation;
    private string identity = string.Empty;
    private string documentStatus = string.Empty;
    private string activeMode = "side-by-side";
    private bool isStale;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? OpenCurrentFileRequested;
    internal event EventHandler? RetryRequested;

    internal ReadOnlyDiffView(string label, string? currentFile, DiffModePreference? preference = null)
    {
        this.preference = preference ?? new DiffModePreference();
        root = new Grid { Background = new SolidColorBrush(Color.FromRgb(25, 26, 31)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var actions = new DockPanel { Margin = new Thickness(12, 10, 12, 6) };
        heading = new TextBlock { Text = label, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(heading, label);
        closeButton = new Button { Content = "Close Diff", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(closeButton, "Close diff and restore invoking control");
        closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        openButton = new Button { Content = "Open in VS Code", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), Visibility = currentFile is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetName(openButton, "Open current worktree file in VS Code (not historical snapshot)");
        openButton.Click += (_, _) => OpenCurrentFileRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(closeButton, Dock.Right); actions.Children.Add(closeButton);
        DockPanel.SetDock(openButton, Dock.Right); actions.Children.Add(openButton);
        actions.Children.Add(heading);
        root.Children.Add(actions);

        var controls = new WrapPanel { Margin = new Thickness(12, 0, 12, 5), Orientation = Orientation.Horizontal };
        foreach (var (caption, mode) in new[] { ("Side by side", "side-by-side"), ("Inline", "inline"), ("File", "file") })
        {
            var button = Action(caption, $"Read-only diff mode: {caption}", () => _ = ChangeModeAsync(mode));
            controls.Children.Add(button);
        }
        controls.Children.Add(Action("Previous change", "Previous review change", () => _ = ControlAsync("change", "previous")));
        controls.Children.Add(Action("Next change", "Next review change", () => _ = ControlAsync("change", "next")));
        controls.Children.Add(Action("Find", "Find in read-only review document", () => _ = ControlAsync("find")));
        controls.Children.Add(Action("Focus code", "Focus read-only code comparison", () => _ = ControlAsync("focus")));
        retryButton = Action("Retry", "Retry selected review document", () => RetryRequested?.Invoke(this, EventArgs.Empty));
        controls.Children.Add(retryButton);
        Grid.SetRow(controls, 1); root.Children.Add(controls);
        status = new TextBlock { Text = "Loading diff…", Margin = new Thickness(12, 0, 12, 5), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "Diff loading");
        Grid.SetRow(status, 2); root.Children.Add(status);
        Content = root;
        ApplySystemColors();
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
    }

    private static Button Action(string caption, string name, Action invoke)
    {
        var button = new Button { Content = caption, Margin = new Thickness(0, 0, 5, 2), Padding = new Thickness(6, 3, 6, 3) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => invoke();
        return button;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast)) ApplySystemColors();
    }

    private void ApplySystemColors()
    {
        var contrast = SystemParameters.HighContrast;
        root.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(25, 26, 31));
        heading.Foreground = status.Foreground = contrast ? SystemColors.WindowTextBrush : Brushes.White;
        foreach (var button in new[] { closeButton, openButton, retryButton }.Concat(root.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<Button>())))
        {
            if (contrast)
            {
                var style = new Style(typeof(Button));
                style.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.ControlTextBrush));
                style.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.ControlBrush));
                style.Setters.Add(new Setter(Control.BorderBrushProperty, SystemColors.ControlTextBrush));
                button.Style = style;
            }
            else button.Style = null;
        }
    }

    internal async Task ShowDocumentAsync(ReviewDocument document, long selectedGeneration, string selectedIdentity, string path, CancellationToken token)
    {
        generation = selectedGeneration;
        identity = selectedIdentity;
        if (viewer is null)
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "NativeDiffViewer");
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gabCode", "DiffViewerProfile");
            viewer = new WindowsDiffViewerHost(assets, profile);
            var activeHost = viewer;
            viewer.Unavailable += (_, reason) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(viewer, activeHost)) ShowUnavailable(reason);
            });
            Grid.SetRow(viewer, 3);
            root.Children.Add(viewer);
            initialization = viewer.InitializeAsync();
        }
        viewer.Select(selectedGeneration, selectedIdentity);
        await initialization!.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (generation != selectedGeneration || identity != selectedIdentity) throw new OperationCanceledException("Review selection changed.");
        documentStatus = document.Comparison + " — read-only viewer" +
            (document.RawFilter ? " — Raw content — Git filters not applied" : "") +
            (document.BeforeBom != document.AfterBom ? " — UTF-8 BOM differs" : "") +
            (document.BeforeEol != document.AfterEol ? $" — line endings {document.BeforeEol} → {document.AfterEol}" : "") +
            (document.BeforeFinalNewline != document.AfterFinalNewline ? " — final newline differs" : "") +
            (!document.AfterExists ? " — Deleted file: selected result is absent, not an empty file; Inline shows removed lines" :
             !document.BeforeExists ? " — New file: before version is absent" : "");
        activeMode = preference.Read();
        MarkStale("Loading updated review snapshot.");
        var beforeLabel = document.BaseRevision is { } baseSha ? "base " + baseSha[..Math.Min(12, baseSha.Length)] :
            document.Comparison switch { "HEAD → index" => "HEAD", "index → worktree" => "index", "first parent → selected commit" => "first parent", "empty tree → selected commit" => "empty tree", _ => "empty" };
        var afterLabel = document.TargetRevision is { } tip ? "tip " + tip[..Math.Min(12, tip.Length)] :
            document.Comparison switch { "HEAD → index" => "index", "index → worktree" or "empty → worktree" => "worktree", "first parent → selected commit" => "selected commit", _ => "target" };
        viewer.Visibility = Visibility.Visible;
        await viewer.ShowAsync(new WindowsDiffViewerHost.Snapshot(selectedGeneration, selectedIdentity, path,
            document.Before, document.After, beforeLabel, afterLabel, activeMode,
            document.BeforeBom, document.AfterBom, document.BeforeEol, document.AfterEol,
            document.BeforeFinalNewline, document.AfterFinalNewline), token);
        token.ThrowIfCancellationRequested();
        if (generation == selectedGeneration && identity == selectedIdentity)
        {
            isStale = false;
            SetStatus(documentStatus + " — mode: " + activeMode);
        }
    }

    private async Task ChangeModeAsync(string mode)
    {
        if (viewer is null || identity.Length == 0) return;
        try
        {
            if (!await ControlAsync("mode", mode)) return;
            activeMode = mode;
            if (!isStale) SetStatus(documentStatus + " — mode: " + activeMode);
            preference.Write(mode);
        }
        catch (IOException error) { SetStatus("Mode is active but could not be saved: " + error.Message); }
        catch (UnauthorizedAccessException error) { SetStatus("Mode is active but could not be saved: " + error.Message); }
    }

    private async Task<bool> ControlAsync(string kind, string value = "")
    {
        if (viewer is null || identity.Length == 0) return false;
        try { await viewer.SendControlAsync(generation, identity, kind, value); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or ObjectDisposedException)
        { ShowUnavailable(error.Message); return false; }
    }

    private void SetStatus(string message)
    {
        status.Text = message;
        AutomationProperties.SetName(status, message);
    }

    internal void DisposeViewer()
    {
        viewer?.Dispose();
        viewer = null;
        initialization = null;
        identity = string.Empty;
    }

    internal bool HasStagedViewer => viewer is not null;
    internal Task<(string Before, string After)> ReadViewerPairForTestAsync() =>
        viewer?.ReadDocumentForTestAsync() ?? throw new InvalidOperationException("Viewer is not open.");

    internal void MarkStale(string reason)
    {
        isStale = true;
        var warning = "Previous review snapshot — stale: " + reason;
        if (status.Text != warning) SetStatus(warning);
    }

    internal void ShowUnavailable(string reason)
    {
        SetStatus(reason.StartsWith("Diff unavailable: ", StringComparison.Ordinal) ? reason : "Diff unavailable: " + reason);
        if (viewer is not null)
        {
            root.Children.Remove(viewer);
            DisposeViewer();
        }
    }

    internal void FocusDocument()
    {
        if (IsLoaded) { closeButton.Focus(); return; }
        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            Loaded -= onLoaded;
            Dispatcher.BeginInvoke(() => { if (IsVisible) closeButton.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        };
        Loaded += onLoaded;
    }

    internal static string? CurrentFile(string worktree, ReviewFile file)
    {
        try { return FindCurrentFile(worktree, file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? FindCurrentFile(string worktree, ReviewFile file)
    {
        if (file.Status == ReviewStatus.Deleted || Path.IsPathFullyQualified(file.Path)) return null;
        var root = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        // Do not offer external-open for symlinks or junctions escaping the selected worktree.
        for (var segment = Path.GetDirectoryName(path); segment is not null && segment.StartsWith(root, StringComparison.OrdinalIgnoreCase); segment = Path.GetDirectoryName(segment))
            if ((File.GetAttributes(segment) & FileAttributes.ReparsePoint) != 0) return null;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
    }
}
