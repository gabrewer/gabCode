namespace GabCode.Windows.Projects;

internal sealed record StartupWorkspaceSelection(bool IsExplicitEmpty, string? WorkspacePath)
{
    internal static StartupWorkspaceSelection Resolve(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Length switch
        {
            0 => new StartupWorkspaceSelection(false, null),
            1 when string.Equals(arguments[0], "--empty", StringComparison.Ordinal) => new StartupWorkspaceSelection(true, null),
            1 when !string.IsNullOrWhiteSpace(arguments[0]) => new StartupWorkspaceSelection(false, arguments[0]),
            _ => throw new ArgumentException("gabCode accepts either one workspace path or --empty."),
        };
    }
}
