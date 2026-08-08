using System.Diagnostics;
using System.IO;

namespace GabCode.Windows.Projects;

internal interface IGabCodeInstanceLauncher
{
    void Launch(string workspacePath);
}

internal sealed class GabCodeInstanceLauncher : IGabCodeInstanceLauncher
{
    public void Launch(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("gabCode could not determine its executable path to open the new workspace.");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(workspacePath));
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("gabCode could not start a new instance for the workspace.");
    }
}
