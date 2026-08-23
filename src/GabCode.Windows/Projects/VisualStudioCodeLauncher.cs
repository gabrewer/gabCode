using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GabCode.Windows.Projects;

internal static class VisualStudioCodeLauncher
{
    internal static void Open(string executable, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        info.ArgumentList.Add(target);
        _ = Process.Start(info) ?? throw new InvalidOperationException("VS Code could not be started.");
    }

    internal static string FindExecutable(Func<Environment.SpecialFolder, string> folderPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(fileExists);
        var candidates = new[]
        {
            Path.Combine(folderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(folderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(folderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
        };
        return candidates.FirstOrDefault(fileExists) ?? "code";
    }
}
