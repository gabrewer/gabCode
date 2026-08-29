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
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Workspace JSON must be an object.");
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new FormatException("Workspace name is required.");
        if (!root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object) throw new FormatException("Workspace project is required.");
        if (!project.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()) ||
            !project.TryGetProperty("mainBranch", out var mainBranch) || mainBranch.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(mainBranch.GetString()))
            throw new FormatException("Workspace project path and mainBranch are required.");
        return new WorkspaceDocument(1, name.GetString()!, new WorkspaceProject(path.GetString()!, mainBranch.GetString()!));
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
        return JsonSerializer.Serialize(new { name = Name, project = new { path, mainBranch = Project.MainBranch } }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

}

internal sealed record WorkspaceProject(string Path, string MainBranch);
