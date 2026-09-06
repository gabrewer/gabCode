using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeDeletionWorkflowTests
{
    [Fact]
    public void Deletion_surface_declares_guarded_confirmation_and_secondary_force_recovery()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows");
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var dialog = File.ReadAllText(Path.Combine(root, "Projects", "WorktreeDeletionDialog.cs"));

        Assert.Contains("WorktreeDeletionDialog", code);
        Assert.Contains("Force delete this worktree", code);
        Assert.Contains("Also delete the local branch", dialog);
        Assert.Contains("SelectRemainingWorktree", code);
    }

    [Fact]
    public void Deletion_surface_exposes_retained_folder_recovery_actions_and_safe_retry_guard()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows");
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        Assert.Contains("Retry cleanup", xaml);
        Assert.Contains("Open folder", xaml);
        Assert.Contains("RemovedWithRetainedPath", code);
        Assert.Contains("RetryRetainedWorktreeCleanup", code);
        Assert.Contains("Worktree removed; local folder remains", code);
        Assert.Contains("retainedWorktreeRepositoryPath", code);
        Assert.Contains("outcome.Entries.First(entry => entry.IsPrimary).Path", code);
        Assert.Contains("cleanup == RetainedCleanupState.Registered", code);
        Assert.Contains("Cleanup stopped because a worktree is registered", code);
        Assert.Contains("Cleanup cancelled; local folder remains", code);
        Assert.Contains("Worktree removed.", code);
        Assert.Contains("Worktree and local branch removed.", code);
        Assert.Contains("Local branch removed.", code);
        Assert.Contains("Local branch was retained:", code);
        Assert.Contains("var safe = current.FirstOrDefault", code);
        Assert.Contains("SelectWorktree(safe.Path", code);
    }

    [Fact]
    public void Primary_worktree_delete_is_disabled_in_the_context_menu()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("delete.IsEnabled = !entry.IsPrimary", code);
    }
}
