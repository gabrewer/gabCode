using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceProjectLoader
{
    private readonly GitRepositoryValidator gitRepositoryValidator;

    internal WorkspaceProjectLoader(GitRepositoryValidator? gitRepositoryValidator = null)
    {
        this.gitRepositoryValidator = gitRepositoryValidator ?? new GitRepositoryValidator();
    }

    internal async Task<ProjectContext> LoadAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var workspace = WorkspaceDocument.Parse(await File.ReadAllTextAsync(fullWorkspacePath, cancellationToken));
        var folder = workspace.ResolveFolder(fullWorkspacePath);
        _ = await gitRepositoryValidator.FindRepositoryAsync(folder, cancellationToken);
        return new ProjectContext(workspace.Name, folder);
    }
}
