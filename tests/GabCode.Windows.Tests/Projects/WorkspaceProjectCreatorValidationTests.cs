using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceProjectCreatorValidationTests
{
    [Fact]
    public async Task Validate_git_folder_rejects_non_git_before_descriptor_publication()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gabCode workspace folder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var creator = new WorkspaceProjectCreator();
            await Assert.ThrowsAsync<InvalidOperationException>(() => creator.ValidateGitFolderAsync(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
