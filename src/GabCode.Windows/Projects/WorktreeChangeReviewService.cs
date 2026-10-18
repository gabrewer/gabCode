using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace GabCode.Windows.Projects;

internal enum ReviewGroup { Staged, Unstaged, Commit }
internal enum ReviewStatus { Added, Modified, Deleted, Renamed, Untracked, Conflicted, Binary }
internal enum ReviewDiffState { Patch, Binary, Oversized, Truncated, Conflict, Timeout, Unavailable }
internal enum ReviewStopReason { None, Limit, Cancelled, Budget }

internal sealed record ReviewFile(string Path, string? OldPath, ReviewGroup Group, ReviewStatus Status, string? CommitSha = null, string? Comparison = null)
{
    internal string Identity => $"{Group}:{CommitSha}:{OldPath}:{Path}";
}
internal sealed record ReviewCommit(string Sha, string Subject, string Author, DateTimeOffset Timestamp, IReadOnlyList<string> Parents, string Summary);
internal sealed record ReviewSnapshot(string Worktree, string? Head, string? MainHead, IReadOnlyList<ReviewFile> Working, IReadOnlyList<ReviewCommit> Commits, bool IsStale, string? Error, ReviewStopReason StopReason = ReviewStopReason.None, bool WasSuccessful = false);
internal sealed record ReviewDiff(ReviewDiffState State, string? Patch, string? Error = null);
internal sealed record ReviewDocument(string Before, string After, string Comparison,
    bool BeforeBom, bool AfterBom, string BeforeEol, string AfterEol,
    bool BeforeFinalNewline, bool AfterFinalNewline, bool RawFilter);

