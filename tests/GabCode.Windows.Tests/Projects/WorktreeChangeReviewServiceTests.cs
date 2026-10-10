using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeChangeReviewServiceTests
{
    [Fact]
    public async Task Reads_real_worktree_index_working_files_and_branch_unique_commits_without_mutation()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.name", "Review Tests");
        await repo.Git("config", "user.email", "review@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "both ü.txt"), "base\n");
        await repo.Git("add", ".");
        await repo.Git("commit", "-m", "base");
        await repo.Git("switch", "-c", "feature/review");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "committed.txt"), "commit\n");
        await repo.Git("add", ".");
        await repo.Git("commit", "-m", "feature change");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "both ü.txt"), "staged\n");
        await repo.Git("add", "--", "both ü.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "both ü.txt"), "unstaged\n");
        Directory.CreateDirectory(Path.Combine(repo.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "nested", "new name.txt"), "untracked\n");
        var head = await repo.Git("rev-parse", "HEAD");
        var index = await repo.Git("ls-files", "-s");
        var before = await repo.Git("status", "--porcelain=v1", "-uall");

        var service = new WorktreeChangeReviewService();
        var snapshot = await service.GetSnapshotAsync(repo.Path, "trunk");

        Assert.False(snapshot.IsStale);
        Assert.Equal(["feature change"], snapshot.Commits.Select(c => c.Subject).ToArray());
        Assert.Contains("file changed", snapshot.Commits[0].Summary);
        Assert.Contains(snapshot.Working, x => x.Path == "both ü.txt" && x.Group == ReviewGroup.Staged);
        Assert.Contains(snapshot.Working, x => x.Path == "both ü.txt" && x.Group == ReviewGroup.Unstaged);
        Assert.Contains(snapshot.Working, x => x.Path == "nested/new name.txt" && x.Status == ReviewStatus.Untracked);
        var files = await service.GetCommitFilesAsync(repo.Path, snapshot.Commits[0].Sha);
        Assert.Contains(files, x => x.Path == "committed.txt" && x.Status == ReviewStatus.Added);
        var staged = Assert.Single(snapshot.Working, x => x.Path == "both ü.txt" && x.Group == ReviewGroup.Staged);
        var unstaged = Assert.Single(snapshot.Working, x => x.Path == "both ü.txt" && x.Group == ReviewGroup.Unstaged);
        Assert.Contains("+staged", (await service.GetDiffAsync(repo.Path, staged)).Patch);
        Assert.Contains("+unstaged", (await service.GetDiffAsync(repo.Path, unstaged)).Patch);
        Assert.Equal(head, await repo.Git("rev-parse", "HEAD"));
        Assert.Equal(index, await repo.Git("ls-files", "-s"));
        Assert.Equal(before, await repo.Git("status", "--porcelain=v1", "-uall"));
    }

    [Fact]
    public async Task Missing_configured_local_main_is_unavailable_not_an_empty_history()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "first.txt"), "x");
        await repo.Git("add", ".");
        await repo.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "first");
        var result = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "missing");
        Assert.True(result.IsStale);
        Assert.NotNull(result.Head);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Shared_fixture_cases_are_enumerated_with_stable_expected_outcomes()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var cases = json.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(35, cases.Length);
        Assert.Equal(35, cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(new[] { "working", "history", "diff", "reconcile" }, cases.Select(c => c.GetProperty("kind").GetString()).Distinct().ToArray());
        foreach (var row in cases) Assert.True(row.TryGetProperty("expected", out _), row.GetProperty("id").GetString());
    }

    [Fact]
    public void All_shared_working_status_cases_match_normalized_target_groups_paths_and_status()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/worktree-change-review/cases.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() != "working") continue;
            var given = row.GetProperty("given").GetProperty("status");
            var raw = string.Concat(given.EnumerateArray().Select(s =>
                s.GetProperty("xy").GetString()!.Replace('.', ' ') + " " + s.GetProperty("path").GetString() + "\0" +
                (s.TryGetProperty("oldPath", out var old) ? old.GetString() + "\0" : "")));
            var actual = WorktreeChangeReviewService.ParseStatus(raw);
            var expected = row.GetProperty("expected").GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Count);
            foreach (var (entry, index) in actual.Select((entry, index) => (entry, index)))
            {
                var outcome = expected[index];
                Assert.Equal(outcome.GetProperty("path").GetString(), entry.Path);
                Assert.Equal(outcome.GetProperty("group").GetString(), entry.Group.ToString());
                Assert.Equal(outcome.GetProperty("status").GetString(), entry.Status.ToString().ToLowerInvariant());
                if (outcome.TryGetProperty("oldPath", out var old)) Assert.Equal(old.GetString(), entry.OldPath);
            }
        }
    }

    private sealed class ReviewRepository : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gabCode review Ω " + Guid.NewGuid().ToString("N"));
        internal ReviewRepository() => Directory.CreateDirectory(Path);
        internal async Task<string> Git(params string[] arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {await error}");
            return (await output).Trim();
        }
        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
