using System.IO;

namespace GabCode.Windows.Projects;

internal sealed record ProjectContext
{
    internal ProjectContext(string workspaceName, string projectFolder, string? mainBranch = null, bool usedPrimaryFallback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        WorkspaceName = workspaceName;
        ProjectFolder = Path.GetFullPath(projectFolder);
        MainBranch = string.IsNullOrWhiteSpace(mainBranch) ? null : mainBranch.Trim();
        UsedPrimaryFallback = usedPrimaryFallback;
    }

    internal string WorkspaceName { get; }

    internal string ProjectFolder { get; }

    internal string? MainBranch { get; }

    internal bool UsedPrimaryFallback { get; }

    internal string WindowTitle => $"{WorkspaceName} — {Path.GetFileName(ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))} — gabCode";
}
