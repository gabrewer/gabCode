using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeChangeReviewBoundaryTests
{
    [Fact]
    public async Task Git_pathspec_magic_is_a_literal_filename_not_a_pattern()
    {
        using var repo = new Repo();
        await repo.Initialize();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "[a].txt"), "literal\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "other\n");
        await repo.Git("add", ".");
        await repo.Commit("files");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "[a].txt"), "literal-new\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "other-new\n");
        var diff = await new WorktreeChangeReviewService().GetDiffAsync(repo.Path, new("[a].txt", null, ReviewGroup.Unstaged, ReviewStatus.Modified));
        Assert.Contains("+literal-new", diff.Patch);
        Assert.DoesNotContain("+other-new", diff.Patch);
    }

    [Fact]
    public async Task Root_rename_binary_untracked_and_conflict_diff_states_are_explicit()
    {
        using var repo = new Repo();
        await repo.Initialize();
        var service = new WorktreeChangeReviewService();
        var root = await repo.Git("rev-parse", "HEAD");
        var rootFile = Assert.Single(await service.GetCommitFilesAsync(repo.Path, root));
        Assert.Equal(ReviewStatus.Added, rootFile.Status);
        Assert.Contains("empty tree", rootFile.Comparison);
        Assert.Contains("+base", (await service.GetDiffAsync(repo.Path, rootFile)).Patch);
        await repo.Git("mv", "base.txt", "renamed ü.txt");
        await repo.Commit("renamed");
        var renamed = Assert.Single(await service.GetCommitFilesAsync(repo.Path, await repo.Git("rev-parse", "HEAD")));
        Assert.Equal("base.txt", renamed.OldPath);
        Assert.Equal(ReviewStatus.Renamed, renamed.Status);
        Assert.Contains("first parent", renamed.Comparison);
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "binary.bin"), [1, 0, 2]);
        Assert.Equal(ReviewDiffState.Binary, (await service.GetDiffAsync(repo.Path, new("binary.bin", null, ReviewGroup.Unstaged, ReviewStatus.Untracked))).State);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "new.txt"), "one\n");
        var untracked = await service.GetDiffAsync(repo.Path, new("new.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked));
        Assert.Contains("@@ -0,0 +1,1 @@", untracked.Patch);
        Assert.Equal(ReviewDiffState.Conflict, (await service.GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Unstaged, ReviewStatus.Conflicted))).State);
    }

    [Fact]
    public async Task Cancellation_and_timeout_are_distinguished_from_success()
    {
        using var repo = new Repo();
        await repo.Initialize();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "trunk", cancel.Token));
        var fake = Path.Combine(repo.Path, "hang.cmd");
        await File.WriteAllTextAsync(fake, "@echo off\r\nping -n 10 127.0.0.1 > nul\r\n");
        var result = await new WorktreeChangeReviewService(fake, TimeSpan.FromMilliseconds(100)).GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Staged, ReviewStatus.Modified));
        Assert.Equal(ReviewDiffState.Timeout, result.State);
    }

    [Fact]
    public async Task All_explicit_shared_history_DAGs_are_observed_through_real_git()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"))));
        foreach (var row in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() != "history" || !row.GetProperty("given").TryGetProperty("commits", out var commits)) continue;
            using var repo = new Repo(); await repo.Initialize();
            var tree = await repo.Git("rev-parse", "HEAD^{tree}");
            var ids = new Dictionary<string, string>();
            foreach (var commit in commits.EnumerateArray())
            {
                var args = new List<string> { "commit-tree", tree, "-m", commit.GetProperty("id").GetString()! };
                foreach (var parent in commit.GetProperty("parents").EnumerateArray()) args.AddRange(["-p", ids[parent.GetString()!]]);
                ids.Add(commit.GetProperty("id").GetString()!, await repo.Git(args.ToArray()));
            }
            var given = row.GetProperty("given");
            await repo.Git("update-ref", "refs/heads/trunk", ids[given.GetProperty("mainReachable")[0].GetString()!]);
            await repo.Git("checkout", "--detach", ids[given.GetProperty("selectedHead").GetString()!]);
            var actual = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "trunk");
            Assert.False(actual.IsStale, actual.Error);
            Assert.Equal(row.GetProperty("expected").GetProperty("unique").EnumerateArray().Select(s => ids[s.GetString()!]), actual.Commits.Select(c => c.Sha));
        }
    }

    [Fact]
    public async Task Large_history_pages_are_bounded_and_recomputed_when_main_moves()
    {
        using var repo = new Repo(); await repo.Initialize();
        var main = await repo.Git("rev-parse", "HEAD");
        var tree = await repo.Git("rev-parse", "HEAD^{tree}");
        var shas = new List<string>();
        var parent = main;
        for (var i = 0; i < 250; i++)
        {
            parent = await repo.Git("commit-tree", tree, "-p", parent, "-m", $"review-{i:D3}");
            shas.Add(parent);
        }
        await repo.Git("update-ref", "refs/heads/review", parent);
        await repo.Git("checkout", "review");
        var service = new WorktreeChangeReviewService();
        var first = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(first.IsStale, first.Error);
        Assert.Equal(100, first.Commits.Count);
        Assert.Equal(shas[^1], first.Commits[0].Sha);
        var second = await service.GetCommitsAsync(repo.Path, first.Head!, first.MainHead!, 100);
        Assert.Equal(100, second.Count);
        var third = await service.GetCommitsAsync(repo.Path, first.Head!, first.MainHead!, 200);
        Assert.Equal(50, third.Count);
        Assert.Equal(shas.AsEnumerable().Reverse(), first.Commits.Concat(second).Concat(third).Select(commit => commit.Sha));
        await repo.Git("update-ref", "refs/heads/trunk", shas[200]);
        var updated = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.NotEqual(first.MainHead, updated.MainHead);
        Assert.Equal(shas.AsEnumerable().Skip(201).Reverse(), updated.Commits.Select(commit => commit.Sha));
        Assert.True(await service.IsCommitUniqueToHeadAsync(repo.Path, shas[201], updated.Head!, updated.MainHead!));
        Assert.False(await service.IsCommitUniqueToHeadAsync(repo.Path, shas[200], updated.Head!, updated.MainHead!));
    }

    [Fact]
    public async Task Review_diff_classifies_shared_states_against_real_files_and_controlled_failures()
    {
        using var repo = new Repo(); await repo.Initialize();
        var service = new WorktreeChangeReviewService();
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var cases = json.RootElement.GetProperty("cases").EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "diff").ToArray();
        Assert.Equal(12, cases.Length);
        var seen = new List<string>();
        foreach (var row in cases)
        {
            var id = row.GetProperty("id").GetString()!;
            ReviewDiff actual;
            switch (id)
            {
                case "staged-one-sided-add-diff":
                    await File.WriteAllTextAsync(System.IO.Path.Combine(repo.Path, "new.txt"), "hello\n"); await repo.Git("add", "new.txt");
                    actual = await service.GetDiffAsync(repo.Path, new("new.txt", null, ReviewGroup.Staged, ReviewStatus.Added)); break;
                case "unstaged-deletion-is-one-sided":
                    File.Delete(System.IO.Path.Combine(repo.Path, "base.txt"));
                    actual = await service.GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Unstaged, ReviewStatus.Deleted)); break;
                case "untracked-content-compared-with-empty":
                    await File.WriteAllTextAsync(System.IO.Path.Combine(repo.Path, "notes.txt"), "notes\n");
                    actual = await service.GetDiffAsync(repo.Path, new("notes.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked)); break;
                case "conflict-is-not-normal-patch":
                    actual = await service.GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Unstaged, ReviewStatus.Conflicted)); break;
                case "binary-diff-is-explicit":
                    await File.WriteAllBytesAsync(System.IO.Path.Combine(repo.Path, "picture.png"), [1, 0, 2]); await repo.Git("add", "picture.png");
                    actual = await service.GetDiffAsync(repo.Path, new("picture.png", null, ReviewGroup.Staged, ReviewStatus.Added)); break;
                case "oversized-diff-is-explicit":
                    await File.WriteAllTextAsync(System.IO.Path.Combine(repo.Path, "big.txt"), new string('x', 2 * 1024 * 1024 + 1));
                    actual = await service.GetDiffAsync(repo.Path, new("big.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked)); break;
                case "truncated-and-timed-out-are-not-complete-patches":
                    // Controlled timeout from a child process; truncated output is validated by a separate burst test.
                    var fake = System.IO.Path.Combine(repo.Path, "hang-diff.cmd");
                    await File.WriteAllTextAsync(fake, "@echo off\r\nping -n 10 127.0.0.1 > nul\r\n");
                    actual = await new WorktreeChangeReviewService(fake, TimeSpan.FromMilliseconds(100)).GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Staged, ReviewStatus.Modified));
                    Assert.Equal(ReviewDiffState.Timeout, actual.State); seen.Add(id); continue;
                case "unavailable-git-diff-preserves-error":
                    actual = await new WorktreeChangeReviewService("no-such-gabcode-git.exe").GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Staged, ReviewStatus.Modified)); break;
                case "root-file-diff-compares-empty-tree":
                    actual = await service.GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Commit, ReviewStatus.Added, await repo.Git("rev-list", "--max-parents=0", "HEAD"))); break;
                case "commit-rename-keeps-both-paths":
                    using (var renamed = new Repo())
                    {
                        await renamed.Initialize(); await renamed.Git("mv", "base.txt", "new.txt"); await renamed.Commit("rename");
                        actual = await service.GetDiffAsync(renamed.Path, new("new.txt", "base.txt", ReviewGroup.Commit, ReviewStatus.Renamed, await renamed.Git("rev-parse", "HEAD")));
                    }
                    break;
                case "historical-deletion-does-not-open-current-file":
                    using (var deleted = new Repo())
                    {
                        await deleted.Initialize(); File.Delete(System.IO.Path.Combine(deleted.Path, "base.txt")); await deleted.Git("add", "-u"); await deleted.Commit("delete");
                        actual = await service.GetDiffAsync(deleted.Path, new("base.txt", null, ReviewGroup.Commit, ReviewStatus.Deleted, await deleted.Git("rev-parse", "HEAD")));
                    }
                    break;
                case "merge-file-diff-compares-first-parent":
                    using (var merged = new Repo())
                    {
                        await merged.Initialize(); await merged.Git("switch", "-c", "side");
                        await File.WriteAllTextAsync(System.IO.Path.Combine(merged.Path, "side.txt"), "side\n"); await merged.Git("add", "side.txt"); await merged.Commit("side");
                        await merged.Git("switch", "trunk");
                        await File.WriteAllTextAsync(System.IO.Path.Combine(merged.Path, "main.txt"), "main\n"); await merged.Git("add", "main.txt"); await merged.Commit("main");
                        await merged.Git("merge", "--no-ff", "side", "-m", "merge");
                        actual = await service.GetDiffAsync(merged.Path, new("side.txt", null, ReviewGroup.Commit, ReviewStatus.Added, await merged.Git("rev-parse", "HEAD")));
                    }
                    break;
                default: throw new InvalidOperationException($"Unconsumed shared diff case: {id}");
            }
            var expected = row.GetProperty("expected").GetProperty("state").GetString();
            Assert.Equal(expected, actual.State.ToString().ToLowerInvariant());
            if (actual.State == ReviewDiffState.Patch) Assert.False(string.IsNullOrWhiteSpace(actual.Patch));
            seen.Add(id);
        }
        Assert.Equal(cases.Select(c => c.GetProperty("id").GetString()), seen);
    }

    [Fact]
    public async Task Oversized_child_output_is_explicitly_truncated_and_does_not_publish_partial_patch()
    {
        using var repo = new Repo(); await repo.Initialize();
        var fake = System.IO.Path.Combine(repo.Path, "noisy.cmd");
        await File.WriteAllTextAsync(fake, "@echo off\r\nfor /L %%i in (1,1,6000) do @echo " + new string('x', 500) + "\r\n");
        var diff = await new WorktreeChangeReviewService(fake, TimeSpan.FromSeconds(3)).GetDiffAsync(repo.Path, new("base.txt", null, ReviewGroup.Staged, ReviewStatus.Modified));
        Assert.Equal(ReviewDiffState.Truncated, diff.State);
        Assert.Null(diff.Patch);
    }

    [Fact]
    public async Task Pages_are_pinned_to_object_ids_and_detached_head_is_supported()
    {
        using var repo = new Repo(); await repo.Initialize();
        var main = await repo.Git("rev-parse", "HEAD");
        var tree = await repo.Git("rev-parse", "HEAD^{tree}");
        var head = main;
        for (var i = 1; i <= 250; i++) head = await repo.Git("commit-tree", tree, "-p", head, "-m", "tip" + i);
        await repo.Git("checkout", "--detach", head);
        var service = new WorktreeChangeReviewService();
        var snapshot = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(snapshot.IsStale, snapshot.Error);
        var second = await service.GetCommitsAsync(repo.Path, snapshot.Head!, snapshot.MainHead!, 100);
        var third = await service.GetCommitsAsync(repo.Path, snapshot.Head!, snapshot.MainHead!, 200);
        Assert.Equal(100, snapshot.Commits.Count); Assert.Equal(100, second.Count); Assert.Equal(50, third.Count);
        Assert.Equal(250, snapshot.Commits.Concat(second).Concat(third).Select(c => c.Sha).Distinct().Count());
        await repo.Git("update-ref", "refs/heads/trunk", head);
        Assert.Empty((await service.GetSnapshotAsync(repo.Path, "trunk")).Commits);
        Assert.Equal(second.Select(c => c.Sha), (await service.GetCommitsAsync(repo.Path, head, main, 100)).Select(c => c.Sha));
    }

    private sealed class Repo : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gabcode boundaries Ω " + Guid.NewGuid().ToString("N"));
        internal Repo() => Directory.CreateDirectory(Path);
        internal async Task Initialize()
        {
            await Git("init", "-b", "trunk");
            await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "base.txt"), "base\n"); await Git("add", "."); await Commit("base");
        }
        internal Task<string> Commit(string subject) => Git("commit", "-m", subject);
        internal async Task<string> Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, await error); return (await output).Trim();
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
    }
}
