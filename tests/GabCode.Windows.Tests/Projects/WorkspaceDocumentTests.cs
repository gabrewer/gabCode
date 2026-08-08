using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceDocumentTests
{
    [Fact]
    public void Parses_v1_name_and_one_folder_and_resolves_relative_path()
    {
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"folders\":[{\"path\":\"repo\"}]}");
        var workspace = Path.Combine(Path.GetTempPath(), "gabCode workspace", "demo.gabcode-workspace");
        Assert.Equal("Demo", document.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspace)!, "repo")), document.ResolveFolder(workspace));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"name\":\"x\",\"folders\":[{\"path\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"name\":\"\",\"folders\":[{\"path\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"folders\":[]}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"x\",\"extra\":1}]}")]
    public void Rejects_invalid_documents(string json) => Assert.ThrowsAny<Exception>(() => WorkspaceDocument.Parse(json));

    [Fact]
    public void Writes_relative_path_when_workspace_and_folder_share_a_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode workspace", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "demo.gabcode-workspace");
        var folder = Path.Combine(root, "repo");
        var json = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"folders\":[{\"path\":\"repo\"}]}").ToJson(workspace, folder);
        Assert.Contains("\"path\": \"repo\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_workspace_write_leaves_no_final_or_temporary_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspacePath = Path.Combine(root, "demo.gabcode-workspace");
        var folder = Path.Combine(root, "repo Ω");
        Directory.CreateDirectory(folder);
        var document = WorkspaceDocument.Parse("{\"version\":1,\"name\":\"Demo\",\"folders\":[{\"path\":\"repo Ω\"}]}");
        var store = new WorkspaceFileStore(_ => throw new IOException("simulated write failure"));

        await Assert.ThrowsAsync<IOException>(() => store.SaveNewAsync(workspacePath, document, folder));

        Assert.False(File.Exists(workspacePath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
}
