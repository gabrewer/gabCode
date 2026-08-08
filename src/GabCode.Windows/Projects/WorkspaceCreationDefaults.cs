using System.IO;

namespace GabCode.Windows.Projects;

internal static class WorkspaceCreationDefaults
{
    internal static string GetWorkspaceName(string selectedProjectFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedProjectFolder);
        return Path.GetFileName(Path.GetFullPath(selectedProjectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
