using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceBranchDialog : Window
{
    private readonly ComboBox branchPicker;

    internal WorkspaceBranchDialog(IReadOnlyList<string> branches)
    {
        if (branches.Count == 0) throw new ArgumentException("At least one branch is required.", nameof(branches));
        Title = "Select Branch";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        branchPicker = new ComboBox { ItemsSource = branches, SelectedItem = branches.FirstOrDefault(branch => branch == "main") ?? branches[0], MinWidth = 300, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetName(branchPicker, "Workspace branch");
        var select = new Button { Content = "Select", IsDefault = true, MinWidth = 80 };
        select.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel { Margin = new Thickness(20), Children = { new TextBlock { Text = "Branch/worktree" }, branchPicker, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { select, cancel } } } };
        Loaded += (_, _) => branchPicker.Focus();
    }

    internal string Branch => (string)branchPicker.SelectedItem;
}
