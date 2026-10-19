using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace GabCode.Windows.Projects;

internal enum ReviewGroup { Staged, Unstaged, Commit, Branch }
internal enum ReviewStatus { Added, Modified, Deleted, Renamed, Untracked, Conflicted, Binary }
internal enum ReviewDiffState { Patch, Binary, Oversized, Truncated, Conflict, Timeout, Unavailable }
internal enum ReviewStopReason { None, Limit, Cancelled, Budget }

internal sealed record ReviewFile(string Path, string? OldPath, ReviewGroup Group, ReviewStatus Status, string? CommitSha = null, string? Comparison = null,
    string? BaseRevision = null, string? TargetRevision = null)
{
    internal string Identity => $"{Group}:{CommitSha}:{OldPath}:{Path}" +
        (Group == ReviewGroup.Branch ? $":{BaseRevision}:{TargetRevision}" : string.Empty);
}
internal sealed record ReviewCommit(string Sha, string Subject, string Author, DateTimeOffset Timestamp, IReadOnlyList<string> Parents, string Summary);
internal sealed record BranchFileComparison(string BaseRevision, string TargetRevision, IReadOnlyList<ReviewFile> Files);
internal sealed record ReviewSnapshot(string Worktree, string? Head, string? MainHead, IReadOnlyList<ReviewFile> Working, IReadOnlyList<ReviewCommit> Commits, bool IsStale, string? Error, ReviewStopReason StopReason = ReviewStopReason.None, bool WasSuccessful = false);
internal sealed record ReviewDiff(ReviewDiffState State, string? Patch, string? Error = null);
internal sealed record ReviewDocument(string Before, string After, string Comparison,
    bool BeforeBom, bool AfterBom, string BeforeEol, string AfterEol,
    bool BeforeFinalNewline, bool AfterFinalNewline, bool RawFilter,
    string? BaseRevision = null, string? TargetRevision = null,
    bool BeforeExists = true, bool AfterExists = true);

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

    private async Task<string> RunStatusWithoutFiltersAsync(string root, CancellationToken token)
    {
        // Git status may invoke clean/process filters while comparing changed worktree bytes.
        // Enumerate configured filter commands, then override only their executable hooks
        // for this single read. No user config or index is modified.
        var config = await RunAsync(root, ["config", "--null", "--list"], token);
        var keys = config.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('\n', 2)[0])
            .Where(key => key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) &&
                (key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase) ||
                 key.EndsWith(".process", StringComparison.OrdinalIgnoreCase)))
            // Git driver subsection names are case-sensitive (Alpha != alpha).
            // Collapsing them leaves a configured clean/process command executable.
            .Distinct(StringComparer.Ordinal).ToArray();
        var args = new List<string>();
        foreach (var key in keys)
        {
            args.Add("-c"); args.Add(key + "=");
            var suffix = key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase) ? ".clean" : ".process";
            var driver = key["filter.".Length..^suffix.Length];
            args.Add("-c"); args.Add("filter." + driver + ".required=false");
        }
        args.AddRange(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"]);
        return await RunAsync(root, args, token);
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
            head = await TryReadHeadAsync(root, readToken).ConfigureAwait(false);
            var status = await RunStatusWithoutFiltersAsync(root, readToken).ConfigureAwait(false);
            working = ParseStatus(status);
            if (head is null) return new(root, null, null, working, [], false, "HEAD has no commit yet; staged files can still be reviewed.");
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

    internal async Task<BranchFileComparison> GetBranchFilesAsync(string worktree, string mainBranch,
        string expectedHead, string expectedMainHead, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(mainBranch) || mainBranch.StartsWith('-') || mainBranch.Contains('\0') ||
            !IsOid(expectedHead) || !IsOid(expectedMainHead))
            throw new InvalidDataException("Branch changes need a valid configured local main and selected HEAD.");
        var root = Path.GetFullPath(worktree);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var readToken = deadline.Token;
        try
        {
            var local = $"refs/heads/{mainBranch}^{{commit}}";
            var head = (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim();
            string main;
            try { main = (await RunAsync(root, ["rev-parse", "--verify", local], readToken)).Trim(); }
            catch (InvalidOperationException) { throw new InvalidDataException("Configured local main is unavailable."); }
            if (head != expectedHead || main != expectedMainHead)
                throw new InvalidDataException("Branch refs changed; refresh changes before selecting a file.");
            string rawBases;
            try { rawBases = await RunAsync(root, ["merge-base", "--all", head, main], readToken); }
            catch (InvalidOperationException) { throw new InvalidDataException("Branch changes have no unique local-main merge base."); }
            var bases = rawBases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bases.Length != 1 || !IsOid(bases[0]))
                throw new InvalidDataException("Branch changes have no unique local-main merge base.");
            var basis = bases[0];
            var raw = await RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M", basis, head, "--"], readToken);
            var tokens = raw.Split('\0');
            var files = new List<ReviewFile>();
            for (var i = 0; i < tokens.Length && tokens[i].Length > 0;)
            {
                if (files.Count == 1000) throw new InvalidDataException("Branch changes exceed the 1000-file review limit.");
                var code = tokens[i++];
                var renamed = code.StartsWith('R') || code.StartsWith('C');
                if (code.Length == 0 || i + (renamed ? 1 : 0) >= tokens.Length)
                    throw new InvalidDataException("Malformed branch changes from Git.");
                var old = renamed ? tokens[i++] : null;
                var path = tokens[i++];
                if (path.Length == 0 || old is "") throw new InvalidDataException("Malformed branch file path from Git.");
                files.Add(new ReviewFile(path, old, ReviewGroup.Branch, ParseStatusCode(code[0]),
                    Comparison: $"base {basis[..12]} → tip {head[..12]}", BaseRevision: basis, TargetRevision: head));
            }
            if (head != (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim() ||
                main != (await RunAsync(root, ["rev-parse", "--verify", local], readToken)).Trim())
                throw new InvalidDataException("Branch refs changed during file discovery.");
            return new BranchFileComparison(basis, head, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Branch file discovery exceeded its twelve-second budget."); }
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
        if (file.Group != ReviewGroup.Staged || file.Status is not (ReviewStatus.Modified or ReviewStatus.Added or ReviewStatus.Deleted or ReviewStatus.Renamed) ||
            Path.IsPathFullyQualified(file.Path) || file.Path.Contains('\\') ||
            file.Path.Split('/').Any(segment => segment is "" or "." or "..") || file.Path.Contains('\0'))
            throw new InvalidDataException("Selected staged file is not a regular tracked text path.");
        var root = Path.GetFullPath(worktree);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var readToken = deadline.Token;
        try
        {
            var head = await TryReadHeadAsync(root, readToken);
            if (head is null && file.Status != ReviewStatus.Added)
                throw new InvalidDataException("Selected staged file needs a committed HEAD.");
            var oldPath = file.OldPath ?? file.Path;
            var beforeEntry = head is null ? null : await TryReadTreeEntryAsync(root, head, oldPath, readToken);
            var afterEntry = await TryReadIndexEntryAsync(root, file.Path, readToken);
            if ((beforeEntry is null && file.Status != ReviewStatus.Added) ||
                (beforeEntry is not null && file.Status == ReviewStatus.Added) ||
                (afterEntry is null && file.Status != ReviewStatus.Deleted) ||
                (afterEntry is not null && file.Status == ReviewStatus.Deleted))
                throw new InvalidDataException("Staged file changed or has an unsupported entry; refresh review.");
            var before = DecodeReviewBytes(beforeEntry is null ? [] : await RunBytesAsync(root, ["cat-file", "blob", beforeEntry], readToken));
            var after = DecodeReviewBytes(afterEntry is null ? [] : await RunBytesAsync(root, ["cat-file", "blob", afterEntry], readToken));
            var attribute = await RunAsync(root, ["check-attr", "-z", "filter", "--", file.Path], readToken);
            var parts = attribute.Split('\0');
            var rawFilter = parts.Length >= 3 && parts[1] == "filter" && parts[2] is not ("unspecified" or "unset" or "");
            if (head != await TryReadHeadAsync(root, readToken) ||
                beforeEntry != (head is null ? null : await TryReadTreeEntryAsync(root, head, oldPath, readToken)) ||
                afterEntry != await TryReadIndexEntryAsync(root, file.Path, readToken))
                throw new InvalidDataException("Staged file changed while loading; refresh review.");
            return new ReviewDocument(before.Text, after.Text, "HEAD → index",
                before.Bom, after.Bom, before.Eol, after.Eol, before.FinalNewline, after.FinalNewline, rawFilter,
                BeforeExists: beforeEntry is not null, AfterExists: afterEntry is not null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Diff unavailable: staged snapshot exceeded its twelve-second budget."); }
    }

    internal async Task<ReviewDocument> GetReviewDocumentAsync(string worktree, ReviewFile file, string? mainBranch, CancellationToken token = default)
    {
        if (file.Group == ReviewGroup.Staged) return await GetStagedDocumentAsync(worktree, file, token);
        if (file.Status == ReviewStatus.Conflicted || file.Path.Contains('\0') || Path.IsPathFullyQualified(file.Path) ||
            file.Path.Contains('\\') || file.Path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Diff unavailable: unsafe or conflicted file.");
        var root = Path.GetFullPath(worktree);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var readToken = deadline.Token;
        try
        {
            string? beforeOid = null, afterOid = null;
            string? afterText = null;
            var basis = string.Empty;
            var validateHead = string.Empty;
            var validateIndex = string.Empty;
            var validateMain = string.Empty;
            string? branchBase = null;
            var validateWorktree = false;
            if (file.Group == ReviewGroup.Unstaged)
            {
                if (file.Status != ReviewStatus.Untracked)
                {
                    beforeOid = await ReadIndexEntryAsync(root, file.OldPath ?? file.Path, readToken);
                    validateIndex = beforeOid;
                }
                if (file.Status == ReviewStatus.Untracked && await TryReadIndexEntryAsync(root, file.Path, readToken) is not null)
                    throw new InvalidDataException("Untracked selection is now in the index; refresh review.");
                if (file.Status == ReviewStatus.Deleted && WorktreeEntryExists(root, file.Path))
                    throw new InvalidDataException("Deleted worktree file reappeared; refresh review.");
                afterText = file.Status == ReviewStatus.Deleted ? null : file.Path;
                validateWorktree = afterText is not null;
                basis = file.Status == ReviewStatus.Untracked ? "empty → worktree" : "index → worktree";
            }
            else if (file.Group == ReviewGroup.Commit)
            {
                if (file.CommitSha is null || !IsOid(file.CommitSha)) throw new InvalidDataException("Selected commit is unavailable.");
                var parents = (await RunAsync(root, ["rev-list", "--parents", "-n", "1", file.CommitSha], readToken))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parents.Length == 0 || !string.Equals(parents[0], file.CommitSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Selected commit changed or is unavailable.");
                beforeOid = parents.Length > 1 ? await TryReadTreeEntryAsync(root, parents[1], file.OldPath ?? file.Path, readToken) : null;
                afterOid = await TryReadTreeEntryAsync(root, file.CommitSha, file.Path, readToken);
                basis = parents.Length == 1 ? "empty tree → selected commit" : "first parent → selected commit";
            }
            else if (file.Group == ReviewGroup.Branch)
            {
                if (string.IsNullOrWhiteSpace(mainBranch) || mainBranch.StartsWith('-') || mainBranch.Contains('\0'))
                    throw new InvalidDataException("Configured local main is unavailable.");
                var local = $"refs/heads/{mainBranch}";
                validateHead = (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim();
                try { validateMain = (await RunAsync(root, ["rev-parse", "--verify", local + "^{commit}"], readToken)).Trim(); }
                catch (InvalidOperationException) { throw new InvalidDataException("Configured local main is unavailable."); }
                string rawBases;
                try { rawBases = await RunAsync(root, ["merge-base", "--all", validateHead, validateMain], readToken); }
                catch (InvalidOperationException) { throw new InvalidDataException("Branch comparison has no unique local-main base."); }
                var bases = rawBases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (bases.Length != 1 || !IsOid(bases[0])) throw new InvalidDataException("Branch comparison has no unique local-main base.");
                branchBase = bases[0];
                if (file.BaseRevision is not null && file.BaseRevision != branchBase ||
                    file.TargetRevision is not null && file.TargetRevision != validateHead)
                    throw new InvalidDataException("Selected branch file belongs to an obsolete comparison; refresh review.");
                beforeOid = await TryReadTreeEntryAsync(root, branchBase, file.OldPath ?? file.Path, readToken);
                afterOid = await TryReadTreeEntryAsync(root, validateHead, file.Path, readToken);
                basis = "base → tip";
            }
            else throw new InvalidDataException("Unsupported review comparison.");
            if (file.Group is ReviewGroup.Commit or ReviewGroup.Branch && beforeOid is null && afterOid is null)
                throw new InvalidDataException("Selected file is absent from both comparison revisions.");
            var before = DecodeReviewBytes(beforeOid is null ? [] : await RunBytesAsync(root, ["cat-file", "blob", beforeOid], readToken));
            var after = DecodeReviewBytes(validateWorktree ? await ReadWorktreeBytesAsync(root, afterText!, readToken) :
                afterOid is null ? [] : await RunBytesAsync(root, ["cat-file", "blob", afterOid], readToken));
            if (validateIndex.Length != 0 && validateIndex != await ReadIndexEntryAsync(root, file.OldPath ?? file.Path, readToken))
                throw new InvalidDataException("Index changed during diff review.");
            if (file.Group == ReviewGroup.Unstaged && file.Status == ReviewStatus.Untracked &&
                await TryReadIndexEntryAsync(root, file.Path, readToken) is not null)
                throw new InvalidDataException("Untracked selection is now in the index; refresh review.");
            if (file.Group == ReviewGroup.Unstaged && file.Status == ReviewStatus.Deleted && WorktreeEntryExists(root, file.Path))
                throw new InvalidDataException("Deleted worktree file reappeared; refresh review.");
            if (validateHead.Length != 0 && (validateHead != (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], readToken)).Trim() ||
                validateMain != (await RunAsync(root, ["rev-parse", "--verify", $"refs/heads/{mainBranch}^{{commit}}"], readToken)).Trim()))
                throw new InvalidDataException("Branch refs changed during diff review.");
            var attribute = await RunAsync(root, ["check-attr", "-z", "filter", "--", file.Path], readToken);
            var parts = attribute.Split('\0');
            var rawFilter = parts.Length >= 3 && parts[1] == "filter" && parts[2] is not ("unspecified" or "unset" or "");
            return new ReviewDocument(before.Text, after.Text, basis, before.Bom, after.Bom,
                before.Eol, after.Eol, before.FinalNewline, after.FinalNewline, rawFilter,
                branchBase, file.Group == ReviewGroup.Branch ? validateHead : null,
                beforeOid is not null, validateWorktree || afterOid is not null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Diff unavailable: snapshot exceeded its twelve-second budget."); }
    }

    private async Task<string?> TryReadHeadAsync(string root, CancellationToken token)
    {
        try { return (await RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], token)).Trim(); }
        catch (InvalidOperationException)
        {
            // An unborn local branch has no HEAD object; require a real Git worktree.
            _ = await RunAsync(root, ["symbolic-ref", "-q", "HEAD"], token);
            return null;
        }
    }

    private async Task<string?> TryReadIndexEntryAsync(string root, string path, CancellationToken token)
    {
        var record = await RunAsync(root, ["ls-files", "--stage", "-z", "--", path], token);
        return record.Length == 0 ? null : await ReadIndexEntryAsync(root, path, token);
    }

    private async Task<string?> TryReadTreeEntryAsync(string root, string tree, string path, CancellationToken token)
    {
        var record = await RunAsync(root, ["ls-tree", "-z", tree, "--", path], token);
        if (record.Length == 0) return null;
        return await ReadTreeEntryAsync(root, tree, path, token);
    }

    private static bool WorktreeEntryExists(string root, string relativePath)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe worktree path.");
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task<byte[]> ReadWorktreeBytesAsync(string root, string relativePath, CancellationToken token)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe worktree file path.");
        for (var current = path; current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Diff unavailable: symbolic link or junction is not a regular file.");
            if (current == prefix.TrimEnd(Path.DirectorySeparatorChar)) break;
        }
        var before = new FileInfo(path);
        if (!before.Exists || before.Length > 1024 * 1024) throw new InvalidDataException("Diff unavailable: file is absent or oversized.");
        byte[] bytes;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            EnsureHandleWithinRoot(stream, prefix);
            bytes = new byte[checked((int)before.Length)];
            await stream.ReadExactlyAsync(bytes, token);
            if (await stream.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("File changed during review.");
        }
        var after = new FileInfo(path);
        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("File changed during review.");
        // A same-size rewrite can preserve timestamps; compare a second bounded read.
        var verify = new byte[bytes.Length];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            EnsureHandleWithinRoot(stream, prefix);
            await stream.ReadExactlyAsync(verify, token);
            if (await stream.ReadAsync(new byte[1], token) != 0 || !bytes.AsSpan().SequenceEqual(verify))
                throw new InvalidDataException("File changed during review.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("File changed during review.");
        return bytes;
    }

    private static void EnsureHandleWithinRoot(FileStream stream, string rootPrefix)
    {
        var path = new StringBuilder(32768);
        var written = GetFinalPathNameByHandleW(stream.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (written == 0 || written >= path.Capacity)
            throw new InvalidDataException("Diff unavailable: could not verify opened file path.");
        var resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diff unavailable: opened file escaped the selected worktree.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        StringBuilder path, uint length, uint flags);

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
