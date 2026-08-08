using System.IO;

namespace GabCode.Windows.Projects;

internal sealed record ProjectContext
{
    internal ProjectContext(string workspaceName, string projectFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        WorkspaceName = workspaceName;
        ProjectFolder = Path.GetFullPath(projectFolder);
    }

    internal string WorkspaceName { get; }

    internal string ProjectFolder { get; }

    internal string WindowTitle => $"{WorkspaceName} — {Path.GetFileName(ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))} — gabCode";
}
