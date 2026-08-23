using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GabCode.Windows.Projects;

internal sealed class VisualStudioCodeSettingsDialog : Window
{
    private readonly TextBox pathTextBox;

    internal VisualStudioCodeSettingsDialog(string currentPath)
    {
        Title = "VS Code Settings";
        Width = 650;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        pathTextBox = new TextBox { Text = currentPath, MinWidth = 500 };
        AutomationProperties.SetName(pathTextBox, "VS Code executable path");
        var browse = new Button { Content = "Browse", Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "VS Code executable (Code.exe)|Code.exe|Executable files|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) pathTextBox.Text = dialog.FileName;
        };
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        save.Click += (_, _) =>
        {
            if (File.Exists(pathTextBox.Text.Trim())) DialogResult = true;
            else MessageBox.Show(this, "Choose an existing Code.exe path.", "Invalid VS Code path", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse); row.Children.Add(pathTextBox);
        Content = new StackPanel { Margin = new Thickness(20), Children =
        {
            new TextBlock { Text = "VS Code executable", FontWeight = FontWeights.SemiBold },
            new TextBlock { Text = "gabCode uses this executable for Open in VS Code actions.", Margin = new Thickness(0, 6, 0, 10) },
            row,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { save, cancel } },
        }};
    }

    internal string ExecutablePath => pathTextBox.Text.Trim();
}
