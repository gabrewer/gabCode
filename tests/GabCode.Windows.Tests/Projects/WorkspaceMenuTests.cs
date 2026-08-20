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
        Assert.Contains("<Menu Background=\"#242424\" Foreground=\"White\"", xaml);
        Assert.Contains("SystemColors.MenuBrushKey", xaml);
        Assert.DoesNotContain("CompactMenuCommand", xaml);
        Assert.DoesNotContain("<Popup x:Name=\"PART_Popup\"", xaml);
        Assert.Contains("<MenuItem x:Name=\"FileMenuItem\" Header=\"_File\" Background=\"#242424\" Foreground=\"White\" Loaded=\"FileMenuItem_Loaded\"", xaml);
        Assert.Contains("<MenuItem Header=\"_Open Workspace…\" Background=\"#242424\" Foreground=\"White\" Click=\"OpenWorkspaceButton_Click\"", xaml);
        Assert.Contains("CreateWorkspaceButton_Click", xaml);
        Assert.Contains("Header=\"_View\"", xaml);
        Assert.Contains("Move Sidebar _Right", xaml);
        Assert.Contains("Move Sidebar _Left", xaml);
        Assert.Contains("Refresh Worktrees", xaml);
        Assert.Contains("InputGestureText=\"Ctrl+R\"", xaml);
        Assert.Contains("WorktreeSidebar", xaml);
        Assert.Contains("WorktreeList", xaml);
        Assert.Contains("x:Name=\"WorktreeSidebar\" Background=\"Black\"", xaml);
        Assert.Contains("x:Name=\"WorktreeList\" Background=\"Black\" Foreground=\"White\"", xaml);

        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("CreateTerminalWorkspace();\n        UpdateSidebarIndicators();", code.Replace("\r\n", "\n"));
    }
}
