using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceProjectLoader
{
    private readonly GitWorktreeDiscovery worktreeDiscovery;
    private readonly WorkspaceSelectionPreference selectionPreference;

    internal WorkspaceProjectLoader(GitWorktreeDiscovery? worktreeDiscovery = null, WorkspaceSelectionPreference? selectionPreference = null)
    {
        this.worktreeDiscovery = worktreeDiscovery ?? new GitWorktreeDiscovery();
        this.selectionPreference = selectionPreference ?? new WorkspaceSelectionPreference();
    }

    internal async Task<ProjectContext> LoadAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var workspace = WorkspaceDocument.Parse(await File.ReadAllTextAsync(fullWorkspacePath, cancellationToken));
        var projectPath = workspace.ResolveProjectPath(fullWorkspacePath);
        var entries = await worktreeDiscovery.DiscoverEntriesAsync(projectPath, cancellationToken: cancellationToken);
        if (!await worktreeDiscovery.LocalBranchExistsAsync(projectPath, workspace.Project.MainBranch, cancellationToken))
            throw new FormatException($"Workspace mainBranch '{workspace.Project.MainBranch}' is not a local branch.");
        var primary = entries.SingleOrDefault(entry => entry.IsPrimary && Directory.Exists(entry.Path))
            ?? throw new InvalidOperationException("No accessible primary worktree is available.");
        var rememberedPath = await selectionPreference.ReadAsync(fullWorkspacePath, cancellationToken);
        var remembered = rememberedPath is null
            ? null
            : entries.SingleOrDefault(entry => WorktreePath.Comparer.Equals(entry.Path, rememberedPath) && Directory.Exists(entry.Path));
        var usedPrimaryFallback = rememberedPath is not null && remembered is null;
        var selected = remembered ?? primary;
        return new ProjectContext(workspace.Name, selected.Path, workspace.Project.MainBranch, usedPrimaryFallback);
    }
}
