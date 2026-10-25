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

    [Theory]
    [InlineData("clean")]
    [InlineData("process")]
    public async Task Snapshot_status_never_executes_case_distinct_filter_drivers(string hook)
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.email", "test@example.invalid");
        await repo.Git("config", "user.name", "Test");
        foreach (var name in new[] { "upper", "lower" })
            await File.WriteAllTextAsync(Path.Combine(repo.Path, name + ".txt"), "base\n");
        await repo.Git("add", "upper.txt", "lower.txt");
        await repo.Git("commit", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, ".gitattributes"),
            "upper.txt filter=Alpha\nlower.txt filter=alpha\n");
        await repo.Git("config", "filter.Alpha." + hook, "cmd /d /c echo upper>marker-upper.txt");
        await repo.Git("config", "filter.alpha." + hook, "cmd /d /c echo lower>marker-lower.txt");
        // Same byte length as the committed content: Git must inspect the worktree
        // rather than declaring it changed just from stat size.
        foreach (var name in new[] { "upper", "lower" })
            await File.WriteAllTextAsync(Path.Combine(repo.Path, name + ".txt"), "edit\n");
        var snapshot = await new WorktreeChangeReviewService().GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(snapshot.IsStale, snapshot.Error);
        Assert.Contains(snapshot.Working, file => file.Path == "upper.txt");
        Assert.Contains(snapshot.Working, file => file.Path == "lower.txt");
        Assert.False(File.Exists(Path.Combine(repo.Path, "marker-upper.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "marker-lower.txt")));
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
    public async Task Working_file_edits_refresh_status_without_repeating_first_page_git_log()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.name", "Test");
        await repo.Git("config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "base");
        await repo.Git("switch", "-c", "feature/test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "committed\n");
        await repo.Git("commit", "-am", "feature commit");
        var (executable, trace) = await GitProbeAsync(repo.Path);
        var service = new WorktreeChangeReviewService(executable);

        var first = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(first.IsStale, first.Error);
        Assert.Equal("feature commit", Assert.Single(first.Commits).Subject);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "working edit\n");
        var second = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(second.IsStale, second.Error);
        Assert.Contains(second.Working, file => file.Path == "tracked.txt" && file.Group == ReviewGroup.Unstaged);
        Assert.Equal(first.Commits.Select(commit => commit.Sha), second.Commits.Select(commit => commit.Sha));
        Assert.Equal(1, CountGitLogs(trace));
        Assert.Equal(2, CountGitStatuses(trace));

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "next commit\n");
        await repo.Git("commit", "-am", "second feature commit");
        var third = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(third.IsStale, third.Error);
        Assert.Equal(2, third.Commits.Count);
        Assert.Equal(2, CountGitLogs(trace));

        await repo.Git("branch", "-f", "trunk", "HEAD");
        var fourth = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(fourth.IsStale, fourth.Error);
        Assert.Empty(fourth.Commits);
        Assert.Equal(3, CountGitLogs(trace));
        var fifth = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(fifth.IsStale, fifth.Error);
        Assert.Empty(fifth.Commits);
        Assert.Equal(3, CountGitLogs(trace)); // An empty successful page is reusable.
        Assert.Equal(5, CountGitStatuses(trace));
    }

    [Fact]
    public async Task Cached_history_is_scoped_by_comparison_branch_and_selected_worktree()
    {
        using var firstRepo = new ReviewRepository();
        using var secondRepo = new ReviewRepository();
        foreach (var repo in new[] { firstRepo, secondRepo })
        {
            await repo.Git("init", "-b", "trunk");
            await repo.Git("config", "user.name", "Test");
            await repo.Git("config", "user.email", "test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
            await repo.Git("add", "tracked.txt");
            await repo.Git("commit", "-m", "base");
            await repo.Git("switch", "-c", "feature/test");
            await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "feature\n");
            await repo.Git("commit", "-am", "feature");
            await repo.Git("branch", "alternate", "trunk");
        }
        var (executable, trace) = await GitProbeAsync(firstRepo.Path);
        var service = new WorktreeChangeReviewService(executable);
        Assert.False((await service.GetSnapshotAsync(firstRepo.Path, "trunk")).IsStale);
        Assert.False((await service.GetSnapshotAsync(firstRepo.Path, "alternate")).IsStale);
        Assert.False((await service.GetSnapshotAsync(secondRepo.Path, "trunk")).IsStale);
        Assert.False((await service.GetSnapshotAsync(firstRepo.Path, "trunk")).IsStale);
        Assert.Equal(3, CountGitLogs(trace)); // Returning to an unchanged scope can reuse its page.
    }

    [Fact]
    public async Task Failed_history_query_does_not_seed_cache_for_next_successful_read()
    {
        using var repo = new ReviewRepository();
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.name", "Test");
        await repo.Git("config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "base");
        await repo.Git("switch", "-c", "feature/test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "feature\n");
        await repo.Git("commit", "-am", "feature");
        var (executable, trace) = await GitProbeAsync(repo.Path);
        var marker = Path.Combine(repo.Path, "fail-log.txt");
        await File.WriteAllTextAsync(marker, "fail once");
        var service = new WorktreeChangeReviewService(executable);
        var failed = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.True(failed.IsStale);
        Assert.Empty(failed.Commits);
        File.Delete(marker);
        var success = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(success.IsStale, success.Error);
        Assert.Equal("feature", Assert.Single(success.Commits).Subject);
        Assert.False((await service.GetSnapshotAsync(repo.Path, "trunk")).IsStale);
        Assert.Equal(2, CountGitLogs(trace));
    }

    [Fact]
    public async Task Cancelled_history_read_cannot_seed_cache_and_cleanup_allows_next_read()
    {
        using var repo = new ReviewRepository();
        await InitializeHistoryRepoAsync(repo);
        var (executable, trace) = await GitProbeAsync(repo.Path);
        var block = Path.Combine(repo.Path, "block-log.txt");
        var entered = Path.Combine(repo.Path, "log-entered.txt");
        await File.WriteAllTextAsync(block, "block");
        var service = new WorktreeChangeReviewService(executable);
        using var cancellation = new CancellationTokenSource();
        var pending = service.GetSnapshotAsync(repo.Path, "trunk", cancellation.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(entered))
            {
                Assert.False(pending.IsCompleted, "Read ended before the controlled log command started.");
                await Task.Delay(10, deadline.Token);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
        File.Delete(block);
        var success = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(success.IsStale, success.Error);
        Assert.Single(success.Commits);
        Assert.False((await service.GetSnapshotAsync(repo.Path, "trunk")).IsStale);
        Assert.Equal(2, CountGitLogs(trace));
    }

    [Fact]
    public async Task History_cache_is_bounded_and_eviction_reloads_instead_of_pausing()
    {
        using var repo = new ReviewRepository();
        await InitializeHistoryRepoAsync(repo);
        var (executable, trace) = await GitProbeAsync(repo.Path);
        var service = new WorktreeChangeReviewService(executable);
        for (var index = 0; index < 9; index++)
        {
            await repo.Git("branch", $"base-{index}", "trunk");
            var result = await service.GetSnapshotAsync(repo.Path, $"base-{index}");
            Assert.False(result.IsStale, result.Error);
            Assert.Single(result.Commits);
        }
        Assert.Equal(9, CountGitLogs(trace));
        var oldest = await service.GetSnapshotAsync(repo.Path, "base-0");
        Assert.False(oldest.IsStale, oldest.Error);
        Assert.Single(oldest.Commits);
        Assert.Equal(10, CountGitLogs(trace));
        Assert.False((await service.GetSnapshotAsync(repo.Path, "base-0")).IsStale);
        Assert.Equal(10, CountGitLogs(trace));
    }

    [Fact]
    public async Task Cache_does_not_hide_missing_main_and_ref_rewrites_reload_correct_history()
    {
        using var repo = new ReviewRepository();
        await InitializeHistoryRepoAsync(repo);
        var (executable, trace) = await GitProbeAsync(repo.Path);
        var service = new WorktreeChangeReviewService(executable);
        var first = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(first.IsStale, first.Error);
        var baseId = first.MainHead!;
        await repo.Git("branch", "-D", "trunk");
        var missing = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.True(missing.IsStale);
        Assert.Empty(missing.Commits);
        await repo.Git("branch", "trunk", "HEAD");
        var advanced = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(advanced.IsStale, advanced.Error);
        Assert.Empty(advanced.Commits);
        await repo.Git("commit", "--amend", "-m", "rewritten feature");
        var rewritten = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(rewritten.IsStale, rewritten.Error);
        Assert.Equal("rewritten feature", Assert.Single(rewritten.Commits).Subject);
        await repo.Git("branch", "-f", "trunk", baseId);
        var mainMovedBack = await service.GetSnapshotAsync(repo.Path, "trunk");
        Assert.False(mainMovedBack.IsStale, mainMovedBack.Error);
        Assert.Equal(baseId, mainMovedBack.MainHead);
        Assert.Equal("rewritten feature", Assert.Single(mainMovedBack.Commits).Subject);
        Assert.Equal(4, CountGitLogs(trace));
        // Alternate spelling of the same Windows worktree must not create another cache entry.
        Assert.False((await service.GetSnapshotAsync(repo.Path + Path.DirectorySeparatorChar, "trunk")).IsStale);
        Assert.Equal(4, CountGitLogs(trace));
    }

    private static async Task InitializeHistoryRepoAsync(ReviewRepository repo)
    {
        await repo.Git("init", "-b", "trunk");
        await repo.Git("config", "user.name", "Test");
        await repo.Git("config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "base\n");
        await repo.Git("add", "tracked.txt");
        await repo.Git("commit", "-m", "base");
        await repo.Git("switch", "-c", "feature/test");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "tracked.txt"), "feature\n");
        await repo.Git("commit", "-am", "feature");
    }

    private static async Task<(string Executable, string Trace)> GitProbeAsync(string root)
    {
        var trace = Path.Combine(root, "git-commands.txt");
        var script = Path.Combine(root, "git-probe.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif \"%3\"==\"log\" echo LOG>>\"%~dp0git-commands.txt\"\r\nif \"%3\"==\"status\" echo STATUS>>\"%~dp0git-commands.txt\"\r\nif \"%3\"==\"-c\" echo STATUS>>\"%~dp0git-commands.txt\"\r\nif \"%3\"==\"log\" if exist \"%~dp0fail-log.txt\" exit /b 1\r\nif \"%3\"==\"log\" if exist \"%~dp0block-log.txt\" (\r\n echo entered>\"%~dp0log-entered.txt\"\r\n ping -n 60 127.0.0.1 > nul\r\n)\r\ngit %*\r\n");
        return (script, trace);
    }

    private static int CountGitLogs(string trace) => File.ReadAllLines(trace).Count(line => line == "LOG");
    private static int CountGitStatuses(string trace) => File.ReadAllLines(trace).Count(line => line == "STATUS");

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
