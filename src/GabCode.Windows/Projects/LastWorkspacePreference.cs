using System.IO;
using System.Text.Json;

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

internal sealed class WorkspaceSelectionPreference
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private readonly string preferencePath;

    internal WorkspaceSelectionPreference(string? preferencePath = null)
    {
        this.preferencePath = preferencePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gabCode",
            "workspace-selections.json");
    }

    internal async Task<string?> ReadAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadSelectionsAsync(cancellationToken)).GetValueOrDefault(Path.GetFullPath(workspacePath));
        }
        finally { gate.Release(); }
    }

    internal async Task WriteAsync(string workspacePath, string worktreePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(preferencePath)!;
            Directory.CreateDirectory(directory);
            var selections = await ReadSelectionsAsync(cancellationToken);
            selections[Path.GetFullPath(workspacePath)] = WorktreePath.Normalize(worktreePath);
            var temporaryPath = preferencePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(selections), cancellationToken);
            File.Move(temporaryPath, preferencePath, overwrite: true);
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadSelectionsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(preferencePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(preferencePath, cancellationToken));
            return parsed is null ? new(StringComparer.OrdinalIgnoreCase) : new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { return new(StringComparer.OrdinalIgnoreCase); }
    }
}
