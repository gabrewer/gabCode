using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceProjectCreatorTests
{
    [Fact]
    public async Task Create_publishes_descriptor_then_launches_new_instance()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode creator", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var descriptor = Path.Combine(root, "Demo.gabcode-workspace");
        Directory.CreateDirectory(repository);
        try
        {
            await RunGitAsync(repository, ["init", "--initial-branch", "main"]);
            await RunGitAsync(repository, ["config", "user.email", "test@example.invalid"]);
            await RunGitAsync(repository, ["config", "user.name", "Test"]);
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "fixture");
            await RunGitAsync(repository, ["add", "README.md"]);
            await RunGitAsync(repository, ["commit", "-m", "fixture"]);
            var launcher = new RecordingLauncher();
            var creator = new WorkspaceProjectCreator(new GitRepositoryValidator(), new WorkspaceFileStore(), launcher);

            var project = await creator.CreateAsync(descriptor, "Demo", repository, "main", launchNewWindow: true);

            Assert.Equal(repository, project.ProjectFolder);
            Assert.Single(launcher.Paths);
            Assert.Equal(descriptor, launcher.Paths[0]);
            Assert.True(File.Exists(descriptor));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor));
            Assert.Equal("Demo", json.RootElement.GetProperty("name").GetString());
            Assert.Equal("main", json.RootElement.GetProperty("project").GetProperty("mainBranch").GetString());
            Assert.False(json.RootElement.GetProperty("project").TryGetProperty("branch", out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class RecordingLauncher : IGabCodeInstanceLauncher
    {
        internal List<string> Paths { get; } = [];
        public void Launch(string workspacePath) => Paths.Add(workspacePath);
    }
}
