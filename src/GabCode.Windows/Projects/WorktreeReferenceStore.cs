using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GabCode.Windows.Projects;

internal sealed record GitHubIssueReference(string Owner, string Repository, int Number)
{
    internal string Url => $"https://github.com/{Owner}/{Repository}/issues/{Number}";

    internal string DisplayName => $"{Owner}/{Repository}#{Number}";

    internal static GitHubIssueReference Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort)
            throw new FormatException("Enter a GitHub issue URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 ||
            !IsNameSegment(segments[0]) ||
            !IsNameSegment(segments[1]) ||
            !string.Equals(segments[2], "issues", StringComparison.Ordinal) ||
            !int.TryParse(segments[3], out var number) ||
            number <= 0)
            throw new FormatException("Enter a GitHub issue URL.");

        return new GitHubIssueReference(segments[0], segments[1], number);
    }

    private static bool IsNameSegment(string value) =>
        value.Length != 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

internal sealed record WorktreeReference(string? MarkdownPath, GitHubIssueReference? Issue)
{
    internal static WorktreeReference Empty { get; } = new(null, null);
}

internal sealed class WorktreeReferenceStore
{
    private readonly string path;

    internal WorktreeReferenceStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gabCode",
            "worktree-references.json");
    }

    internal Task<WorktreeReference> ReadAsync(string workspacePath, string worktreePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithLock(() =>
        {
            var references = ReadAll();
            return references.TryGetValue(Key(workspacePath, worktreePath), out var stored)
                ? ToReference(stored)
                : WorktreeReference.Empty;
        }, cancellationToken), cancellationToken);

    internal Task SaveMarkdownAsync(string workspacePath, string worktreePath, string markdownPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var normalizedMarkdownPath = ValidateMarkdown(markdownPath);
            Mutate(workspacePath, worktreePath, current => current with { MarkdownPath = normalizedMarkdownPath }, cancellationToken);
        }, cancellationToken);

    internal Task SaveIssueAsync(string workspacePath, string worktreePath, GitHubIssueReference issue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return Task.Run(() =>
            Mutate(workspacePath, worktreePath, current => current with { IssueUrl = GitHubIssueReference.Parse(issue.Url).Url }, cancellationToken), cancellationToken);
    }

    internal Task RemoveMarkdownAsync(string workspacePath, string worktreePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Mutate(workspacePath, worktreePath, current => current with { MarkdownPath = null }, cancellationToken), cancellationToken);

    internal Task RemoveIssueAsync(string workspacePath, string worktreePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Mutate(workspacePath, worktreePath, current => current with { IssueUrl = null }, cancellationToken), cancellationToken);

    internal Task RemoveWorktreeAsync(string workspacePath, string worktreePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithLock(() =>
        {
            var references = ReadAll();
            if (references.Remove(Key(workspacePath, worktreePath))) WriteAll(references);
            return 0;
        }, cancellationToken), cancellationToken);

    private void Mutate(string workspacePath, string worktreePath, Func<PersistedReference, PersistedReference> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        WithLock(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var references = ReadAll();
            var key = Key(workspacePath, worktreePath);
            var updated = mutate(references.GetValueOrDefault(key, PersistedReference.Empty));
            if (string.IsNullOrWhiteSpace(updated.MarkdownPath) && string.IsNullOrWhiteSpace(updated.IssueUrl)) references.Remove(key);
            else references[key] = updated;
            cancellationToken.ThrowIfCancellationRequested();
            WriteAll(references);
            return 0;
        }, cancellationToken);
    }

    private Dictionary<string, PersistedReference> ReadAll()
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, PersistedReference>>(File.ReadAllText(path));
            return parsed is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void WriteAll(Dictionary<string, PersistedReference> references)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(references));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private T WithLock<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var mutexName = $"Local\\gabCode.worktreeReferences.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))}";
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (!acquired && stopwatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(50)); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired) throw new IOException("Timed out while saving worktree references.");
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string Key(string workspacePath, string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        return $"{Path.GetFullPath(workspacePath)}\u001F{WorktreePath.Normalize(worktreePath)}";
    }

    private static string ValidateMarkdown(string markdownPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        var fullPath = Path.GetFullPath(markdownPath);
        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a Markdown file.", nameof(markdownPath));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The Markdown file could not be found.", fullPath);
        return fullPath;
    }

    private static WorktreeReference ToReference(PersistedReference stored)
    {
        var markdownPath = IsStoredMarkdownPath(stored.MarkdownPath) ? Path.GetFullPath(stored.MarkdownPath!) : null;
        GitHubIssueReference? issue = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stored.IssueUrl)) issue = GitHubIssueReference.Parse(stored.IssueUrl);
        }
        catch (FormatException) { }
        return markdownPath is null && issue is null ? WorktreeReference.Empty : new WorktreeReference(markdownPath, issue);
    }

    private static bool IsStoredMarkdownPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            var fullPath = Path.GetFullPath(value);
            return string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(fullPath), ".markdown", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private sealed record PersistedReference(string? MarkdownPath, string? IssueUrl)
    {
        internal static PersistedReference Empty { get; } = new(null, null);
    }
}
