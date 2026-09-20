using System.IO;
using System.Text.Json;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeReferenceStoreTests
{
    [Theory]
    [InlineData("https://github.com/gabrewer/gabCode/issues/92", "gabrewer", "gabCode", 92)]
    [InlineData("https://github.com/gabrewer/gabCode/issues/92?foo=bar#comment", "gabrewer", "gabCode", 92)]
    public void Parses_a_canonical_GitHub_issue_URL(string input, string owner, string repository, int number)
    {
        var reference = GitHubIssueReference.Parse(input);

        Assert.Equal(owner, reference.Owner);
        Assert.Equal(repository, reference.Repository);
        Assert.Equal(number, reference.Number);
        Assert.Equal($"https://github.com/{owner}/{repository}/issues/{number}", reference.Url);
        Assert.Equal($"{owner}/{repository}#{number}", reference.DisplayName);
    }

    [Theory]
    [InlineData("http://github.com/gabrewer/gabCode/issues/92")]
    [InlineData("https://github.com/gabrewer/gabCode/pull/92")]
    [InlineData("https://github.com/gabrewer/gabCode/issues/0")]
    [InlineData("https://example.test/gabrewer/gabCode/issues/92")]
    [InlineData("https://github.com/gabrewer/gabCode/issues/not-a-number")]
    public void Rejects_noncanonical_GitHub_issue_URLs(string input) =>
        Assert.Throws<FormatException>(() => GitHubIssueReference.Parse(input));

    [Fact]
    public async Task Stores_independent_markdown_and_issue_slots_by_normalized_workspace_and_worktree_identity()
    {
        var root = TemporaryDirectory();
        try
        {
            var markdown = Path.Combine(root, "review.md");
            await File.WriteAllTextAsync(markdown, "# Review");
            var store = new WorktreeReferenceStore(Path.Combine(root, "references.json"));
            var workspace = Path.Combine(root, "project.gabcode-workspace");
            var worktree = Path.Combine(root, "wt", "feature");

            await store.SaveMarkdownAsync(workspace, worktree, markdown);
            await store.SaveIssueAsync(workspace.ToUpperInvariant(), Path.Combine(root, "wt", "feature", "..", "feature"), GitHubIssueReference.Parse("https://github.com/gabrewer/gabCode/issues/92"));

            var saved = await store.ReadAsync(workspace, worktree);
            Assert.Equal(Path.GetFullPath(markdown), saved.MarkdownPath);
            Assert.Equal("gabrewer/gabCode#92", saved.Issue?.DisplayName);

            await store.RemoveMarkdownAsync(workspace, worktree);
            var afterMarkdownRemoval = await store.ReadAsync(workspace, worktree);
            Assert.Null(afterMarkdownRemoval.MarkdownPath);
            Assert.Equal("gabrewer/gabCode#92", afterMarkdownRemoval.Issue?.DisplayName);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Rejects_nonexistent_or_nonmarkdown_assignment_without_creating_local_state()
    {
        var root = TemporaryDirectory();
        try
        {
            var storePath = Path.Combine(root, "references.json");
            var store = new WorktreeReferenceStore(storePath);
            var workspace = Path.Combine(root, "project.gabcode-workspace");
            var worktree = Path.Combine(root, "wt", "feature");
            var text = Path.Combine(root, "review.txt");
            await File.WriteAllTextAsync(text, "not markdown");

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMarkdownAsync(workspace, worktree, text));
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.SaveMarkdownAsync(workspace, worktree, Path.Combine(root, "missing.md")));

            Assert.False(File.Exists(storePath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Recovers_from_corrupt_local_metadata_without_blocking_a_valid_assignment()
    {
        var root = TemporaryDirectory();
        try
        {
            var storePath = Path.Combine(root, "references.json");
            await File.WriteAllTextAsync(storePath, "not json");
            var markdown = Path.Combine(root, "review.markdown");
            await File.WriteAllTextAsync(markdown, "# Review");
            var store = new WorktreeReferenceStore(storePath);
            var workspace = Path.Combine(root, "project.gabcode-workspace");
            var worktree = Path.Combine(root, "wt", "feature");

            Assert.Equal(WorktreeReference.Empty, await store.ReadAsync(workspace, worktree));
            await store.SaveMarkdownAsync(workspace, worktree, markdown);

            Assert.Equal(Path.GetFullPath(markdown), (await store.ReadAsync(workspace, worktree)).MarkdownPath);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Corrupt_relative_markdown_path_is_not_silently_retargeted_to_the_process_directory()
    {
        var root = TemporaryDirectory();
        try
        {
            var storePath = Path.Combine(root, "references.json");
            var workspace = Path.Combine(root, "project.gabcode-workspace");
            var worktree = Path.Combine(root, "wt", "feature");
            var key = $"{Path.GetFullPath(workspace)}\u001F{WorktreePath.Normalize(worktree)}";
            await File.WriteAllTextAsync(storePath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [key] = new { MarkdownPath = "review.md", IssueUrl = (string?)null },
            }));

            var restored = await new WorktreeReferenceStore(storePath).ReadAsync(workspace, worktree);

            Assert.Equal(WorktreeReference.Empty, restored);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Concurrent_store_instances_preserve_different_worktree_keys_and_leave_no_temporary_files()
    {
        var root = TemporaryDirectory();
        try
        {
            var storePath = Path.Combine(root, "references.json");
            var firstMarkdown = Path.Combine(root, "first.md");
            var secondMarkdown = Path.Combine(root, "second.md");
            await File.WriteAllTextAsync(firstMarkdown, "# First");
            await File.WriteAllTextAsync(secondMarkdown, "# Second");
            var workspace = Path.Combine(root, "project.gabcode-workspace");

            await Task.WhenAll(
                new WorktreeReferenceStore(storePath).SaveMarkdownAsync(workspace, Path.Combine(root, "wt", "first"), firstMarkdown),
                new WorktreeReferenceStore(storePath).SaveIssueAsync(workspace, Path.Combine(root, "wt", "second"), GitHubIssueReference.Parse("https://github.com/gabrewer/gabCode/issues/92")),
                new WorktreeReferenceStore(storePath).SaveMarkdownAsync(workspace, Path.Combine(root, "wt", "second"), secondMarkdown));

            var reader = new WorktreeReferenceStore(storePath);
            Assert.Equal(Path.GetFullPath(firstMarkdown), (await reader.ReadAsync(workspace, Path.Combine(root, "wt", "first"))).MarkdownPath);
            var second = await reader.ReadAsync(workspace, Path.Combine(root, "wt", "second"));
            Assert.Equal(Path.GetFullPath(secondMarkdown), second.MarkdownPath);
            Assert.Equal("gabrewer/gabCode#92", second.Issue?.DisplayName);
            Assert.Empty(Directory.GetFiles(root, "references.json.*.tmp"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task Confirmed_removal_deletes_only_that_worktree_association()
    {
        var root = TemporaryDirectory();
        try
        {
            var firstMarkdown = Path.Combine(root, "first.md");
            var secondMarkdown = Path.Combine(root, "second.md");
            await File.WriteAllTextAsync(firstMarkdown, "# First");
            await File.WriteAllTextAsync(secondMarkdown, "# Second");
            var store = new WorktreeReferenceStore(Path.Combine(root, "references.json"));
            var workspace = Path.Combine(root, "project.gabcode-workspace");
            var first = Path.Combine(root, "wt", "first");
            var second = Path.Combine(root, "wt", "second");
            await store.SaveMarkdownAsync(workspace, first, firstMarkdown);
            await store.SaveMarkdownAsync(workspace, second, secondMarkdown);

            await store.RemoveWorktreeAsync(workspace, first);

            Assert.Equal(WorktreeReference.Empty, await store.ReadAsync(workspace, first));
            Assert.Equal(Path.GetFullPath(secondMarkdown), (await store.ReadAsync(workspace, second)).MarkdownPath);
        }
        finally { Delete(root); }
    }

    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gabCode worktree references", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { }
    }
}
