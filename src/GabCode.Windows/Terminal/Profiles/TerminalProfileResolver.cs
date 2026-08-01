using System.IO;
using System.Text.Json;

namespace GabCode.Windows.Terminal.Profiles;

internal sealed class TerminalProfileResolver
{
    private const string PowerShellCoreSource = "Windows.Terminal.PowershellCore";
    private readonly IReadOnlyList<string> settingsPaths;
    private readonly IReadOnlyList<TerminalShellCandidate> fallbackCandidates;

    internal TerminalProfileResolver(
        IEnumerable<string> settingsPaths,
        IEnumerable<TerminalShellCandidate> fallbackCandidates)
    {
        this.settingsPaths = settingsPaths?.ToArray() ?? throw new ArgumentNullException(nameof(settingsPaths));
        this.fallbackCandidates = fallbackCandidates?.ToArray() ?? throw new ArgumentNullException(nameof(fallbackCandidates));
    }

    internal static TerminalProfileResolver CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new TerminalProfileResolver(
            [
                Path.Combine(localApplicationData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
                Path.Combine(localApplicationData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
                Path.Combine(localApplicationData, "Microsoft", "Windows Terminal", "settings.json"),
            ],
            [
                new TerminalShellCandidate("pwsh.exe", "PowerShell 7 (pwsh)"),
                new TerminalShellCandidate(
                    Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "Windows PowerShell"),
                new TerminalShellCandidate(
                    Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(systemRoot, "System32", "cmd.exe"),
                    "Command Prompt (cmd.exe)",
                    "/d /q"),
            ]);
    }

    internal TerminalProfileResolution Resolve()
    {
        var settingsPath = settingsPaths.FirstOrDefault(File.Exists);
        if (settingsPath is null)
        {
            return ResolveFallback("Windows Terminal settings were not found.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(settingsPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return ResolveSettings(document.RootElement);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ResolveFallback($"Windows Terminal settings could not be read ({GetSafeReason(exception)}).");
        }
    }

    private TerminalProfileResolution ResolveSettings(JsonElement root)
    {
        if (!TryGetString(root, "defaultProfile", out var defaultProfile) || string.IsNullOrWhiteSpace(defaultProfile))
        {
            return ResolveFallback("Windows Terminal has no default profile selection.");
        }

        if (!root.TryGetProperty("profiles", out var profiles) ||
            !profiles.TryGetProperty("list", out var profileList) ||
            profileList.ValueKind != JsonValueKind.Array)
        {
            return ResolveFallback("Windows Terminal profile settings are malformed.");
        }

        JsonElement? selected = null;
        foreach (var profile in profileList.EnumerateArray())
        {
            if ((TryGetString(profile, "guid", out var guid) && IdentifiersEqual(guid, defaultProfile)) ||
                (TryGetString(profile, "name", out var profileName) && IdentifiersEqual(profileName, defaultProfile)))
            {
                selected = profile;
                break;
            }
        }

        if (selected is null)
        {
            return ResolveFallback("Windows Terminal's selected default profile was not found.");
        }

        var selectedProfile = selected.Value;
        var displayName = TryGetString(selectedProfile, "name", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "Unnamed Windows Terminal profile";
        if (selectedProfile.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
        {
            return ResolveFallback($"Windows Terminal profile '{displayName}' is hidden.");
        }

        profiles.TryGetProperty("defaults", out var defaults);
        var commandLine = GetInheritedString(selectedProfile, defaults, "commandline");
        var source = GetInheritedString(selectedProfile, defaults, "source");
        if (string.IsNullOrWhiteSpace(commandLine) && string.Equals(source, PowerShellCoreSource, StringComparison.OrdinalIgnoreCase))
        {
            commandLine = "pwsh.exe";
        }

        if (string.IsNullOrWhiteSpace(commandLine) || !TrySplitCommandLine(commandLine, out var executable, out var arguments))
        {
            return ResolveFallback($"Windows Terminal profile '{displayName}' has no representable command line.");
        }

        var resolvedExecutable = ResolveExecutable(executable);
        if (resolvedExecutable is null)
        {
            return ResolveFallback($"Windows Terminal profile '{displayName}' points to an unavailable command.");
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!TryAddEnvironment(defaults, environment) || !TryAddEnvironment(selectedProfile, environment))
        {
            return ResolveFallback($"Windows Terminal profile '{displayName}' has environment settings that cannot be represented safely.");
        }

        return new TerminalProfileResolution(
            displayName,
            resolvedExecutable,
            arguments,
            environment,
            usedFallback: false,
            $"Windows Terminal profile: {displayName}.");
    }

    private TerminalProfileResolution ResolveFallback(string reason)
    {
        foreach (var candidate in fallbackCandidates)
        {
            var resolved = ResolveExecutable(candidate.ExecutablePath);
            if (resolved is null)
            {
                continue;
            }

            return new TerminalProfileResolution(
                candidate.DisplayName,
                resolved,
                candidate.Arguments,
                new Dictionary<string, string?>(),
                usedFallback: true,
                $"{reason} Using fallback: {candidate.DisplayName}.");
        }

        throw new InvalidOperationException($"{reason} No supported local shell fallback is available.");
    }

    private static bool TryAddEnvironment(JsonElement profile, IDictionary<string, string?> environment)
    {
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("environment", out var settings))
        {
            return true;
        }

        if (settings.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var variable in settings.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Contains('=') || variable.Name.Contains('\0'))
            {
                return false;
            }

            if (variable.Value.ValueKind == JsonValueKind.Null)
            {
                environment[variable.Name] = null;
                continue;
            }

            if (variable.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = variable.Value.GetString() ?? string.Empty;
            if (value.Contains('\0'))
            {
                return false;
            }

            environment[variable.Name] = value;
        }

        return true;
    }

    private static string? GetInheritedString(JsonElement profile, JsonElement defaults, string propertyName)
    {
        if (TryGetString(profile, propertyName, out var profileValue) && !string.IsNullOrWhiteSpace(profileValue))
        {
            return profileValue;
        }

        return TryGetString(defaults, propertyName, out var defaultValue) ? defaultValue : null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TrySplitCommandLine(string commandLine, out string executable, out string arguments)
    {
        var expanded = Environment.ExpandEnvironmentVariables(commandLine).Trim();
        executable = string.Empty;
        arguments = string.Empty;
        if (expanded.Length == 0)
        {
            return false;
        }

        if (File.Exists(expanded))
        {
            executable = expanded;
            return true;
        }

        if (expanded[0] == '"')
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            executable = expanded[1..closingQuote];
            arguments = expanded[(closingQuote + 1)..].TrimStart();
            return executable.Length != 0;
        }

        var separator = expanded.IndexOfAny([' ', '\t']);
        if (separator < 0)
        {
            executable = expanded;
            return true;
        }

        executable = expanded[..separator];
        arguments = expanded[separator..].TrimStart();
        return executable.Length != 0;
    }

    private static string? ResolveExecutable(string executable)
    {
        var expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }

        var extensions = Path.HasExtension(expanded)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), expanded + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool IdentifiersEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string GetSafeReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access denied",
        JsonException => "invalid JSON",
        _ => "I/O failure",
    };
}
