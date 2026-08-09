using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceDocumentTests
{
    [Fact]
    public void Parses_v1_project_path_and_branch_and_resolves_relative_path()
    {
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"branch\":\"feature/demo\"}}");
        var workspace = Path.Combine(Path.GetTempPath(), "gabCode workspace", "demo.gabcode-workspace");
        Assert.Equal("Demo", document.Name);
        Assert.Equal("feature/demo", document.Project.Branch);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspace)!, "project")), document.ResolveProjectPath(workspace));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"name\":\"x\",\"project\":{\"path\":\"x\",\"branch\":\"main\"}}")]
    [InlineData("{\"version\":1,\"name\":\"\",\"project\":{\"path\":\"x\",\"branch\":\"main\"}}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"project\":{\"path\":\"x\"}}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"x\"}]}")]
    public void Rejects_invalid_documents(string json) => Assert.ThrowsAny<Exception>(() => WorkspaceDocument.Parse(json));

    [Fact]
    public void Writes_relative_project_path_when_workspace_and_project_share_a_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode workspace", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "demo.gabcode-workspace");
        var project = Path.Combine(root, "project");
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"branch\":\"main\"}}");
        var json = document.ToJson(workspace, project);
        Assert.Contains("\"project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"branch\": \"main\"", json, StringComparison.Ordinal);
    }
}
