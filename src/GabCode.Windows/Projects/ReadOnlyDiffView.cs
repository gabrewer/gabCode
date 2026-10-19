using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using Path = System.IO.Path;

namespace GabCode.Windows.Projects;

/// <summary>Native review actions; the embedded viewer owns only read-only code presentation.</summary>
internal sealed class ReadOnlyDiffView : UserControl
{
    private readonly Grid root;
    private readonly TextBlock heading;
    private readonly TextBlock status;
    private readonly TextBlock changePosition;
    private readonly Button closeButton;
    private readonly Button openButton;
    private readonly Button retryButton;
    private readonly Button previousButton;
    private readonly Button nextButton;
    private readonly Button findButton;
    private readonly Button deletionButton;
    private readonly Dictionary<string, ToggleButton> modeButtons = new(StringComparer.Ordinal);
    private readonly List<ButtonBase> toolbarButtons = [];
    private readonly DiffModePreference preference;
    private WindowsDiffViewerHost? viewer;
    private Task? initialization;
    private long generation;
    private string identity = string.Empty;
    private string documentStatus = string.Empty;
    private string activeMode = "side-by-side";
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
        heading = new TextBlock
        {
            Text = label, ToolTip = label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 8, 12, 3)
        };
        AutomationProperties.SetName(heading, label);
        root.Children.Add(heading);

        var toolbar = new Grid { Margin = new Thickness(10, 0, 10, 5) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(toolbar, "Read-only review controls. F6 focuses code; Escape returns to controls.");
        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(controls);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        AddMode(controls, "Side by side", "side-by-side", "M3,3 H17 V17 H3 Z M10,3 V17");
        AddMode(controls, "Inline", "inline", "M3,3 H17 V17 H3 Z M5,8 H15 M5,12 H15");
        AddMode(controls, "File", "file", "M5,2 H13 L17,6 V18 H5 Z M13,2 V6 H17");
        previousButton = AddButton(controls, "Previous review change", "Previous change", "M4,13 L10,7 L16,13", () => _ = ControlAsync("change", "previous"));
        nextButton = AddButton(controls, "Next review change", "Next change", "M4,7 L10,13 L16,7", () => _ = ControlAsync("change", "next"));
        changePosition = new TextBlock
        {
            Text = "Computing changes…", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 2), MinWidth = 54
        };
        AutomationProperties.SetLiveSetting(changePosition, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(changePosition, "Computing review changes");
        controls.Children.Add(changePosition);
        deletionButton = AddButton(controls, "View deletion in Inline", "View deletion in Inline", "M4,4 H16 M4,9 H12 M4,14 H16 M12,9 L16,13 M16,13 L18,10", () => _ = ControlAsync("deletion"));
        deletionButton.Visibility = Visibility.Collapsed;
        retryButton = AddButton(controls, "Retry selected review document", "Retry review", "M16,7 A7,7 0 1 0 17,11 M16,3 V7 H12", () => RetryRequested?.Invoke(this, EventArgs.Empty));
        retryButton.Visibility = Visibility.Collapsed;
        findButton = AddButton(actions, "Find in read-only review document", "Find (F6 focuses code)", "M13.5,8 A5.5,5.5 0 1 1 2.5,8 A5.5,5.5 0 1 1 13.5,8 M12,12 L18,18", () => _ = ControlAsync("find"));
        openButton = AddButton(actions, "Open current worktree file in VS Code (not historical snapshot)", "Open in VS Code", "M9,4 H17 V12 M17,4 L8,13 M14,11 V17 H3 V6 H9", () => OpenCurrentFileRequested?.Invoke(this, EventArgs.Empty));
        openButton.Visibility = currentFile is null ? Visibility.Collapsed : Visibility.Visible;
        closeButton = AddButton(actions, "Close diff and restore invoking control", "Close diff", "M4,4 L16,16 M16,4 L4,16", () => CloseRequested?.Invoke(this, EventArgs.Empty));
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

        status = new TextBlock { Text = "Loading diff…", Margin = new Thickness(12, 0, 12, 4), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "Diff loading");
        Grid.SetRow(status, 2); root.Children.Add(status);
        Content = root;
        ApplySystemColors();
        UpdateModes();
        SetControlsAvailable(false);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.F6 || viewer is null) return;
            args.Handled = true;
            if (viewer.IsKeyboardFocusWithin) closeButton.Focus();
            else _ = ControlAsync("focus");
        };
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
    }

    private void AddMode(Panel target, string caption, string mode, string geometry)
    {
        var button = new ToggleButton
        {
            Content = Icon(geometry), ToolTip = caption, Margin = new Thickness(0, 0, 2, 2),
            IsThreeState = false
        };
        AutomationProperties.SetName(button, "Read-only diff mode: " + caption);
        button.Click += (_, _) => _ = ChangeModeAsync(mode);
        modeButtons.Add(mode, button);
        toolbarButtons.Add(button);
        target.Children.Add(button);
    }

    private Button AddButton(Panel target, string name, string tooltip, string geometry, Action invoke)
    {
        var button = new Button { Content = Icon(geometry), ToolTip = tooltip, Margin = new Thickness(0, 0, 4, 2) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => invoke();
        toolbarButtons.Add(button);
        target.Children.Add(button);
        return button;
    }

    private static ShapePath Icon(string geometry)
    {
        var icon = new ShapePath
        {
            Data = Geometry.Parse(geometry), Width = 16, Height = 16, Stretch = Stretch.Uniform,
            StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        icon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ButtonBase), 1) });
        return icon;
    }

    private static Style ToolbarStyle(Type type, bool contrast)
    {
        var style = new Style(type);
        var background = contrast ? SystemColors.ControlBrush : new SolidColorBrush(Color.FromRgb(44, 47, 57));
        var borderBrush = contrast ? SystemColors.ControlTextBrush : new SolidColorBrush(Color.FromRgb(78, 83, 99));
        var foreground = contrast ? SystemColors.ControlTextBrush : new SolidColorBrush(Color.FromRgb(231, 233, 240));
        var highlight = contrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(57, 89, 125));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(type) { VisualTree = chrome };
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, contrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(60, 66, 80)), "Chrome"),
                new Setter(ForegroundProperty, contrast ? SystemColors.HighlightTextBrush : foreground) } });
        template.Triggers.Add(new Trigger { Property = IsKeyboardFocusedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, contrast ? SystemColors.HighlightTextBrush : SystemColors.HighlightBrush, "Chrome"),
                new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome") } });
        if (type == typeof(ToggleButton))
            template.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true,
                Setters = { new Setter(Border.BackgroundProperty, highlight, "Chrome"),
                    new Setter(Border.BorderBrushProperty, contrast ? SystemColors.HighlightTextBrush : new SolidColorBrush(Color.FromRgb(103, 180, 239)), "Chrome"),
                    new Setter(ForegroundProperty, contrast ? SystemColors.HighlightTextBrush : foreground) } });
        template.Triggers.Add(new Trigger { Property = IsEnabledProperty, Value = false,
            Setters = { new Setter(Border.OpacityProperty, contrast ? 1.0 : 0.55, "Chrome"),
                new Setter(ForegroundProperty, contrast ? SystemColors.GrayTextBrush : foreground) } });
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(BackgroundProperty, background));
        style.Setters.Add(new Setter(BorderBrushProperty, borderBrush));
        style.Setters.Add(new Setter(ForegroundProperty, foreground));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(5, 3, 5, 3)));
        style.Setters.Add(new Setter(MinWidthProperty, 28.0));
        style.Setters.Add(new Setter(MinHeightProperty, 26.0));
        return style;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast)) ApplySystemColors();
    }

    private void ApplySystemColors()
    {
        var contrast = SystemParameters.HighContrast;
        root.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(25, 26, 31));
        heading.Foreground = status.Foreground = changePosition.Foreground = contrast ? SystemColors.WindowTextBrush : Brushes.White;
        var buttonStyle = ToolbarStyle(typeof(Button), contrast);
        var toggleStyle = ToolbarStyle(typeof(ToggleButton), contrast);
        foreach (var button in toolbarButtons) button.Style = button is ToggleButton ? toggleStyle : buttonStyle;
    }

    private void UpdateModes()
    {
        foreach (var (mode, button) in modeButtons)
        {
            var selected = activeMode == mode;
            button.IsChecked = selected;
            var caption = mode switch { "side-by-side" => "Side by side", "inline" => "Inline", _ => "File" };
            button.ToolTip = selected ? "Selected: " + caption : caption;
            AutomationProperties.SetHelpText(button, selected ? "Selected review mode" : "Switch to " + caption + " review mode");
        }
    }

    private void SetControlsAvailable(bool available)
    {
        foreach (var button in modeButtons.Values) button.IsEnabled = available;
        previousButton.IsEnabled = nextButton.IsEnabled = deletionButton.IsEnabled = false;
        findButton.IsEnabled = available;
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
            viewer.ProgressChanged += (_, progress) =>
            {
                if (ReferenceEquals(viewer, activeHost) && progress.Generation == generation && progress.Identity == identity)
                    ShowProgress(progress);
            };
            Grid.SetRow(viewer, 3);
            root.Children.Add(viewer);
            initialization = viewer.InitializeAsync();
        }
        viewer.Select(selectedGeneration, selectedIdentity);
        await initialization!.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (generation != selectedGeneration || identity != selectedIdentity) throw new OperationCanceledException("Review selection changed.");
        documentStatus = string.Join(" — ", new[]
        {
            document.RawFilter ? "Raw content — Git filters not applied" : "",
            document.BeforeBom != document.AfterBom ? "UTF-8 BOM differs" : "",
            document.BeforeEol != document.AfterEol ? $"line endings {document.BeforeEol} → {document.AfterEol}" : "",
            document.BeforeFinalNewline != document.AfterFinalNewline ? "final newline differs" : "",
            !document.AfterExists ? "Deleted file: selected result is absent, not an empty file; Inline shows removed lines" :
                !document.BeforeExists ? "New file: before version is absent" : ""
        }.Where(text => text.Length > 0));
        activeMode = preference.Read();
        UpdateModes();
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
            retryButton.Visibility = Visibility.Collapsed;
            SetStatus(documentStatus);
            SetControlsAvailable(true);
        }
    }

    private void ShowProgress(WindowsDiffViewerHost.ViewerProgress progress)
    {
        activeMode = progress.Mode;
        UpdateModes();
        changePosition.Text = progress.Total == 0 ? progress.Description : $"{progress.Index} of {progress.Total}";
        changePosition.ToolTip = progress.Description;
        AutomationProperties.SetName(changePosition, progress.Total == 0 ? progress.Description :
            $"Change {progress.Index} of {progress.Total}: {progress.Description}");
        previousButton.IsEnabled = nextButton.IsEnabled = progress.Total > 0;
        deletionButton.Visibility = progress.Deletion ? Visibility.Visible : Visibility.Collapsed;
        deletionButton.IsEnabled = progress.Deletion;
    }

    private async Task ChangeModeAsync(string mode)
    {
        if (viewer is null || identity.Length == 0) return;
        try
        {
            if (!await ControlAsync("mode", mode)) { UpdateModes(); return; }
            activeMode = mode;
            UpdateModes();
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
        status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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
        var warning = "Diff unavailable — previous review snapshot is stale: " + reason;
        if (status.Text != warning) SetStatus(warning);
        retryButton.Visibility = Visibility.Visible;
    }

    internal void ShowUnavailable(string reason)
    {
        SetStatus(reason.StartsWith("Diff unavailable: ", StringComparison.Ordinal) ? reason : "Diff unavailable: " + reason);
        retryButton.Visibility = Visibility.Visible;
        deletionButton.Visibility = Visibility.Collapsed;
        changePosition.Text = "Unavailable";
        SetControlsAvailable(false);
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
        for (var segment = Path.GetDirectoryName(path); segment is not null && segment.StartsWith(root, StringComparison.OrdinalIgnoreCase); segment = Path.GetDirectoryName(segment))
            if ((File.GetAttributes(segment) & FileAttributes.ReparsePoint) != 0) return null;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
    }
}