/// <summary>Windows-owned, bounded Git observation; never calls worktree prune or changes the index.</summary>
internal sealed class WorktreeChangeReviewService
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaximumOutputBytes = 2 * 1024 * 1024;
    private const int PageSize = 100;
    // All updated gabCode instances for this Windows user share an exclusive OS file handle.
    // File handles close on process death; unlike a named semaphore there is no stranded count.
    private static readonly SemaphoreSlim GitSlots = new(1, 1);
    private static readonly List<FileStream> QuarantinedSlots = [];
    private static readonly string MachineSlotPath = Path.Combine(Path.GetTempPath(), "gabCode.windows.git-review-slot.lock");
    private readonly string executable;
    private readonly TimeSpan timeout;
    private readonly TimeSpan snapshotBudget;

    internal WorktreeChangeReviewService(string executable = "git", TimeSpan? timeout = null, TimeSpan? snapshotBudget = null)
    {
        this.executable = executable;
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        this.snapshotBudget = snapshotBudget ?? TimeSpan.FromSeconds(8);
        if (this.snapshotBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(snapshotBudget));
    }

    internal async Task<ReviewSnapshot> GetSnapshotAsync(string worktree, string? mainBranch, CancellationToken token = default)
    {
        var root = Path.GetFullPath(worktree);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(snapshotBudget);
        var readToken = budget.Token;
        string? head = null;
        string? mainHead = null;
        IReadOnlyList<ReviewFile> working = [];
        try
        {
            head = (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken).ConfigureAwait(false)).Trim();
            var status = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"], readToken).ConfigureAwait(false);
            working = ParseStatus(status);
            if (string.IsNullOrWhiteSpace(mainBranch)) return new(root, head, null, working, [], true, "Configured local main branch is unavailable.");
            mainHead = (await RunAsync(root, ["rev-parse", "--verify", $"refs/heads/{mainBranch}^{{commit}}"], readToken).ConfigureAwait(false)).Trim();
            var commits = await GetCommitsAsync(root, head, mainHead, 0, readToken).ConfigureAwait(false);
            return new(root, head, mainHead, working, commits, false, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { return new(root, head, mainHead, working, [], true, "Git refresh exceeded its eight-second budget.", ReviewStopReason.Budget); }
        catch (Exception exception) when (exception is TimeoutException or InvalidDataException or Win32Exception)
        { return new(root, head, mainHead, working, [], true, exception.Message, ReviewStopReason.Limit); }
        catch (Exception exception) { return new(root, head, mainHead, working, [], true, exception.Message); }
    }

    internal async Task<IReadOnlyList<ReviewCommit>> GetCommitsAsync(string worktree, string head, string mainHead, int skip, CancellationToken token = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        var raw = await RunAsync(worktree, ["log", "--topo-order", $"--skip={skip}", $"--max-count={PageSize}", "--format=%H%x00%s%x00%an%x00%aI%x00%P%x00", "--shortstat", head, $"^{mainHead}"], token).ConfigureAwait(false);
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

    internal async Task<bool> IsCommitUniqueToHeadAsync(string worktree, string sha, string head, string mainHead, CancellationToken token = default)
    {
        try
        {
            var common = (await RunAsync(worktree, ["merge-base", sha, head], token)).Trim();
            if (!string.Equals(common, sha, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals((await RunAsync(worktree, ["rev-list", "--max-count=1", sha, $"^{mainHead}"], token)).Trim(), sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
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
            entries.Add(new(path, old, ReviewGroup.Commit, ParseStatusCode(code[0]), sha,
                parents.Length > 1 ? $"Compared with first parent ({before[..Math.Min(8, before.Length)]} → {sha[..Math.Min(8, sha.Length)]})" : "Compared with empty tree (root commit)"));
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
            // Git's binary markers are unprefixed diff metadata lines. Hunk lines
            // begin with '+', '-' or ' ', and must never determine classification.
            var isBinary = raw.Split('\n').Any(line =>
            {
                var metadata = line.TrimEnd('\r');
                return metadata == "GIT binary patch" ||
                    metadata.StartsWith("Binary files ", StringComparison.Ordinal) && metadata.EndsWith(" differ", StringComparison.Ordinal);
            });
            return isBinary ? new(ReviewDiffState.Binary, null) : new(ReviewDiffState.Patch, raw);
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

    internal async Task<ReviewDocument> GetStagedDocumentAsync(string worktree, ReviewFile file, CancellationToken token = default)
    {
        if (file.Group != ReviewGroup.Staged || file.Status != ReviewStatus.Modified ||
            Path.IsPathFullyQualified(file.Path) || file.Path.Contains('\\') ||
            file.Path.Split('/').Any(segment => segment is "" or "." or "..") || file.Path.Contains('\0'))
            throw new InvalidDataException("Selected staged file is not a regular tracked text path.");
        var root = Path.GetFullPath(worktree);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var readToken = deadline.Token;
        try
        {
            var head = (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim();
            var beforeEntry = await ReadTreeEntryAsync(root, head, file.Path, readToken);
            var afterEntry = await ReadIndexEntryAsync(root, file.Path, readToken);
            var before = DecodeReviewBytes(await RunBytesAsync(root, ["cat-file", "blob", beforeEntry], readToken));
            var after = DecodeReviewBytes(await RunBytesAsync(root, ["cat-file", "blob", afterEntry], readToken));
            var attribute = await RunAsync(root, ["check-attr", "-z", "filter", "--", file.Path], readToken);
            var parts = attribute.Split('\0');
            var rawFilter = parts.Length >= 3 && parts[1] == "filter" && parts[2] is not ("unspecified" or "unset" or "");
            if (head != (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim() ||
                beforeEntry != await ReadTreeEntryAsync(root, head, file.Path, readToken) ||
                afterEntry != await ReadIndexEntryAsync(root, file.Path, readToken))
                throw new InvalidDataException("Staged file changed while loading; refresh review.");
            return new ReviewDocument(before.Text, after.Text, "HEAD → index",
                before.Bom, after.Bom, before.Eol, after.Eol, before.FinalNewline, after.FinalNewline, rawFilter);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Diff unavailable: staged snapshot exceeded its twelve-second budget."); }
    }

    private async Task<string> ReadTreeEntryAsync(string root, string head, string path, CancellationToken token)
    {
        var record = await RunAsync(root, ["ls-tree", "-z", head, "--", path], token);
        var rows = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length != 1 || !rows[0].EndsWith("\t" + path, StringComparison.Ordinal))
            throw new InvalidDataException("Selected file is unavailable in HEAD.");
        var prefix = rows[0][..rows[0].IndexOf('\t')].Split(' ');
        if (prefix.Length != 3 || prefix[0] is not ("100644" or "100755") || prefix[1] != "blob" || !IsOid(prefix[2]))
            throw new InvalidDataException("Diff unavailable: selected HEAD entry is not a regular file.");
        return prefix[2];
    }

    private async Task<string> ReadIndexEntryAsync(string root, string path, CancellationToken token)
    {
        var record = await RunAsync(root, ["ls-files", "--stage", "-z", "--", path], token);
        var rows = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length != 1 || !rows[0].EndsWith("\t" + path, StringComparison.Ordinal))
            throw new InvalidDataException("Staged file is unavailable or unmerged.");
        var prefix = rows[0][..rows[0].IndexOf('\t')].Split(' ');
        if (prefix.Length != 3 || prefix[0] is not ("100644" or "100755") || prefix[2] != "0" || !IsOid(prefix[1]))
            throw new InvalidDataException("Diff unavailable: selected index entry is not a regular file.");
        return prefix[1];
    }

    private static bool IsOid(string oid) => oid.Length is 40 or 64 && oid.All(Uri.IsHexDigit);

    private static (string Text, bool Bom, string Eol, bool FinalNewline) DecodeReviewBytes(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ||
            bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }) || bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
            throw new InvalidDataException("Diff unavailable: unsupported text encoding.");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var data = bytes.AsSpan(bom ? 3 : 0);
        if (data.Contains((byte)0)) throw new InvalidDataException("Diff unavailable: binary content.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(data);
            var crlf = text.Contains("\r\n", StringComparison.Ordinal);
            var rest = text.Replace("\r\n", "", StringComparison.Ordinal);
            var lf = rest.Contains('\n');
            var cr = rest.Contains('\r');
            var eol = ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0)) switch
            { 0 => "none", > 1 => "mixed", _ => crlf ? "CRLF" : lf ? "LF" : "CR" };
            return (text, bom, eol, text.EndsWith('\n') || text.EndsWith('\r'));
        }
        catch (DecoderFallbackException) { throw new InvalidDataException("Diff unavailable: unsupported text encoding."); }
    }

    private Task<string> RunAsync(string worktree, IReadOnlyList<string> args, CancellationToken token) =>
        RunCoreAsync(worktree, args, (owned, readToken) => ReadBoundedAsync(owned.StandardOutput, readToken), token);

    private Task<byte[]> RunBytesAsync(string worktree, IReadOnlyList<string> args, CancellationToken token) =>
        RunCoreAsync(worktree, args, (owned, readToken) => ReadBoundedBytesAsync(owned.StandardOutput.BaseStream, readToken), token);

    private async Task<T> RunCoreAsync<T>(string worktree, IReadOnlyList<string> args,
        Func<OwnedGitProcess, CancellationToken, Task<T>> readOutput, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = worktree, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.ArgumentList.Add("--no-pager");
        info.ArgumentList.Add("--literal-pathspecs");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try { await GitSlots.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        { throw new TimeoutException("Read-only Git query could not acquire capacity before its deadline."); }
        var safeToRelease = true;
        FileStream? machineSlot = null;
        try
        {
            try { machineSlot = await AcquireMachineSlotAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            { throw new TimeoutException("Read-only Git query could not acquire cross-process capacity before its deadline."); }
            using var owned = OwnedGitProcess.Start(info);
            var process = owned.Process;
            Task<T>? stdout = null;
            Task<string>? stderr = null;
            try
            {
            stdout = readOutput(owned, linked.Token);
            stderr = ReadBoundedAsync(owned.StandardError, linked.Token);
            var exit = process.WaitForExitAsync(linked.Token);
            // Task.WhenAll would wait for a hung child even after a reader rejected oversized output.
            // Observe each completed operation immediately so the catch can stop the owned process.
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                // The root may exit before a descendant releases the pipes. Keep the
                // deadline active even after the process-exit task leaves this list.
                var completed = await Task.WhenAny(pending).WaitAsync(linked.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Git exited {process.ExitCode}." : error.Trim());
            owned.Stop();
            await owned.WaitForEmptyAsync().ConfigureAwait(false);
            return output;
        }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                try { owned.Stop(); await owned.WaitForEmptyAsync().ConfigureAwait(false); await ObserveReadersAsync(stdout, stderr).ConfigureAwait(false); }
                catch (Exception error) when (error is TimeoutException or Win32Exception) { safeToRelease = false; throw; }
                throw new TimeoutException("Read-only Git query timed out.");
            }
            catch
            {
                try { owned.Stop(); await owned.WaitForEmptyAsync().ConfigureAwait(false); await ObserveReadersAsync(stdout, stderr).ConfigureAwait(false); }
                catch (Exception error) when (error is TimeoutException or Win32Exception) { safeToRelease = false; throw; }
                throw;
            }
        }
        finally
        {
            if (safeToRelease)
            {
                machineSlot?.Dispose();
                GitSlots.Release();
            }
            else if (machineSlot is not null)
            {
                // Keep the OS-wide slot occupied after uncertain cleanup, including across other instances.
                QuarantineMachineSlot(machineSlot);
            }
        }
    }

    internal static void QuarantineMachineSlot(FileStream slot)
    {
        lock (QuarantinedSlots) QuarantinedSlots.Add(slot);
    }

    internal static async Task<FileStream> AcquireMachineSlotAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(MachineSlotPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (!token.IsCancellationRequested)
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    private static async Task ObserveReadersAsync(Task? stdout, Task<string>? stderr)
    {
        if (stdout is null || stderr is null) return;
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception error) when (error is not TimeoutException) { /* original read failure remains authoritative */ }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            if (result.Length + count > 1024 * 1024) throw new InvalidDataException("Diff unavailable: text exceeds one-megabyte limit.");
            result.Write(buffer, 0, count);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var data = new StringBuilder();
        var buffer = new char[4096];
        var capturedBytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return data.ToString();
            capturedBytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (capturedBytes > MaximumOutputBytes) throw new InvalidDataException("Git output exceeded the review limit.");
            data.Append(buffer, 0, read);
        }
    }

}
