namespace GabCode.Windows.Projects;

internal sealed class WorkspaceProjectCreator
{
    private readonly GitRepositoryValidator validator;
    private readonly WorkspaceFileStore store;
    private readonly IGabCodeInstanceLauncher instanceLauncher;
    private readonly LastWorkspacePreference preference;

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

    internal async Task<ProjectContext> CreateAsync(string workspacePath, string workspaceName, string folder, CancellationToken cancellationToken = default)
    {
        _ = await validator.FindRepositoryAsync(folder, cancellationToken);
        var document = new WorkspaceDocument(1, workspaceName, new WorkspaceFolder(folder));
        await store.SaveNewAsync(workspacePath, document, folder, cancellationToken);
        await preference.WriteAsync(workspacePath, cancellationToken);
        instanceLauncher.Launch(workspacePath);
        return new ProjectContext(workspaceName, folder);
    }
}
