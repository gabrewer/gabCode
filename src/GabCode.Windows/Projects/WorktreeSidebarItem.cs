using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GabCode.Windows.Projects;

internal sealed class WorktreeSidebarItem : Border
{
    private readonly WorktreeNavigationEntry entry;
    private static readonly Geometry BoltCircleGeometry = Geometry.Parse(
        "M9,0.75 A8.25,8.25 0 1 1 9,17.25 A8.25,8.25 0 1 1 9,0.75 M10.5,3.75 L6.75,9.25 L9.5,9.25 L7.75,14.25 L12.25,8.25 L9.5,8.25 Z");

    private WorktreeSidebarItem(WorktreeNavigationEntry entry, bool isSelected, bool hasRunningTerminals)
    {
        this.entry = entry;
        RunningIcon = CreateIcon(BoltCircleGeometry, hasRunningTerminals);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = entry.FolderName,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.FolderName,
        });
        labels.Children.Add(new TextBlock
        {
            Text = entry.Branch + (entry.Availability == WorktreeAvailability.Unavailable ? " — unavailable" : string.Empty),
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Branch,
        });

        var content = new Grid { Background = Brushes.Black };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(RunningIcon, 0);
        Grid.SetColumn(labels, 1);
        content.Children.Add(RunningIcon);
        content.Children.Add(labels);

        Padding = new Thickness(4, 3, 4, 3);
        Background = Brushes.Black;
        Child = content;
        UpdateState(isSelected, hasRunningTerminals);
    }

    internal Path RunningIcon { get; }

    internal static WorktreeSidebarItem Create(WorktreeNavigationEntry entry, bool selected, bool hasRunningTerminals) =>
        new(entry, selected, hasRunningTerminals);

    internal void UpdateState(bool selected, bool hasRunningTerminals)
    {
        RunningIcon.Visibility = hasRunningTerminals ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, BuildAccessibleName(entry, selected, hasRunningTerminals));
    }

    private static Path CreateIcon(Geometry geometry, bool visible) => new()
    {
        Data = geometry,
        Width = 18,
        Height = 18,
        Stretch = Stretch.Uniform,
        Stroke = Brushes.White,
        StrokeThickness = 1.4,
        Fill = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
    };

    private static string BuildAccessibleName(WorktreeNavigationEntry entry, bool isSelected, bool hasRunningTerminals)
    {
        var state = new List<string>();
        if (isSelected) state.Add("selected");
        if (hasRunningTerminals) state.Add("running terminals");
        if (entry.Availability == WorktreeAvailability.Unavailable) state.Add("unavailable");
        return string.Join(", ", new[] { entry.FolderName, entry.Branch }.Concat(state));
    }
}
