using System.IO;

namespace GabCode.Windows.Tests.Packaging;

public sealed class WindowsPreviewPreparationSurfaceTests
{
    [Fact]
    public void Windows_preview_preparation_entry_point_builds_verifies_and_writes_only_the_versioned_artifact_pair()
    {
        var root = GetRepositoryRoot();
        var preparationPath = Path.Combine(root, "eng", "release", "windows", "Prepare-Preview.ps1");

        Assert.True(File.Exists(preparationPath), $"Missing Windows preview preparation entry point: {preparationPath}");
        var preparation = File.ReadAllText(preparationPath);
        Assert.Contains("[string] $Version", preparation, StringComparison.Ordinal);
        Assert.Contains("Build-Preview.ps1", preparation, StringComparison.Ordinal);
        Assert.Contains("Test-Preview.ps1", preparation, StringComparison.Ordinal);
        Assert.Contains("preview-evidence.schema.json", preparation, StringComparison.Ordinal);
        Assert.Contains("gabCode-$Version-windows-x64.msi", preparation, StringComparison.Ordinal);
        Assert.Contains("gabCode-$Version-windows-x64.evidence.json", preparation, StringComparison.Ordinal);
        Assert.Contains("origin/main", preparation, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", preparation, StringComparison.Ordinal);
        Assert.Contains("Move-Item", preparation, StringComparison.Ordinal);
        Assert.Contains("Refusing to replace an existing gabCode installation", File.ReadAllText(Path.Combine(root, "eng", "release", "windows", "Test-Preview.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_build_prompt_is_explicit_host_guarded_and_cannot_publish()
    {
        var promptPath = Path.Combine(GetRepositoryRoot(), ".pi", "prompts", "build-preview-msi.md");

        Assert.True(File.Exists(promptPath), $"Missing Windows preview build prompt: {promptPath}");
        var prompt = File.ReadAllText(promptPath);
        Assert.Contains("/build-preview-msi", prompt, StringComparison.Ordinal);
        Assert.Contains("Windows", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop", prompt, StringComparison.Ordinal);
        Assert.Contains("Prepare-Preview.ps1", prompt, StringComparison.Ordinal);
        Assert.Contains("must not create", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub issue", prompt, StringComparison.Ordinal);
        Assert.Contains("release", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Publish-Preview.ps1", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_windows_release_documentation_and_preparation_contract_keep_release_and_transfer_out_of_scope()
    {
        var root = GetRepositoryRoot();
        var documentation = File.ReadAllText(Path.Combine(root, "Documentation", "release", "windows-unsigned-preview.md"));
        var workflow = File.ReadAllText(Path.Combine(root, "Documentation", "release", "local-preview-workflow.md"));

        Assert.Contains("Prepare-Preview.ps1", documentation, StringComparison.Ordinal);
        Assert.Contains("x.y.z-preview", documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("x.y.z-preview.n", documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.1-preview.1", documentation, StringComparison.Ordinal);
        Assert.Contains("It creates no GitHub issue, tag, or release.", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow does not configure or automate transport", workflow, StringComparison.OrdinalIgnoreCase);
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
