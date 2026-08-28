using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceProjectCreator
{
    private readonly GitRepositoryValidator validator;
    private readonly WorkspaceFileStore store;
    private readonly IGabCodeInstanceLauncher instanceLauncher;
    private readonly LastWorkspacePreference preference;
    private readonly GitWorktreeDiscovery worktreeDiscovery = new();

    internal WorkspaceProjectCreator(
        GitRepositoryValidator? validator = null,
        WorkspaceFileStore? store = null,
        IGabCodeInstanceLauncher? instanceLauncher = null,
        LastWorkspacePreference? preference = null)
    {
        this.validator = validator ?? new GitRepositoryValidator();
        this.store = store ?? new WorkspaceFileStore();
        this.instanceLauncher = instanceLauncher ?? new GabCodeInstanceLauncher();
        this.preference = preference ?? new LastWorkspacePreference();
    }

    internal Task<string> ValidateGitFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        validator.FindRepositoryAsync(folder, cancellationToken);

    internal async Task<ProjectContext> CreateAsync(string workspacePath, string workspaceName, string projectRoot, string branch, bool launchNewWindow, CancellationToken cancellationToken = default)
    {
        var entries = await worktreeDiscovery.DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        if (!await worktreeDiscovery.LocalBranchExistsAsync(projectRoot, branch, cancellationToken))
            throw new FormatException($"Workspace mainBranch '{branch}' is not a local branch.");
        var primary = entries.SingleOrDefault(entry => entry.IsPrimary && Directory.Exists(entry.Path))
            ?? throw new InvalidOperationException("No accessible primary worktree is available.");
        var document = new WorkspaceDocument(1, workspaceName, new WorkspaceProject(projectRoot, branch));
        await store.SaveNewAsync(workspacePath, document, projectRoot, cancellationToken);
        await preference.WriteAsync(workspacePath, cancellationToken);
        if (launchNewWindow) instanceLauncher.Launch(workspacePath);
        return new ProjectContext(workspaceName, primary.Path, branch);
    }
}
