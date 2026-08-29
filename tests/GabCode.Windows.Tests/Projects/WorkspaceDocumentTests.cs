using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceDocumentTests
{
    [Fact]
    public void Parses_v1_project_path_and_main_branch_and_resolves_relative_path()
    {
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"mainBranch\":\"main\"}}");
        var workspace = Path.Combine(Path.GetTempPath(), "gabCode workspace", "demo.gabcode-workspace");
        Assert.Equal("Demo", document.Name);
        Assert.Equal("main", document.Project.MainBranch);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspace)!, "project")), document.ResolveProjectPath(workspace));
    }

    [Fact]
    public void Ignores_version_and_unknown_properties_but_requires_main_branch()
    {
        var document = WorkspaceDocument.Parse("{\"version\":99,\"future\":true,\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"mainBranch\":\"main\",\"futureProjectValue\":\"x\"}}");
        Assert.Equal("main", document.Project.MainBranch);

        var exception = Assert.Throws<FormatException>(() => WorkspaceDocument.Parse("{\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"branch\":\"main\"}}"));
        Assert.Contains("mainBranch", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"\",\"project\":{\"path\":\"x\",\"mainBranch\":\"main\"}}")]
    [InlineData("{\"name\":\"x\",\"project\":{\"path\":\"x\"}}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"x\"}]}")]
    public void Rejects_invalid_documents(string json) => Assert.ThrowsAny<Exception>(() => WorkspaceDocument.Parse(json));

    [Fact]
    public void Writes_relative_project_path_when_workspace_and_project_share_a_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode workspace", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "demo.gabcode-workspace");
        var project = Path.Combine(root, "project");
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"project\":{\"path\":\"project\",\"mainBranch\":\"main\"}}");
        var json = document.ToJson(workspace, project);
        Assert.Contains("\"project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mainBranch\": \"main\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"branch\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"version\"", json, StringComparison.Ordinal);
    }
}
