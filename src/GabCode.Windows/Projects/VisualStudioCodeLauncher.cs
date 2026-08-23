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

    internal static string FindExecutable(Func<Environment.SpecialFolder, string> folderPath, Func<string, bool> fileExists, string? pathValue = null)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var pathEntry in (pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = pathEntry.Trim().Trim('"');
            var candidate = Path.Combine(entry, "Code.exe");
            if (fileExists(candidate)) return candidate;
            var parent = Directory.GetParent(entry)?.FullName;
            candidate = parent is null ? string.Empty : Path.Combine(parent, "Code.exe");
            if (candidate.Length > 0 && fileExists(candidate)) return candidate;
        }
        var candidates = new[]
        {
            Path.Combine(folderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(folderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(folderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
        };
        return candidates.FirstOrDefault(fileExists) ?? "code";
    }
}
