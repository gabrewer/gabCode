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
        _ = await worktreeDiscovery.ResolveAsync(projectRoot, branch, cancellationToken);
        var document = new WorkspaceDocument(1, workspaceName, new WorkspaceProject(projectRoot, branch));
        await store.SaveNewAsync(workspacePath, document, projectRoot, cancellationToken);
        await preference.WriteAsync(workspacePath, cancellationToken);
        if (launchNewWindow) instanceLauncher.Launch(workspacePath);
        return new ProjectContext(workspaceName, projectRoot);
    }
}
