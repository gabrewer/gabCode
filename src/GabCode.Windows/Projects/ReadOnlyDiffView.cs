using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GabCode.Windows.Projects;

internal sealed class ReadOnlyDiffView : UserControl
{
    private readonly Grid root;
    private readonly TextBlock heading;
    private readonly TextBlock status;
    private readonly TextBox patch;
    private readonly Button closeButton;
    private readonly Button openButton;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? OpenCurrentFileRequested;

    internal ReadOnlyDiffView(string label, string? currentFile)
    {
        root = new Grid { Background = new SolidColorBrush(Color.FromRgb(25, 26, 31)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var actions = new DockPanel { Margin = new Thickness(12, 10, 12, 6) };
        heading = new TextBlock { Text = label, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(heading, label);
        closeButton = new Button { Content = "Close Diff", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(closeButton, "Close diff");
        closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        openButton = new Button { Content = "Open in VS Code", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), Visibility = currentFile is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetName(openButton, "Open current worktree file in VS Code");
        openButton.Click += (_, _) => OpenCurrentFileRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(closeButton, Dock.Right); actions.Children.Add(closeButton);
        DockPanel.SetDock(openButton, Dock.Right); actions.Children.Add(openButton);
        actions.Children.Add(heading);
        root.Children.Add(actions);
        status = new TextBlock { Text = "Loading diff…", Margin = new Thickness(12, 0, 12, 5), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "Diff loading");
        Grid.SetRow(status, 1); root.Children.Add(status);
        patch = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(20, 20, 22)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(12, 0, 12, 12) };
        AutomationProperties.SetName(patch, $"Read-only unified diff for {label}");
        Grid.SetRow(patch, 2); root.Children.Add(patch);
        Content = root;
        ApplySystemColors();
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast)) ApplySystemColors();
    }

    private void ApplySystemColors()
    {
        var contrast = SystemParameters.HighContrast;
        root.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(25, 26, 31));
        heading.Foreground = status.Foreground = patch.Foreground = contrast ? SystemColors.WindowTextBrush : Brushes.White;
        patch.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(20, 20, 22));
        if (contrast)
        {
            var systemButton = new Style(typeof(Button));
            systemButton.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.ControlTextBrush));
            systemButton.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.ControlBrush));
            systemButton.Setters.Add(new Setter(Control.BorderBrushProperty, SystemColors.ControlTextBrush));
            closeButton.Style = openButton.Style = systemButton;
        }
        else closeButton.Style = openButton.Style = null;
    }

    internal void Show(ReviewDiff result)
    {
        status.Text = result.State switch
        {
            ReviewDiffState.Patch => "Read-only unified diff",
            ReviewDiffState.Conflict => "Unresolved conflict — normal two-way diff unavailable",
            ReviewDiffState.Binary => "Binary content — text diff unavailable",
            ReviewDiffState.Oversized => "Diff exceeds size limit",
            ReviewDiffState.Truncated => "Diff output exceeded limit",
            ReviewDiffState.Timeout => "Git diff timed out",
            _ => "Diff unavailable: " + (result.Error ?? "Git could not retrieve the diff")
        };
        AutomationProperties.SetName(status, status.Text);
        var nextPatch = result.State == ReviewDiffState.Patch ? result.Patch ?? string.Empty : string.Empty;
        if (patch.Text != nextPatch)
        {
            var position = patch.VerticalOffset;
            patch.Text = nextPatch;
            patch.ScrollToVerticalOffset(position);
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
