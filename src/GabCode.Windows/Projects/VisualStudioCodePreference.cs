using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class VisualStudioCodePreference
{
    private readonly string path;

    internal VisualStudioCodePreference(string? path = null) =>
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gabCode", "vscode-path.txt");

    internal string? Read() => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    internal string Resolve() => Read() is { Length: > 0 } configured && File.Exists(configured)
        ? configured
        : VisualStudioCodeLauncher.FindExecutable(Environment.GetFolderPath, File.Exists);

    internal void Write(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Path.GetFullPath(executablePath));
    }
}
