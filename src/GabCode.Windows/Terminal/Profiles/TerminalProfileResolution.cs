using System.Collections.ObjectModel;

namespace GabCode.Windows.Terminal.Profiles;

internal sealed class TerminalProfileResolution
{
    internal TerminalProfileResolution(
        string displayName,
        string executablePath,
        string arguments,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        bool usedFallback,
        string statusMessage)
    {
        DisplayName = displayName;
        ExecutablePath = executablePath;
        Arguments = arguments;
        EnvironmentOverrides = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(environmentOverrides, StringComparer.OrdinalIgnoreCase));
        UsedFallback = usedFallback;
        StatusMessage = statusMessage;
    }

    internal string DisplayName { get; }

    internal string ExecutablePath { get; }

    internal string Arguments { get; }

    internal IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; }

    internal bool UsedFallback { get; }

    internal string StatusMessage { get; }
}
