using System.Diagnostics;
using System.IO;
using System.Text;

namespace GabCode.Windows.Projects;

internal enum ReviewGroup { Staged, Unstaged, Commit }
internal enum ReviewStatus { Added, Modified, Deleted, Renamed, Untracked, Conflicted, Binary }
internal enum ReviewDiffState { Patch, Binary, Oversized, Truncated, Conflict, Timeout, Unavailable }

internal sealed record ReviewFile(string Path, string? OldPath, ReviewGroup Group, ReviewStatus Status, string? CommitSha = null)
{
    internal string Identity => $"{Group}:{CommitSha}:{OldPath}:{Path}";
}
internal sealed record ReviewCommit(string Sha, string Subject, string Author, DateTimeOffset Timestamp, IReadOnlyList<string> Parents, string Summary);
internal sealed record ReviewSnapshot(string Worktree, string? Head, string? MainHead, IReadOnlyList<ReviewFile> Working, IReadOnlyList<ReviewCommit> Commits, bool IsStale, string? Error);
internal sealed record ReviewDiff(ReviewDiffState State, string? Patch, string? Error = null);

/// <summary>Windows-owned, bounded Git observation; never calls worktree prune or changes the index.</summary>
internal sealed class WorktreeChangeReviewService
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaximumOutputBytes = 2 * 1024 * 1024;
    private const int PageSize = 100;
    private readonly string executable;
    private readonly TimeSpan timeout;

    internal WorktreeChangeReviewService(string executable = "git", TimeSpan? timeout = null)
    {
        this.executable = executable;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    internal async Task<ReviewSnapshot> GetSnapshotAsync(string worktree, string? mainBranch, CancellationToken token = default)
    {
        var root = Path.GetFullPath(worktree);
        string? head = null;
        string? mainHead = null;
        IReadOnlyList<ReviewFile> working = [];
        try
        {
            head = (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], token)).Trim();
            var status = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"], token);
            working = ParseStatus(status);
            if (string.IsNullOrWhiteSpace(mainBranch)) return new(root, head, null, working, [], true, "Configured local main branch is unavailable.");
            mainHead = (await RunAsync(root, ["rev-parse", "--verify", $"refs/heads/{mainBranch}^{{commit}}"], token)).Trim();
            var commits = await GetCommitsAsync(root, head, mainHead, 0, token);
            return new(root, head, mainHead, working, commits, false, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(root, head, mainHead, working, [], true, exception.Message); }
    }

    internal async Task<IReadOnlyList<ReviewCommit>> GetCommitsAsync(string worktree, string head, string mainHead, int skip, CancellationToken token = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        var raw = await RunAsync(worktree, ["log", "--topo-order", $"--skip={skip}", $"--max-count={PageSize}", "--format=%H%x00%s%x00%an%x00%aI%x00%P%x00", "--shortstat", head, $"^{mainHead}"], token);
        var commits = new List<ReviewCommit>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length >= 5 && fields[0].Length is 40 or 64)
            {
                var parents = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                commits.Add(new ReviewCommit(fields[0], fields[1], fields[2], DateTimeOffset.Parse(fields[3]), parents, "Summary unavailable"));
            }
            else if (commits.Count > 0 && line.Contains(" changed", StringComparison.Ordinal))
                commits[^1] = commits[^1] with { Summary = line.Trim() };
        }
        return commits;
    }

    internal async Task<IReadOnlyList<ReviewFile>> GetCommitFilesAsync(string worktree, string sha, CancellationToken token = default)
    {
        var parents = (await RunAsync(worktree, ["rev-list", "--parents", "-n", "1", sha], token)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var before = parents.Length > 1 ? parents[1].Trim() : EmptyTree;
        var raw = await RunAsync(worktree, ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M", before, sha, "--"], token);
        var tokens = raw.Split('\0');
        var entries = new List<ReviewFile>();
        for (var i = 0; i + 1 < tokens.Length && tokens[i].Length > 0;)
        {
            var code = tokens[i++];
            var old = code.StartsWith('R') || code.StartsWith('C') ? tokens[i++] : null;
            var path = tokens[i++];
            entries.Add(new(path, old, ReviewGroup.Commit, ParseStatusCode(code[0]), sha));
        }
        return entries.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    internal async Task<ReviewDiff> GetDiffAsync(string worktree, ReviewFile file, CancellationToken token = default)
    {
        if (file.Status == ReviewStatus.Conflicted) return new(ReviewDiffState.Conflict, null);
        try
        {
            string raw;
            if (file.Status == ReviewStatus.Untracked)
            {
                var path = Path.GetFullPath(Path.Combine(worktree, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                var root = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return new(ReviewDiffState.Unavailable, null, "Path is outside the selected worktree.");
                if (!File.Exists(path)) return new(ReviewDiffState.Unavailable, null, "Untracked file is unavailable.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumOutputBytes) return new(ReviewDiffState.Oversized, null);
                var data = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(data, token);
                if (Array.IndexOf(data, (byte)0) >= 0) return new(ReviewDiffState.Binary, null);
                var content = new UTF8Encoding(false, true).GetString(data);
                var lines = content.Split('\n');
                var endsWithNewline = content.EndsWith('\n');
                var count = lines.Length - (endsWithNewline ? 1 : 0);
                raw = "--- /dev/null\n+++ b/" + file.Path + "\n@@ -0,0 +1," + count + " @@\n" +
                    string.Concat(lines.Take(count).Select(line => "+" + line + "\n")) +
                    (count > 0 && !endsWithNewline ? "\\ No newline at end of file\n" : "");
            }
            else if (file.Group == ReviewGroup.Commit)
            {
                if (file.CommitSha is null) return new(ReviewDiffState.Unavailable, null);
                var parents = (await RunAsync(worktree, ["rev-list", "--parents", "-n", "1", file.CommitSha], token)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var before = parents.Length > 1 ? parents[1].Trim() : EmptyTree;
                raw = await RunAsync(worktree, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "-M", before, file.CommitSha, "--", file.OldPath ?? file.Path, file.Path], token);
            }
            else
            {
                var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "-M" };
                if (file.Group == ReviewGroup.Staged) args.Add("--cached");
                args.AddRange(["--", file.OldPath ?? file.Path, file.Path]);
                raw = await RunAsync(worktree, args, token);
            }
            return raw.Contains("Binary files ", StringComparison.Ordinal) || raw.Contains("GIT binary patch", StringComparison.Ordinal)
                ? new(ReviewDiffState.Binary, null)
                : new(ReviewDiffState.Patch, raw);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (TimeoutException exception) { return new(ReviewDiffState.Timeout, null, exception.Message); }
        catch (InvalidDataException exception) { return new(ReviewDiffState.Truncated, null, exception.Message); }
        catch (Exception exception) { return new(ReviewDiffState.Unavailable, null, exception.Message); }
    }

    private static ReviewStatus ParseStatusCode(char code) => code switch
    {
        'A' => ReviewStatus.Added, 'D' => ReviewStatus.Deleted, 'R' => ReviewStatus.Renamed,
        _ => ReviewStatus.Modified
    };

    internal static IReadOnlyList<ReviewFile> ParseStatus(string raw)
    {
        var output = new List<ReviewFile>();
        var records = raw.Split('\0');
        for (var i = 0; i < records.Length && records[i].Length > 2; i++)
        {
            var row = records[i];
            if (row.Length < 4 || row[2] != ' ') continue;
            var x = row[0]; var y = row[1]; var path = row[3..].Replace('\\', '/');
            if (x == '!' || y == '!') continue;
            if (x == '?' && y == '?') { output.Add(new(path, null, ReviewGroup.Unstaged, ReviewStatus.Untracked)); continue; }
            if (x == 'U' || y == 'U' || x == 'A' && y == 'A' || x == 'D' && y == 'D')
            { output.Add(new(path, null, ReviewGroup.Unstaged, ReviewStatus.Conflicted)); continue; }
            string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C') original = ++i < records.Length ? records[i].Replace('\\', '/') : null;
            if (x != ' ') output.Add(new(path, x is 'R' or 'C' ? original : null, ReviewGroup.Staged, ParseStatusCode(x)));
            if (y != ' ') output.Add(new(path, y is 'R' or 'C' ? original : null, ReviewGroup.Unstaged, ParseStatusCode(y)));
        }
        return output.OrderBy(x => x.Group).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    private async Task<string> RunAsync(string worktree, IReadOnlyList<string> args, CancellationToken token)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = worktree, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.ArgumentList.Add("--no-pager");
        info.ArgumentList.Add("--literal-pathspecs");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, linked.Token);
            var stderr = ReadBoundedAsync(process.StandardError, linked.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(linked.Token));
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Git exited {process.ExitCode}." : error.Trim());
            return output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            TryStop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("Read-only Git query timed out.");
        }
        catch
        {
            TryStop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var data = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return data.ToString();
            if (data.Length + read > MaximumOutputBytes) throw new InvalidDataException("Git output exceeded the review limit.");
            data.Append(buffer, 0, read);
        }
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
