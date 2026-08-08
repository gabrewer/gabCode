using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceNameDialog : Window
{
    private readonly TextBox nameTextBox;

    internal WorkspaceNameDialog(string suggestedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        Title = "Name Workspace";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        nameTextBox = new TextBox { Text = suggestedName, MinWidth = 300, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetName(nameTextBox, "Workspace name");
        var create = new Button { Content = "Create", IsDefault = true, MinWidth = 80 };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(WorkspaceName)) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Workspace name" },
                nameTextBox,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { create, cancel } },
            },
        };
        Loaded += (_, _) => { nameTextBox.Focus(); nameTextBox.SelectAll(); };
    }

    internal string WorkspaceName
    {
        get => nameTextBox.Text.Trim();
        set => nameTextBox.Text = value?.Trim() ?? string.Empty;
    }
}
