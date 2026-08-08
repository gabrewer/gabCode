using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceCreationDefaultsTests
{
    [Fact]
    public void Workspace_name_defaults_to_selected_project_folder_basename()
    {
        Assert.Equal("my project", WorkspaceCreationDefaults.GetWorkspaceName("C:\\source\\my project"));
    }
}
