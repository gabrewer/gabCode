using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceSelectionPreferenceTests
{
    [Fact]
    public async Task Stores_a_normalized_worktree_path_by_workspace_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode selection preference", Guid.NewGuid().ToString("N"));
        var store = Path.Combine(root, "selections.json");
        var workspace = Path.Combine(root, "project.gabcode-workspace");
        try
        {
            var preference = new WorkspaceSelectionPreference(store);
            await preference.WriteAsync(workspace, Path.Combine(root, "wt", "feature", "..", "feature"));

            Assert.Equal(WorktreePath.Normalize(Path.Combine(root, "wt", "feature")), await preference.ReadAsync(workspace));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
