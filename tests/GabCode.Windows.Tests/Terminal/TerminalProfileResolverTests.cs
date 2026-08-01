using System.IO;
using System.Text.Json;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Tests.Terminal;

public sealed class TerminalProfileResolverTests
{
    [Fact]
    public void Resolver_uses_the_default_profile_command_line_and_environment_when_representable()
    {
        var commandLine = JsonSerializer.Serialize($"{Environment.GetEnvironmentVariable("ComSpec")} /d /q");
        var settingsPath = CreateSettingsFile(
            $$"""
            {
              "defaultProfile": "{11111111-1111-1111-1111-111111111111}",
              "profiles": {
                "list": [
                  {
                    "guid": "{11111111-1111-1111-1111-111111111111}",
                    "name": "Test profile",
                    "commandline": {{commandLine}},
                    "environment": { "GABCODE_TEST_PROFILE": "Ω 漢字" }
                  }
                ]
              }
            }
            """);

        var resolution = new TerminalProfileResolver(
            [settingsPath],
            [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd")]).Resolve();

        Assert.False(resolution.UsedFallback);
        Assert.Equal("Test profile", resolution.DisplayName);
        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec"), resolution.ExecutablePath, ignoreCase: true);
        Assert.Equal("/d /q", resolution.Arguments);
        Assert.Equal("Ω 漢字", resolution.EnvironmentOverrides["GABCODE_TEST_PROFILE"]);
        Assert.Contains("Test profile", resolution.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_treats_an_invalid_profile_environment_as_unrepresentable_and_uses_fallback()
    {
        var commandLine = JsonSerializer.Serialize(Environment.GetEnvironmentVariable("ComSpec"));
        var settingsPath = CreateSettingsFile(
            $$"""
            {
              "defaultProfile": "{33333333-3333-3333-3333-333333333333}",
              "profiles": {
                "list": [
                  {
                    "guid": "{33333333-3333-3333-3333-333333333333}",
                    "name": "Invalid environment profile",
                    "commandline": {{commandLine}},
                    "environment": { "INVALID=NAME": "value" }
                  }
                ]
              }
            }
            """);

        var resolution = new TerminalProfileResolver(
            [settingsPath],
            [new TerminalShellCandidate(Environment.GetEnvironmentVariable("ComSpec")!, "cmd")]).Resolve();

        Assert.True(resolution.UsedFallback);
        Assert.Equal("cmd", resolution.DisplayName);
        Assert.Contains("Invalid environment profile", resolution.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("fallback", resolution.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_reports_a_malformed_default_profile_and_uses_the_first_available_fallback()
    {
        var settingsPath = CreateSettingsFile(
            """
            {
              "defaultProfile": "{22222222-2222-2222-2222-222222222222}",
              "profiles": { "list": [ { "guid": "{22222222-2222-2222-2222-222222222222}", "name": "Broken", "commandline": "" } ] }
            }
            """);
        var fallback = Environment.GetEnvironmentVariable("ComSpec")!;

        var resolution = new TerminalProfileResolver(
            [settingsPath],
            [new TerminalShellCandidate("missing-gabcode-shell.exe", "missing"), new TerminalShellCandidate(fallback, "cmd")]).Resolve();

        Assert.True(resolution.UsedFallback);
        Assert.Equal(fallback, resolution.ExecutablePath, ignoreCase: true);
        Assert.Equal("cmd", resolution.DisplayName);
        Assert.Contains("Broken", resolution.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("fallback", resolution.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateSettingsFile(string content)
    {
        _ = JsonDocument.Parse(content);
        var directory = Path.Combine(Path.GetTempPath(), "gabCode profile Ω", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, content);
        return path;
    }
}
