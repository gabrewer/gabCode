using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class WorktreeDeletionDialog : Window
{
    private readonly CheckBox deleteBranch;

    internal WorktreeDeletionDialog(WorktreeNavigationEntry entry, int activeTerminals)
    {
        Title = "Delete Worktree";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var warning = activeTerminals == 0 ? string.Empty : $"\n{activeTerminals} active gabCode terminal process(es) must be stopped before removal.";
        deleteBranch = new CheckBox { Content = "Also delete the local branch after successful worktree removal", Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(deleteBranch, "Also delete the local branch");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var remove = new Button { Content = "Delete worktree", IsDefault = true, MinWidth = 120 };
        remove.Click += (_, _) => DialogResult = true;
        Content = new StackPanel { Margin = new Thickness(20), Children =
        {
            new TextBlock { Text = $"Delete {entry.Branch}?", FontWeight = FontWeights.SemiBold, FontSize = 18 },
            new TextBlock { Text = $"Git will safely remove this worktree:\n{entry.Path}{warning}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) },
            deleteBranch,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { cancel, remove } },
        }};
    }

    internal bool DeleteLocalBranch => deleteBranch.IsChecked is true;
}
