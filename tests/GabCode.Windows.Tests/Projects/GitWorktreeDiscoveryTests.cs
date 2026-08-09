using System.Diagnostics;
using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class GitWorktreeDiscoveryTests
{
    [Fact]
    public async Task Resolves_registered_branch_to_its_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode worktrees", Guid.NewGuid().ToString("N"));
        var main = Path.Combine(root, "main");
        var feature = Path.Combine(root, "wt", "feature");
        Directory.CreateDirectory(main);
        try
        {
            await Git(main, ["init", "--initial-branch", "main"]);
            await Git(main, ["config", "user.email", "test@example.invalid"]);
            await Git(main, ["config", "user.name", "Test"]);
            await File.WriteAllTextAsync(Path.Combine(main, "README.md"), "x");
            await Git(main, ["add", "."]);
            await Git(main, ["commit", "-m", "initial"]);
            Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
            await Git(main, ["worktree", "add", "-b", "feature/demo", feature]);

            var worktree = await new GitWorktreeDiscovery().ResolveAsync(root, "feature/demo");

            Assert.Equal(Path.GetFullPath(feature), worktree);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static async Task Git(string directory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
