using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace GabCode.Windows.Tests.Packaging;

public sealed class WindowsPreviewPackagingSurfaceTests
{
    private const string PreviewVersionPattern = "x.y.z-preview.n";
    private const string MsiNamePattern = "gabCode-x.y.z-preview.n-windows-x64.msi";

    [Fact]
    public void Windows_preview_packaging_surface_declares_the_pinned_Wix_tool_and_entry_points()
    {
        var root = GetRepositoryRoot();
        var toolsPath = Path.Combine(root, ".config", "dotnet-tools.json");
        var buildScriptPath = Path.Combine(root, "eng", "release", "windows", "Build-Preview.ps1");
        var testScriptPath = Path.Combine(root, "eng", "release", "windows", "Test-Preview.ps1");

        Assert.True(File.Exists(toolsPath), $"Missing repository-local tool manifest: {toolsPath}");
        Assert.True(File.Exists(buildScriptPath), $"Missing deterministic preview build entry point: {buildScriptPath}");
        Assert.True(File.Exists(testScriptPath), $"Missing deterministic preview verification entry point: {testScriptPath}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(toolsPath));
        Assert.Equal("7.0.0", manifest.RootElement.GetProperty("tools").GetProperty("wix").GetProperty("version").GetString());
        Assert.Contains("dotnet publish", File.ReadAllText(buildScriptPath), StringComparison.Ordinal);
        Assert.Contains("wix build", File.ReadAllText(buildScriptPath), StringComparison.Ordinal);
        Assert.Contains("msiexec.exe", File.ReadAllText(testScriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Wix_source_and_generic_build_script_model_a_per_user_stable_upgrade_preview_package_without_a_desktop_shortcut()
    {
        var wixSourcePath = Path.Combine(GetRepositoryRoot(), "eng", "release", "windows", "GabCode.Preview.wxs");
        Assert.True(File.Exists(wixSourcePath), $"Missing WiX source: {wixSourcePath}");

        var document = XDocument.Load(wixSourcePath);
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var package = Assert.Single(document.Descendants(wix + "Package"));
        Assert.Equal("0.0.1", package.Attribute("Version")?.Value);
        Assert.Equal("perUser", package.Attribute("Scope")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(package.Attribute("UpgradeCode")?.Value));
        Assert.Equal("yes", package.Element(wix + "MajorUpgrade")?.Attribute("AllowSameVersionUpgrades")?.Value);
        Assert.DoesNotContain(document.Descendants(wix + "Shortcut"), shortcut =>
            string.Equals("DesktopFolder", shortcut.Attribute("Directory")?.Value, StringComparison.Ordinal));

        var buildScript = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "eng", "release", "windows", "Build-Preview.ps1"));
        Assert.Contains("New-StableGuid \"gabCode/windows-x64/product/$Version\"", buildScript, StringComparison.Ordinal);
        Assert.Contains("$package.SetAttribute('Version', $numericVersion)", buildScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_preview_documentation_states_the_generic_artifact_and_unsigned_support_boundary()
    {
        var documentationPath = Path.Combine(GetRepositoryRoot(), "Documentation", "release", "windows-unsigned-preview.md");
        Assert.True(File.Exists(documentationPath), $"Missing Windows preview documentation: {documentationPath}");

        var documentation = File.ReadAllText(documentationPath);
        Assert.Contains(PreviewVersionPattern, documentation, StringComparison.Ordinal);
        Assert.Contains(MsiNamePattern, documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.1-preview.1", documentation, StringComparison.Ordinal);
        Assert.Contains("unsigned", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows 11", documentation, StringComparison.Ordinal);
        Assert.Contains("MIT", documentation, StringComparison.Ordinal);
        Assert.Contains("NOTICE.md", documentation, StringComparison.Ordinal);
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
