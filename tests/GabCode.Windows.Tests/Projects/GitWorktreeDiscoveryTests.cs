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

    [Fact]
    public async Task Discovers_branch_bearing_worktrees_with_primary_porcelain_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode worktrees Ω", Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(root, "primary trunk");
        var alpha = Path.Combine(root, "wt", "alpha");
        var zulu = Path.Combine(root, "wt", "zulu");
        var detached = Path.Combine(root, "wt", "detached");
        Directory.CreateDirectory(primary);
        try
        {
            await Git(primary, ["init", "--initial-branch", "trunk"]);
            await Git(primary, ["config", "user.email", "test@example.invalid"]);
            await Git(primary, ["config", "user.name", "Test"]);
            await File.WriteAllTextAsync(Path.Combine(primary, "README.md"), "x");
            await Git(primary, ["add", "."]);
            await Git(primary, ["commit", "-m", "initial"]);
            Directory.CreateDirectory(Path.GetDirectoryName(alpha)!);
            await Git(primary, ["worktree", "add", "-b", "feature/alpha", alpha]);
            await Git(primary, ["worktree", "add", "-b", "feature/zulu", zulu]);
            await Git(primary, ["worktree", "add", "--detach", detached, "HEAD"]);

            var entries = await new GitWorktreeDiscovery().DiscoverEntriesAsync(root);

            Assert.Equal(3, entries.Count);
            Assert.Equal(Path.GetFullPath(primary), entries[0].Path);
            Assert.Equal("trunk", entries[0].Branch);
            Assert.True(entries[0].IsPrimary);
            Assert.Contains(entries, entry => entry.Branch == "feature/alpha" && entry.Path == Path.GetFullPath(alpha));
            Assert.Contains(entries, entry => entry.Branch == "feature/zulu" && entry.Path == Path.GetFullPath(zulu));
            Assert.DoesNotContain(entries, entry => entry.Path == Path.GetFullPath(detached));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Drains_noisy_stdout_and_stderr_without_retaining_unbounded_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode noisy discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, ["init", "--initial-branch", "trunk"]);
            var fakeGit = Path.Combine(root, "noisy-git.cmd");
            await File.WriteAllTextAsync(fakeGit, "@echo off\r\necho worktree %CD%\r\necho HEAD 000\r\necho branch refs/heads/trunk\r\necho.\r\nfor /L %%i in (1,1,5000) do @echo 012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789\r\nfor /L %%i in (1,1,5000) do @echo 012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789 1>&2\r\n");

            var entries = await new GitWorktreeDiscovery(fakeGit, TimeSpan.FromSeconds(5)).DiscoverEntriesAsync(root);

            Assert.Single(entries);
            Assert.Equal("trunk", entries[0].Branch);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Times_out_and_stops_a_hung_git_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "gabCode hung discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, ["init", "--initial-branch", "trunk"]);
            var fakeGit = Path.Combine(root, "hung-git.cmd");
            await File.WriteAllTextAsync(fakeGit, "@echo off\r\nping -n 10 127.0.0.1 > nul\r\n");

            await Assert.ThrowsAsync<TimeoutException>(() => new GitWorktreeDiscovery(fakeGit, TimeSpan.FromMilliseconds(100)).DiscoverEntriesAsync(root));
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
