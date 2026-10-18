using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeChangeReviewServiceTests
{
    [Fact]
    public async Task Staged_document_uses_raw_committed_and_index_bytes_never_current_worktree()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        const string path = "both ü.txt";
        await File.WriteAllTextAsync(Path.Combine(repo.Path, path), "committed\n");
        await repo.Git("add", "--", path);
        await repo.Git("commit", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, path), "staged\n");
        await repo.Git("add", "--", path);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, path), "worktree-only\n");
        var service = new WorktreeChangeReviewService();
        var selected = Assert.Single((await service.GetSnapshotAsync(repo.Path, "trunk")).Working,
            item => item.Group == ReviewGroup.Staged && item.Path == path);
        var document = await service.GetStagedDocumentAsync(repo.Path, selected);
        Assert.Equal("committed\n", document.Before);
        Assert.Equal("staged\n", document.After);
        Assert.Equal("HEAD → index", document.Comparison);
        Assert.DoesNotContain("worktree-only", document.After);
        Assert.Equal("worktree-only\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, path)));
    }

    [Fact]
    public async Task Staged_byte_facts_and_raw_filter_label_are_reported_without_running_filter()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "core.autocrlf", "false");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "text.txt"), [0xEF, 0xBB, 0xBF, (byte)'a', 13, 10]);
        await repo.Git("add", "text.txt");
        await repo.Git("commit", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".gitattributes"), "text.txt filter=nonexistent\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "text.txt"), "b");
        await repo.Git("add", "text.txt");
        var document = await new WorktreeChangeReviewService().GetStagedDocumentAsync(repo.Path,
            new ReviewFile("text.txt", null, ReviewGroup.Staged, ReviewStatus.Modified));
        Assert.Equal("a\r\n", document.Before);
        Assert.Equal("b", document.After);
        Assert.True(document.BeforeBom);
        Assert.False(document.AfterBom);
        Assert.Equal("CRLF", document.BeforeEol);
        Assert.Equal("none", document.AfterEol);
        Assert.True(document.BeforeFinalNewline);
        Assert.False(document.AfterFinalNewline);
        Assert.True(document.RawFilter);
    }

    [Fact]
    public async Task Invalid_UTF8_and_symlink_stage_are_not_rendered_as_text()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "text.txt"), "ok\n");
        await repo.Git("add", "text.txt");
        await repo.Git("commit", "-m", "base");
        await File.WriteAllBytesAsync(Path.Combine(repo.Path, "text.txt"), [0xFF, 0xFE, 0x61, 0]);
        await repo.Git("add", "text.txt");
        var service = new WorktreeChangeReviewService();
        var file = new ReviewFile("text.txt", null, ReviewGroup.Staged, ReviewStatus.Modified);
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => service.GetStagedDocumentAsync(repo.Path, file));
        Assert.Contains("unsupported text encoding", invalid.Message, StringComparison.Ordinal);
        await repo.Git("rm", "-f", "text.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "link-target"), "target.txt");
        var oid = await repo.Git("hash-object", "-w", "link-target");
        await repo.Git("update-index", "--add", "--cacheinfo", $"120000,{oid.Trim()},text.txt");
        var unsafeEntry = await Assert.ThrowsAsync<InvalidDataException>(() => service.GetStagedDocumentAsync(repo.Path, file));
        Assert.Contains("not a regular file", unsafeEntry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_documents_use_selected_Git_basis_for_unstaged_untracked_history_and_branch()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "core.autocrlf", "false");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "base\n");
        await repo.Git("add", "file.txt");
        await repo.Git("commit", "-m", "base");
        await repo.Git("switch", "-c", "feature/review");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "historical\n");
        await repo.Git("commit", "-am", "history");
        var selectedSha = (await repo.Git("rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "staged\n");
        await repo.Git("add", "file.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "today\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "untracked.txt"), "");
        var service = new WorktreeChangeReviewService();
        var unstaged = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("file.txt", null, ReviewGroup.Unstaged, ReviewStatus.Modified), "trunk");
        Assert.Equal("staged\n", unstaged.Before);
        Assert.Equal("today\n", unstaged.After);
        Assert.Equal("index → worktree", unstaged.Comparison);
        var untracked = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("untracked.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked), "trunk");
        Assert.Equal("", untracked.Before);
        Assert.Equal("", untracked.After);
        Assert.False(untracked.BeforeExists);
        Assert.True(untracked.AfterExists);
        var historical = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("file.txt", null, ReviewGroup.Commit, ReviewStatus.Modified, selectedSha), "trunk");
        Assert.Equal("base\n", historical.Before);
        Assert.Equal("historical\n", historical.After);
        Assert.Equal("first parent → selected commit", historical.Comparison);
        var branch = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("file.txt", null, ReviewGroup.Branch, ReviewStatus.Modified), "trunk");
        Assert.Equal("base\n", branch.Before);
        Assert.Equal("historical\n", branch.After);
        Assert.Equal("base → tip", branch.Comparison);
        Assert.Equal((await repo.Git("rev-parse", "trunk")).Trim(), branch.BaseRevision);
        Assert.Equal(selectedSha, branch.TargetRevision);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("file.txt", null, ReviewGroup.Branch, ReviewStatus.Modified), "missing"));
    }

    [Fact]
    public async Task Branch_file_rows_share_verified_actual_base_and_tip_with_selected_document()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "main\n");
        await repo.Git("add", "file.txt");
        await repo.Git("commit", "-m", "base");
        var mainSha = (await repo.Git("rev-parse", "trunk")).Trim();
        await repo.Git("switch", "-c", "feature/review");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "tip\n");
        await repo.Git("commit", "-am", "change");
        var tipSha = (await repo.Git("rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "today\n");
        var service = new WorktreeChangeReviewService();
        var comparison = await service.GetBranchFilesAsync(repo.Path, "trunk", tipSha, mainSha);
        Assert.Equal(mainSha, comparison.BaseRevision);
        Assert.Equal(tipSha, comparison.TargetRevision);
        var file = Assert.Single(comparison.Files);
        Assert.Equal(ReviewGroup.Branch, file.Group);
        Assert.Equal("file.txt", file.Path);
        Assert.Contains(mainSha[..12], file.Comparison);
        Assert.Contains(tipSha[..12], file.Comparison);
        var document = await service.GetReviewDocumentAsync(repo.Path, file, "trunk");
        Assert.Equal("main\n", document.Before);
        Assert.Equal("tip\n", document.After);
        await repo.Git("branch", "-f", "trunk", tipSha);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReviewDocumentAsync(repo.Path, file, "trunk"));
    }

    [Fact]
    public async Task Worktree_document_does_not_read_outside_selected_root()
    {
        using var repo = new ReviewRepository();
        var service = new WorktreeChangeReviewService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("../private.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked), "trunk"));
    }

    [Fact]
    public async Task Snapshot_status_does_not_execute_configured_clean_filter()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".gitattributes"), "tracked.txt filter=sentinel\n");
        await repo.Git("config", "filter.sentinel.clean", "cmd /d /c echo invoked>marker-sentinel.txt");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "changed\n");
        var marker = Path.Combine(repo.Path, "marker-sentinel.txt");
        await repo.Git("diff", "--", "tracked.txt");
        Assert.True(File.Exists(marker));
        File.Delete(marker);
        var result = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(File.Exists(marker));
        Assert.Contains(result.Working, file => file.Path == "tracked.txt");
    }

    [Fact]
    public async Task Unborn_snapshot_retains_staged_file_but_no_history_base()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "new.txt"), "new\n");
        await repo.Git("add", "new.txt");
        var snapshot = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "trunk");
        Assert.Null(snapshot.Head);
        Assert.False(snapshot.IsStale);
        Assert.Contains("no commit yet", snapshot.Error);
        Assert.Contains(snapshot.Working, file => file.Group == ReviewGroup.Staged && file.Path == "new.txt");
    }

    [Fact]
    public async Task Deleted_and_untracked_rows_reject_stale_file_or_index_truth()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "base");
        File.Delete(Path.Combine(repo.Path, "tracked.txt"));
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "reappeared\n");
        var service = new WorktreeChangeReviewService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("tracked.txt", null, ReviewGroup.Unstaged, ReviewStatus.Deleted), "trunk"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("tracked.txt", null, ReviewGroup.Unstaged, ReviewStatus.Untracked), "trunk"));
    }

    [Fact]
    public async Task Staged_add_rename_delete_and_unborn_HEAD_have_distinct_empty_sides()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "old.txt"), "value\n");
        await repo.Git("add", "old.txt");
        var service = new WorktreeChangeReviewService();
        var added = await service.GetStagedDocumentAsync(repo.Path,
            new ReviewFile("old.txt", null, ReviewGroup.Staged, ReviewStatus.Added));
        Assert.Equal("", added.Before);
        Assert.Equal("value\n", added.After);
        await repo.Git("commit", "-m", "base");
        await repo.Git("mv", "old.txt", "new.txt");
        var renamed = await service.GetStagedDocumentAsync(repo.Path,
            new ReviewFile("new.txt", "old.txt", ReviewGroup.Staged, ReviewStatus.Renamed));
        Assert.Equal("value\n", renamed.Before);
        Assert.Equal("value\n", renamed.After);
        await repo.Git("commit", "-m", "rename");
        await repo.Git("rm", "new.txt");
        var deleted = await service.GetStagedDocumentAsync(repo.Path,
            new ReviewFile("new.txt", null, ReviewGroup.Staged, ReviewStatus.Deleted));
        Assert.Equal("value\n", deleted.Before);
        Assert.Equal("", deleted.After);
    }

    [Fact]
    public async Task Root_rename_and_deleted_history_use_selected_object_paths()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "old.txt"), "first\n");
        await repo.Git("add", "old.txt");
        await repo.Git("commit", "-m", "root");
        var rootSha = (await repo.Git("rev-parse", "HEAD")).Trim();
        var service = new WorktreeChangeReviewService();
        var initial = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("old.txt", null, ReviewGroup.Commit, ReviewStatus.Added, rootSha), "trunk");
        Assert.Equal("", initial.Before);
        Assert.Equal("first\n", initial.After);
        Assert.Equal("empty tree → selected commit", initial.Comparison);
        Assert.False(initial.BeforeExists);
        await repo.Git("mv", "old.txt", "new.txt");
        await repo.Git("commit", "-m", "rename");
        var renameSha = (await repo.Git("rev-parse", "HEAD")).Trim();
        var rename = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("new.txt", "old.txt", ReviewGroup.Commit, ReviewStatus.Renamed, renameSha), "trunk");
        Assert.Equal("first\n", rename.Before);
        Assert.Equal("first\n", rename.After);
        await repo.Git("rm", "new.txt");
        await repo.Git("commit", "-m", "delete");
        var deleteSha = (await repo.Git("rev-parse", "HEAD")).Trim();
        var deleted = await service.GetReviewDocumentAsync(repo.Path,
            new ReviewFile("new.txt", null, ReviewGroup.Commit, ReviewStatus.Deleted, deleteSha), "trunk");
        Assert.Equal("first\n", deleted.Before);
        Assert.Equal("", deleted.After);
        Assert.False(deleted.AfterExists);
    }

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
