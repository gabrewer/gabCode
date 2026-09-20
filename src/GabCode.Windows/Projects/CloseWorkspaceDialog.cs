using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class CloseWorkspaceDialog : Window
{
    internal CloseWorkspaceDialog(WorktreeNavigationEntry entry, int activeTerminals)
    {
        Title = "Close Workspace";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, $"Confirm closing workspace {entry.FolderName}");

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close Workspace", IsDefault = true, MinWidth = 140 };
        close.Click += (_, _) => DialogResult = true;

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = $"Close workspace '{entry.FolderName}'?", FontWeight = FontWeights.SemiBold, FontSize = 18 },
                new TextBlock
                {
                    Text = $"Branch: {entry.Branch}\nPath: {entry.Path}\n\nThe Git worktree, branch, and files will remain." +
                        (activeTerminals == 0
                            ? "\n\nNo active gabCode terminals will be stopped."
                            : $"\n\n{activeTerminals} active gabCode terminal process(es) will be stopped. Running shell work will be interrupted."),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0),
                    Children = { cancel, close },
                },
            },
        };
    }
}
