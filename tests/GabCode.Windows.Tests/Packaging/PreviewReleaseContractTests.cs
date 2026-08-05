using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GabCode.Windows.Tests.Packaging;

public sealed class PreviewReleaseContractTests
{
    [Fact]
    public void Local_preview_workflow_defines_three_explicit_operator_commands_and_their_authority_boundaries()
    {
        var workflow = ReadRepositoryFile("Documentation", "release", "local-preview-workflow.md");

        Assert.Contains("/build-preview-dmg <version>", workflow, StringComparison.Ordinal);
        Assert.Contains("/build-preview-msi <version>", workflow, StringComparison.Ordinal);
        Assert.Contains("/release-preview <version>", workflow, StringComparison.Ordinal);
        Assert.Contains("never builds installers", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("creates no GitHub control issue", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("creates no GitHub control issue", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smb://", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_schema_requires_exact_recomputable_artifact_identity_without_secret_fields()
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("eng", "release", "preview-evidence.schema.json"));
        var root = document.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new[] { "schemaVersion", "platform", "version", "sourceCommit", "artifact", "toolchain", "verification" }.Order(StringComparer.Ordinal),
            required.Order(StringComparer.Ordinal));

        var properties = root.GetProperty("properties");
        Assert.Equal("^\\d+\\.\\d+\\.\\d+-preview$", properties.GetProperty("version").GetProperty("pattern").GetString());
        Assert.Equal("^[0-9a-f]{40}$", properties.GetProperty("sourceCommit").GetProperty("pattern").GetString());

        var artifact = properties.GetProperty("artifact");
        Assert.Equal("object", artifact.GetProperty("type").GetString());
        Assert.False(artifact.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            new[] { "bytes", "fileName", "sha256" },
            artifact.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("^[0-9a-f]{64}$", artifact.GetProperty("properties").GetProperty("sha256").GetProperty("pattern").GetString());

        var schemaText = root.GetRawText();
        Assert.DoesNotContain("credential", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", schemaText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("windows", "^gabCode-\\d+\\.\\d+\\.\\d+-preview-windows-x64\\.msi$", "^gabCode-\\d+\\.\\d+\\.\\d+-preview-windows-x64\\.evidence\\.json$")]
    [InlineData("macos", "^gabCode-\\d+\\.\\d+\\.\\d+-preview-macos-arm64\\.dmg$", "^gabCode-\\d+\\.\\d+\\.\\d+-preview-macos-arm64\\.evidence\\.json$")]
    public void Evidence_schema_encodes_the_exact_platform_filename_pair(string platform, string artifactPattern, string evidencePattern)
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("eng", "release", "preview-evidence.schema.json"));
        var root = document.RootElement;
        var platformDefinition = root.GetProperty("$defs").GetProperty(platform);

        Assert.Equal(platform, platformDefinition.GetProperty("properties").GetProperty("platform").GetProperty("const").GetString());
        Assert.Equal(artifactPattern, platformDefinition.GetProperty("properties").GetProperty("artifact").GetProperty("properties").GetProperty("fileName").GetProperty("pattern").GetString());
        Assert.Equal(evidencePattern, platformDefinition.GetProperty("properties").GetProperty("evidenceFileName").GetProperty("pattern").GetString());
    }

    [Theory]
    [InlineData("0.0.3-preview", true)]
    [InlineData("0.0.3-preview.1", false)]
    [InlineData("0.0.3-preview.0", false)]
    [InlineData("0.0.3-preview.-1", false)]
    [InlineData("0.0.3-rc", false)]
    [InlineData("0.0.3", false)]
    [InlineData("v0.0.3-preview", false)]
    public void Evidence_schema_preview_version_pattern_rejects_unsupported_versions(string version, bool expectedMatch)
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("eng", "release", "preview-evidence.schema.json"));
        var pattern = document.RootElement.GetProperty("properties").GetProperty("version").GetProperty("pattern").GetString();

        Assert.NotNull(pattern);
        Assert.Equal(expectedMatch, Regex.IsMatch(version, pattern, RegexOptions.CultureInvariant));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456g", false)]
    [InlineData("0123456789abcdef0123456789abcdef012345678", false)]
    public void Evidence_schema_identity_patterns_reject_malformed_commit_and_hash_values(string commit, bool expectedCommitMatch)
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("eng", "release", "preview-evidence.schema.json"));
        var root = document.RootElement;
        var commitPattern = root.GetProperty("properties").GetProperty("sourceCommit").GetProperty("pattern").GetString();
        var hashPattern = root.GetProperty("properties").GetProperty("artifact").GetProperty("properties").GetProperty("sha256").GetProperty("pattern").GetString();

