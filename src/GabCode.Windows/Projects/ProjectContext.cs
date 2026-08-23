using System.IO;

namespace GabCode.Windows.Projects;

internal sealed record ProjectContext
{
    internal ProjectContext(string workspaceName, string projectFolder, string? selectedBranch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        WorkspaceName = workspaceName;
        ProjectFolder = Path.GetFullPath(projectFolder);
        SelectedBranch = string.IsNullOrWhiteSpace(selectedBranch) ? null : selectedBranch.Trim();
    }

    internal string WorkspaceName { get; }

    internal string ProjectFolder { get; }

    internal string? SelectedBranch { get; }

    internal string WindowTitle => $"{WorkspaceName} — {Path.GetFileName(ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))} — gabCode";
}
