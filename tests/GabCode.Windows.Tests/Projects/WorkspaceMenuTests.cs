using System;
using System.IO;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceMenuTests
{
    [Fact]
    public void Main_window_declares_native_file_menu_workspace_commands()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml"));
        Assert.Contains("Header=\"_File\"", xaml);
        Assert.Contains("Header=\"_Open Workspace…\"", xaml);
        Assert.Contains("Header=\"_Create Workspace…\"", xaml);
        Assert.Contains("OpenWorkspaceButton_Click", xaml);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"White\" />", xaml);
        Assert.Contains("Property=\"IsHighlighted\" Value=\"True\"", xaml);
        Assert.Contains("CreateWorkspaceButton_Click", xaml);
    }
}
