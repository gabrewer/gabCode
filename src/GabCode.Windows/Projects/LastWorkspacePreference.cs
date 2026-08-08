using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class LastWorkspacePreference
{
    private readonly string preferencePath;

    internal LastWorkspacePreference(string? preferencePath = null)
    {
        this.preferencePath = preferencePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gabCode",
            "last-workspace.txt");
    }

    internal async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(preferencePath)) return null;
        var value = (await File.ReadAllTextAsync(preferencePath, cancellationToken)).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal async Task WriteAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        var temporaryPath = preferencePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, Path.GetFullPath(workspacePath), cancellationToken);
        File.Move(temporaryPath, preferencePath, overwrite: true);
    }

    internal void Forget()
    {
        if (File.Exists(preferencePath)) File.Delete(preferencePath);
    }
}
