using System.IO;
using System.Linq;
using System.Text.Json;

namespace GabCode.Windows.Projects;

internal sealed record WorkspaceDocument(int Version, string Name, WorkspaceFolder Folder)
{
    internal IReadOnlyList<WorkspaceFolder> Folders => [Folder];

    internal static WorkspaceDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireExactProperties(root, "version", "name", "folders");

        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionValue) ||
            versionValue != 1)
        {
            throw new FormatException("Workspace version 1 is required.");
        }

        if (!root.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
        {
            throw new FormatException("Workspace name is required.");
        }

        if (!root.TryGetProperty("folders", out var folders) ||
            folders.ValueKind != JsonValueKind.Array ||
            folders.GetArrayLength() != 1)
        {
            throw new FormatException("Workspace must contain exactly one folder.");
        }

        var folder = folders[0];
        RequireExactProperties(folder, "path");
        if (!folder.TryGetProperty("path", out var path) ||
            path.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(path.GetString()))
        {
            throw new FormatException("Workspace folder path is required.");
        }

        return new WorkspaceDocument(versionValue, name.GetString()!, new WorkspaceFolder(path.GetString()!));
    }

    internal string ResolveFolder(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var fullFolderPath = Path.IsPathFullyQualified(Folder.Path)
            ? Path.GetFullPath(Folder.Path)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullWorkspacePath)!, Folder.Path));
        return fullFolderPath;
    }

    internal string ToJson(string workspacePath, string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var fullFolderPath = Path.GetFullPath(folderPath);
        var workspaceDirectory = Path.GetDirectoryName(fullWorkspacePath)!;
        var path = string.Equals(Path.GetPathRoot(fullWorkspacePath), Path.GetPathRoot(fullFolderPath), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspaceDirectory, fullFolderPath)
            : fullFolderPath;

        return JsonSerializer.Serialize(
            new { version = Version, name = Name, folders = new[] { new { path } } },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)) ||
            element.EnumerateObject().Count() != names.Length)
        {
            throw new FormatException($"Workspace object must contain exactly {string.Join(", ", names)}.");
        }
    }
}

internal sealed record WorkspaceFolder(string Path);
