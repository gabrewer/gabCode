using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class ExistingWorktreeBranchDialog : Window
{
    private readonly ComboBox branchPicker;
    private readonly IReadOnlyList<GitBranchReference> branches;
    private readonly Button selectButton;

    internal ExistingWorktreeBranchDialog(IReadOnlyList<GitBranchReference> branches)
    {
        if (branches.Count == 0) throw new ArgumentException("At least one branch is required.", nameof(branches));
        this.branches = branches;
        Title = "Select Existing Branch";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        branchPicker = new ComboBox
        {
            ItemsSource = branches.Select(Describe).ToArray(),
            SelectedIndex = 0,
            MinWidth = 420,
            Margin = new Thickness(0, 8, 0, 12),
        };
        AutomationProperties.SetName(branchPicker, "Existing local or remote branch");
        var refresh = new Button { Content = "Refresh remote branches", MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(refresh, "Refresh remote branches");
        refresh.Click += (_, _) => { RefreshRequested = true; DialogResult = false; };
        selectButton = new Button { Content = "Select", IsDefault = true, MinWidth = 80 };
        selectButton.Click += (_, _) => DialogResult = true;
        branchPicker.SelectionChanged += (_, _) => UpdateSelectionAvailability();
        UpdateSelectionAvailability();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Choose an existing local or remote branch", FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Attached branches identify their current worktree and cannot be selected.", Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap },
                branchPicker,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { refresh, selectButton, cancel } },
            },
        };
        Loaded += (_, _) => branchPicker.Focus();
    }

    internal GitBranchReference SelectedBranch => branches[branchPicker.SelectedIndex];
    internal bool RefreshRequested { get; private set; }

    private void UpdateSelectionAvailability()
    {
        var branch = SelectedBranch;
        selectButton.IsEnabled = branch.IsRemote || branch.AttachedPath is null;
        AutomationProperties.SetHelpText(branchPicker, selectButton.IsEnabled ? string.Empty : "This local branch is already attached to another worktree.");
    }

    private static string Describe(GitBranchReference branch) =>
        branch.IsRemote ? $"Remote: {branch.Name}" : branch.AttachedPath is null ? $"Local: {branch.Name}" : $"Local: {branch.Name} — attached at {branch.AttachedPath}";
}
