namespace GabCode.Windows.Terminal.Profiles;

internal sealed record TerminalShellCandidate(
    string ExecutablePath,
    string DisplayName,
    string Arguments = "");
