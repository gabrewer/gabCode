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
    public void Primary_worktree_delete_is_disabled_in_the_context_menu()
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows", "MainWindow.xaml.cs"));
        Assert.Contains("delete.IsEnabled = !entry.IsPrimary", code);
    }
}
