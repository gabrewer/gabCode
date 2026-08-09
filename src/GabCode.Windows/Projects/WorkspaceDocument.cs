using System.IO;
using System.Linq;
using System.Text.Json;

namespace GabCode.Windows.Projects;

internal sealed record WorkspaceDocument(int Version, string Name, WorkspaceProject Project)
{
    internal static WorkspaceDocument Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireExactProperties(root, "version", "name", "project");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != 1)
            throw new FormatException("Workspace version 1 is required.");
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new FormatException("Workspace name is required.");
        if (!root.TryGetProperty("project", out var project)) throw new FormatException("Workspace project is required.");
        RequireExactProperties(project, "path", "branch");
        if (!project.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()) ||
            !project.TryGetProperty("branch", out var branch) || branch.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(branch.GetString()))
            throw new FormatException("Workspace project path and branch are required.");
        return new WorkspaceDocument(value, name.GetString()!, new WorkspaceProject(path.GetString()!, branch.GetString()!));
    }

    internal string ResolveProjectPath(string workspacePath)
    {
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        return Path.IsPathFullyQualified(Project.Path)
            ? Path.GetFullPath(Project.Path)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullWorkspacePath)!, Project.Path));
    }

    internal string ToJson(string workspacePath, string projectPath)
    {
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var fullProjectPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(fullWorkspacePath)!;
        var path = string.Equals(Path.GetPathRoot(fullWorkspacePath), Path.GetPathRoot(fullProjectPath), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(directory, fullProjectPath) : fullProjectPath;
        return JsonSerializer.Serialize(new { version = Version, name = Name, project = new { path, branch = Project.Branch } }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)) || element.EnumerateObject().Count() != names.Length)
            throw new FormatException($"Workspace object must contain exactly {string.Join(", ", names)}.");
    }
}

internal sealed record WorkspaceProject(string Path, string Branch);
