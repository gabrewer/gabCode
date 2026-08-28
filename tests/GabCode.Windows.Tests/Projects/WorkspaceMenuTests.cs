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
        Assert.Contains("InputGestureText=\"F5\"", xaml);
        Assert.Contains("WorktreeSidebar", xaml);
        Assert.Contains("WorktreeList", xaml);
        Assert.Contains("x:Name=\"WorktreeSidebar\" Background=\"Black\"", xaml);
        Assert.Contains("x:Name=\"WorktreeList\" Background=\"Black\" Foreground=\"White\"", xaml);

        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("CreateTerminalWorkspace();\n        UpdateSidebarIndicators();", code.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Main_window_declares_worktree_action_context_menu()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows");
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        Assert.Contains("ContextMenu", xaml);
        Assert.Contains("Create worktree from main", xaml);
        Assert.Contains("Create worktree from selected branch", xaml);
        Assert.Contains("Create worktree from existing branch", xaml);
        Assert.Contains("Delete worktree", xaml);
        Assert.Contains("Open in VS Code", xaml);
        Assert.Contains("Reveal in Explorer", xaml);
        Assert.Contains("CreateWorktreeFromMain", code);
        Assert.Contains("CreateWorktreeFromSelectedBranch", code);
        Assert.Contains("CreateWorktreeFromExistingBranch", code);
        Assert.Contains("DeleteWorktree", code);
    }

    [Fact]
    public void Switching_worktrees_preserves_the_configured_main_branch_for_from_main()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("new ProjectContext(project!.WorkspaceName, entry.Path, project.MainBranch)", code);
        Assert.Contains("project?.MainBranch ?? ContextEntry(sender)?.Branch", code);
    }

    [Fact]
    public void Convenience_launch_failures_are_reported_without_dispatcher_escape()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("Could not open worktree in VS Code", code);
        Assert.Contains("Could not reveal worktree in Explorer", code);
        Assert.Contains("catch (Exception exception)", code);
    }

    [Fact]
    public void Creation_operations_are_single_flight_cancellable_and_reconciled()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("RunWorktreeActionAsync", code);
        Assert.Contains("worktreeActionCancellation", code);
        Assert.Contains("Worktree action cancelled.", code);
        Assert.Contains("ReconcileWorktrees", code);
    }

    [Fact]
    public void Creation_surface_declares_editable_previews_and_optional_actions()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows");
        var files = string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

        Assert.Contains("Branch preview", files);
        Assert.Contains("Location preview", files);
        Assert.Contains("Use the latest remote version of the workspace branch", files);
        Assert.Contains("Create a VS Code workspace file", files);
        Assert.Contains("Open in VS Code after creation", files);
        Assert.Contains("Refresh remote branches", files);
        Assert.Contains("Browse", files);
    }
}
