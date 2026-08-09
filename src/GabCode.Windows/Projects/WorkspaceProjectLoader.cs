using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceProjectLoader
{
    private readonly GitWorktreeDiscovery worktreeDiscovery;

    internal WorkspaceProjectLoader(GitWorktreeDiscovery? worktreeDiscovery = null)
    {
        this.worktreeDiscovery = worktreeDiscovery ?? new GitWorktreeDiscovery();
    }

    internal async Task<ProjectContext> LoadAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var workspace = WorkspaceDocument.Parse(await File.ReadAllTextAsync(fullWorkspacePath, cancellationToken));
        var projectPath = workspace.ResolveProjectPath(fullWorkspacePath);
        var worktree = await worktreeDiscovery.ResolveAsync(projectPath, workspace.Project.Branch, cancellationToken: cancellationToken);
        return new ProjectContext(workspace.Name, worktree);
    }
}