        Assert.NotNull(commitPattern);
        Assert.NotNull(hashPattern);
        Assert.Equal(expectedCommitMatch, Regex.IsMatch(commit, commitPattern, RegexOptions.CultureInvariant));
        Assert.True(Regex.IsMatch(new string('a', 64), hashPattern, RegexOptions.CultureInvariant));
        Assert.False(Regex.IsMatch(new string('A', 64), hashPattern, RegexOptions.CultureInvariant));
        Assert.False(Regex.IsMatch(new string('a', 63), hashPattern, RegexOptions.CultureInvariant));
        Assert.False(Regex.IsMatch(new string('a', 65), hashPattern, RegexOptions.CultureInvariant));
    }

    [Fact]
    public void Evidence_schema_and_workflow_explicitly_reject_unknown_partial_and_mismatched_release_inputs()
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("eng", "release", "preview-evidence.schema.json"));
        var root = document.RootElement;
        var workflow = ReadRepositoryFile("Documentation", "release", "local-preview-workflow.md");

        Assert.Equal(new[] { "macos", "windows" }, root.GetProperty("properties").GetProperty("platform").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("partial pair", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unexpected entry", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mismatched commit/version/name/hash", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("regular, non-symlink inputs", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not create a separate release issue", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_control_issue_template_preserves_human_gate_and_open_issue_boundary()
    {
        var template = ReadRepositoryFile("eng", "release", "preview-release-issue.md");

        Assert.Contains("<!-- gabcode-preview-release-control:v1 -->", template, StringComparison.Ordinal);
        Assert.Contains("## 🧪 Preview Release: v{{VERSION}}", template, StringComparison.Ordinal);
        Assert.Contains("**State backend:** github-issues", template, StringComparison.Ordinal);
        Assert.Contains("{{VERSION}}", template, StringComparison.Ordinal);
        Assert.Contains("{{SOURCE_COMMIT}}", template, StringComparison.Ordinal);
        Assert.Contains("{{WINDOWS_ARTIFACT}}", template, StringComparison.Ordinal);
        Assert.Contains("{{MACOS_ARTIFACT}}", template, StringComparison.Ordinal);
        Assert.Contains("exact version-named confirmation", template, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must remain open", template, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT CHECKED", template, StringComparison.Ordinal);
        Assert.DoesNotContain("close this issue", template, StringComparison.OrdinalIgnoreCase);

        var placeholders = Regex.Matches(template, "\\{\\{[A-Z0-9_]+\\}\\}").Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "{{MACOS_ARTIFACT}}", "{{MACOS_BYTES}}", "{{MACOS_SHA256}}", "{{SOURCE_COMMIT}}", "{{VERSION}}",
                "{{WINDOWS_ARTIFACT}}", "{{WINDOWS_BYTES}}", "{{WINDOWS_SHA256}}"
            }.Order(StringComparer.Ordinal),
            placeholders);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var path = Path.Combine(new[] { GetRepositoryRoot() }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"Missing preview-release contract file: {path}");
        return File.ReadAllText(path);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GabCode.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate the repository root from the test output directory.");
    }
}
