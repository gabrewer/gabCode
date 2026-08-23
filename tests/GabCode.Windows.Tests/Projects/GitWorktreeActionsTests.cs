using System.Diagnostics;
using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class GitWorktreeActionsTests
{
    [Fact]
    public async Task Creates_a_new_worktree_from_the_workspace_selected_branch_and_reconciles_it()
    {
        var root = CreateRoot("gabCode actions workspace-selected branch");
        var primary = Path.Combine(root, "primary");
        var target = Path.Combine(root, "wt", "wt-feature-demo");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");

            var entries = await new GitWorktreeDiscovery().CreateWorktreeAsync(
                root,
                baseBranch: "trunk",
                branch: "feature/demo",
                path: target,
                fetchLatest: false);

            var created = Assert.Single(entries, entry => entry.Branch == "feature/demo");
            Assert.Equal(Path.GetFullPath(target), created.Path);
            Assert.False(created.IsPrimary);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Creates_a_new_worktree_from_a_selected_linked_worktree_branch()
    {
        var root = CreateRoot("gabCode actions selected branch Ω");
        var primary = Path.Combine(root, "primary");
        var basePath = Path.Combine(root, "wt", "base");
        var target = Path.Combine(root, "wt", "child");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            await Git(primary, ["worktree", "add", "-b", "feature/base", basePath]);

            var entries = await new GitWorktreeDiscovery().CreateWorktreeAsync(
                root,
                baseBranch: "feature/base",
                branch: "feature/child",
                path: target,
                fetchLatest: false);

            Assert.Contains(entries, entry => entry.Branch == "feature/base" && entry.Path == Path.GetFullPath(basePath));
            Assert.Contains(entries, entry => entry.Branch == "feature/child" && entry.Path == Path.GetFullPath(target));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Rejects_creation_when_the_branch_is_already_attached()
    {
        var root = CreateRoot("gabCode actions attached branch");
        var primary = Path.Combine(root, "primary");
        var attached = Path.Combine(root, "wt", "attached");
        var target = Path.Combine(root, "wt", "duplicate");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            Directory.CreateDirectory(Path.GetDirectoryName(attached)!);
            await Git(primary, ["worktree", "add", "-b", "feature/existing", attached]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitWorktreeDiscovery().CreateWorktreeAsync(
                root,
                baseBranch: "trunk",
                branch: "feature/existing",
                path: target,
                fetchLatest: false));

            Assert.False(Directory.Exists(target));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Attaches_an_existing_unattached_local_branch_to_a_new_worktree()
    {
        var root = CreateRoot("gabCode actions existing branch");
        var primary = Path.Combine(root, "primary");
        var target = Path.Combine(root, "wt", "existing");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            await Git(primary, ["branch", "feature/existing"]);

            var entries = await new GitWorktreeDiscovery().CreateExistingWorktreeAsync(
                root,
                localBranch: "feature/existing",
                sourceRef: "feature/existing",
                path: target);

            Assert.Contains(entries, entry => entry.Branch == "feature/existing" && entry.Path == Path.GetFullPath(target));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Protects_the_primary_worktree_and_requires_force_for_dirty_removal()
    {
        var root = CreateRoot("gabCode actions guarded removal");
        var primary = Path.Combine(root, "primary");
        var feature = Path.Combine(root, "wt", "dirty");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
            await Git(primary, ["worktree", "add", "-b", "feature/dirty", feature]);
            await File.WriteAllTextAsync(Path.Combine(feature, "uncommitted.txt"), "keep me");

            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitWorktreeDiscovery().RemoveWorktreeAsync(
                root,
                feature,
                force: false,
                deleteLocalBranch: false,
                forceBranchDelete: false));

            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitWorktreeDiscovery().RemoveWorktreeAsync(
                root,
                primary,
                force: true,
                deleteLocalBranch: false,
                forceBranchDelete: false));

            Assert.True(Directory.Exists(feature));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Lists_local_branches_and_marks_attached_worktrees()
    {
        var root = CreateRoot("gabCode actions branch list");
        var primary = Path.Combine(root, "primary");
        var feature = Path.Combine(root, "wt", "listed");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            await Git(primary, ["branch", "feature/unattached"]);
            Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
            await Git(primary, ["worktree", "add", feature, "-b", "feature/attached"]);

            var branches = await new GitWorktreeDiscovery().ListBranchesAsync(root);

            Assert.Contains(branches, branch => branch.Name == "trunk" && !branch.IsRemote && branch.AttachedPath == Path.GetFullPath(primary));
            Assert.Contains(branches, branch => branch.Name == "feature/attached" && !branch.IsRemote && branch.AttachedPath == Path.GetFullPath(feature));
            Assert.Contains(branches, branch => branch.Name == "feature/unattached" && !branch.IsRemote && branch.AttachedPath is null);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Fetch_latest_uses_the_workspace_branch_remote_without_changing_the_existing_worktree()
    {
        var root = CreateRoot("gabCode actions fetch latest");
        var primary = Path.Combine(root, "primary");
        var remote = Path.Combine(root, "remote.git");
        var target = Path.Combine(root, "wt", "latest");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            await Git(root, ["init", "--bare", remote]);
            await Git(primary, ["remote", "add", "origin", remote]);
            await Git(primary, ["push", "-u", "origin", "trunk"]);

            var entries = await new GitWorktreeDiscovery().CreateWorktreeAsync(
                root,
                baseBranch: "trunk",
                branch: "feature/latest",
                path: target,
                fetchLatest: true);

            Assert.Contains(entries, entry => entry.Branch == "feature/latest" && entry.Path == Path.GetFullPath(target));
            Assert.Equal(Path.GetFullPath(primary), (await new GitWorktreeDiscovery().ResolveAsync(root, "trunk")));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Fetch_latest_bases_creation_on_a_new_remote_commit_not_a_stale_tracking_ref()
    {
        var root = CreateRoot("gabCode actions divergent fetch");
        var auxiliary = CreateRoot("gabCode actions divergent fetch remote");
        var primary = Path.Combine(root, "primary");
        var remote = Path.Combine(auxiliary, "remote.git");
        var other = Path.Combine(auxiliary, "other");
        var target = Path.Combine(root, "wt", "divergent");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            await Git(auxiliary, ["init", "--bare", remote]);
            await Git(primary, ["remote", "add", "origin", remote]);
            await Git(primary, ["push", "-u", "origin", "trunk"]);
            await Git(auxiliary, ["clone", "--branch", "trunk", remote, other]);
            await Git(other, ["config", "user.email", "test@example.invalid"]);
            await Git(other, ["config", "user.name", "Test"]);
            await File.AppendAllTextAsync(Path.Combine(other, "README.md"), "remote change");
            await Git(other, ["add", "."]);
            await Git(other, ["commit", "-m", "remote change"]);
            await Git(other, ["push"]);
            var remoteHead = (await GitOutput(other, ["rev-parse", "HEAD"])).Trim();
            var localHead = (await GitOutput(primary, ["rev-parse", "trunk"])).Trim();
            var staleTrackingHead = (await GitOutput(primary, ["rev-parse", "origin/trunk"])).Trim();
            Assert.NotEqual(remoteHead, staleTrackingHead);
            Assert.Equal(localHead, staleTrackingHead);

            await new GitWorktreeDiscovery().CreateWorktreeAsync(
                root,
                baseBranch: "trunk",
                branch: "feature/divergent",
                path: target,
                fetchLatest: true);

            Assert.Equal(remoteHead, (await GitOutput(primary, ["rev-parse", "feature/divergent"])).Trim());
            Assert.Equal(localHead, (await GitOutput(primary, ["rev-parse", "trunk"])).Trim());
        }
        finally { TryDelete(root); TryDelete(auxiliary); }
    }

    [Fact]
    public async Task Deletes_a_local_branch_only_after_successful_worktree_removal_when_requested()
    {
        var root = CreateRoot("gabCode actions branch deletion");
        var primary = Path.Combine(root, "primary");
        var feature = Path.Combine(root, "wt", "clean");
        Directory.CreateDirectory(primary);
        try
        {
            await InitializeRepository(primary, "trunk");
            Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
            await Git(primary, ["worktree", "add", "-b", "feature/remove-me", feature]);

            var entries = await new GitWorktreeDiscovery().RemoveWorktreeAsync(
                root,
                feature,
                force: false,
                deleteLocalBranch: true,
                forceBranchDelete: false);

            Assert.DoesNotContain(entries, entry => entry.Path == Path.GetFullPath(feature));
            var branches = await GitOutput(primary, ["branch", "--list", "feature/remove-me"]);
            Assert.DoesNotContain("feature/remove-me", branches, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    private static string CreateRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task InitializeRepository(string path, string branch)
    {
        await Git(path, ["init", "--initial-branch", branch]);
        await Git(path, ["config", "user.email", "test@example.invalid"]);
        await Git(path, ["config", "user.name", "Test"]);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "initial");
        await Git(path, ["add", "."]);
        await Git(path, ["commit", "-m", "initial"]);
    }

    private static async Task<string> GitOutput(string directory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static async Task Git(string directory, IReadOnlyList<string> arguments)
    {
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
