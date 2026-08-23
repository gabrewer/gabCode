using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GabCode.Windows.Projects;

internal sealed record GitDiscoveryProgress(string Phase, int FoldersScanned, int RepositoriesFound);

internal sealed record GitWorktreeEntry(string Path, string? Branch, bool IsPrimary);

internal sealed record GitBranchReference(string Name, bool IsRemote, string? AttachedPath);

internal sealed class GitWorktreeDiscovery
{
    private const int MaximumCandidates = 2_000;
    private const int MaximumGitOutputCharacters = 64 * 1024;
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build", "coverage",
    };
    private readonly string executablePath;
    private readonly TimeSpan timeout;

    internal GitWorktreeDiscovery(string executablePath = "git", TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.executablePath = executablePath;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    internal async Task<IReadOnlyDictionary<string, string>> DiscoverAsync(string projectRoot, IProgress<GitDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var entries = await DiscoverEntriesAsync(projectRoot, progress, cancellationToken);
        return entries.Where(entry => entry.Branch is not null)
            .ToDictionary(entry => entry.Branch!, entry => entry.Path, StringComparer.Ordinal);
    }

    internal async Task<IReadOnlyList<GitWorktreeEntry>> DiscoverEntriesAsync(string projectRoot, IProgress<GitDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var repositories = new Dictionary<string, IReadOnlyList<GitWorktreeEntry>>(StringComparer.OrdinalIgnoreCase);
        var coveredWorktrees = new List<string>();
        var foldersScanned = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var candidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++foldersScanned > MaximumCandidates) throw new InvalidOperationException($"Project root contains more than {MaximumCandidates} directories to inspect.");
            progress?.Report(new GitDiscoveryProgress("Searching for Git repositories", foldersScanned, repositories.Count));
            if (ShouldSkipDirectory(candidate, root)) continue;
            if (coveredWorktrees.Any(path => candidate.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (HasGitMarker(candidate))
            {
                var output = await RunAsync(candidate, cancellationToken);
                if (output is null) continue;
                var entries = ParseEntries(output);
                if (entries.Count == 0) continue;
                var identity = entries[0].Path;
                if (repositories.TryAdd(identity, entries))
                {
                    coveredWorktrees.AddRange(entries.Select(entry => entry.Path));
                    progress?.Report(new GitDiscoveryProgress("Resolving Git worktrees", foldersScanned, repositories.Count));
                }
                continue;
            }
            foreach (var child in Directory.EnumerateDirectories(candidate))
            {
                if (!ShouldSkipDirectory(child, root)) pending.Push(child);
            }
        }
        if (repositories.Count != 1) throw new InvalidOperationException(repositories.Count == 0 ? $"No Git repository was found beneath '{root}'." : $"More than one Git repository was found beneath '{root}'.");
        progress?.Report(new GitDiscoveryProgress("Git worktrees resolved", foldersScanned, repositories.Count));
        return repositories.Values.Single();
    }

    internal async Task<string> ResolveAsync(string projectRoot, string branch, IProgress<GitDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var entries = await DiscoverEntriesAsync(projectRoot, progress, cancellationToken);
        var matches = entries.Where(entry => string.Equals(entry.Branch, branch, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0 ? $"No registered worktree exists for branch '{branch}'." : $"Multiple registered worktrees exist for branch '{branch}'.");
        return matches[0].Path;
    }

    internal async Task<bool> HasUsableRemoteAsync(string projectRoot, string branch, CancellationToken cancellationToken = default)
    {
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var remote = await RunGitAsync(primary, ["config", "--get", $"branch.{branch}.remote"], cancellationToken);
        var merge = await RunGitAsync(primary, ["config", "--get", $"branch.{branch}.merge"], cancellationToken);
        return remote.ExitCode == 0 && merge.ExitCode == 0 &&
            !string.IsNullOrWhiteSpace(remote.StandardOutput.Trim()) && remote.StandardOutput.Trim() != "." &&
            merge.StandardOutput.Trim().StartsWith("refs/heads/", StringComparison.Ordinal);
    }

    internal async Task<IReadOnlyList<GitBranchReference>> RefreshRemoteBranchesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var fetch = await RunGitAsync(primary, ["fetch", "--all", "--prune"], cancellationToken);
        if (fetch.ExitCode != 0) throw GitFailure("Refreshing remote branches failed.", fetch);
        return await ListBranchesAsync(projectRoot, cancellationToken);
    }

    internal async Task<IReadOnlyList<GitBranchReference>> ListBranchesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var result = await RunGitAsync(primary, ["for-each-ref", "--format=%(refname)\t%(worktreepath)", "refs/heads", "refs/remotes"], cancellationToken);
        if (result.ExitCode != 0) throw GitFailure("Git could not list branches.", result);

        var attached = entries.Where(entry => entry.Branch is not null)
            .ToDictionary(entry => entry.Branch!, entry => entry.Path, StringComparer.Ordinal);
        var branches = new List<GitBranchReference>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 0) continue;
            var reference = parts[0];
            if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var name = reference[11..];
                branches.Add(new GitBranchReference(name, false, attached.GetValueOrDefault(name)));
            }
            else if (reference.StartsWith("refs/remotes/", StringComparison.Ordinal) && !reference.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                branches.Add(new GitBranchReference(reference[13..], true, null));
            }
        }
        return branches.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<string?> ValidateNewWorktreeAsync(string projectRoot, string branch, string path, bool allowExistingLocalBranch = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branch)) return "Enter a branch name.";
        if (string.IsNullOrWhiteSpace(path)) return "Choose a worktree location.";
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var normalizedPath = WorktreePath.Normalize(path);
        if (entries.Any(entry => WorktreePath.Comparer.Equals(entry.Path, normalizedPath))) return $"A worktree is already registered at '{normalizedPath}'.";
        if (Directory.Exists(normalizedPath) || File.Exists(normalizedPath)) return $"The worktree folder already exists: '{normalizedPath}'.";
        if (entries.Any(entry => string.Equals(entry.Branch, branch, StringComparison.Ordinal))) return $"Branch '{branch}' is already attached to a worktree.";
        var check = await RunGitAsync(primary, ["check-ref-format", "--branch", branch], cancellationToken);
        if (check.ExitCode != 0) return $"Invalid branch name '{branch}'.";
        if (allowExistingLocalBranch) return null;
        var existing = await RunGitAsync(primary, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
        return existing.ExitCode == 0 ? $"Branch '{branch}' already exists. Use Create worktree from existing branch." : null;
    }

    internal async Task<IReadOnlyList<GitWorktreeEntry>> CreateWorktreeAsync(
        string projectRoot,
        string baseBranch,
        string branch,
        string path,
        bool fetchLatest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var normalizedPath = WorktreePath.Normalize(path);
        if (entries.Any(entry => WorktreePath.Comparer.Equals(entry.Path, normalizedPath)))
            throw new InvalidOperationException($"A worktree is already registered at '{normalizedPath}'.");
        if (Directory.Exists(normalizedPath) || File.Exists(normalizedPath))
            throw new InvalidOperationException($"The worktree folder already exists: '{normalizedPath}'.");
        if (entries.Any(entry => string.Equals(entry.Branch, branch, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Branch '{branch}' is already attached to a worktree.");

        var baseRef = baseBranch;
        if (fetchLatest)
        {
            var remote = await RunGitAsync(primary, ["config", "--get", $"branch.{baseBranch}.remote"], cancellationToken);
            var merge = await RunGitAsync(primary, ["config", "--get", $"branch.{baseBranch}.merge"], cancellationToken);
            if (remote.ExitCode == 0 && merge.ExitCode == 0)
            {
                var remoteName = remote.StandardOutput.Trim();
                var mergeRef = merge.StandardOutput.Trim();
                if (!string.IsNullOrWhiteSpace(remoteName) && remoteName != "." && mergeRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    var remoteBranch = mergeRef[11..];
                    var fetch = await RunGitAsync(primary, ["fetch", remoteName, $"{mergeRef}:refs/remotes/{remoteName}/{remoteBranch}"], cancellationToken);
                    if (fetch.ExitCode != 0) throw GitFailure("Fetching the latest workspace branch failed.", fetch);
                    baseRef = $"{remoteName}/{remoteBranch}";
                }
            }
        }

        var check = await RunGitAsync(primary, ["check-ref-format", "--branch", branch], cancellationToken);
        if (check.ExitCode != 0) throw new InvalidOperationException($"Invalid branch name '{branch}'.");
        var add = await RunGitAsync(primary, ["worktree", "add", "-b", branch, normalizedPath, baseRef], cancellationToken);
        if (add.ExitCode != 0) throw GitFailure($"Could not create worktree '{normalizedPath}'.", add);
        return await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
    }

    internal async Task<IReadOnlyList<GitWorktreeEntry>> CreateExistingWorktreeAsync(
        string projectRoot,
        string localBranch,
        string sourceRef,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var primary = entries.First().Path;
        var normalizedPath = WorktreePath.Normalize(path);
        if (entries.Any(entry => WorktreePath.Comparer.Equals(entry.Path, normalizedPath)))
            throw new InvalidOperationException($"A worktree is already registered at '{normalizedPath}'.");
        if (Directory.Exists(normalizedPath) || File.Exists(normalizedPath))
            throw new InvalidOperationException($"The worktree folder already exists: '{normalizedPath}'.");
        if (entries.Any(entry => string.Equals(entry.Branch, localBranch, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Branch '{localBranch}' is already attached to a worktree.");
        var check = await RunGitAsync(primary, ["check-ref-format", "--branch", localBranch], cancellationToken);
        if (check.ExitCode != 0) throw new InvalidOperationException($"Invalid branch name '{localBranch}'.");

        var localBranchExists = await RunGitAsync(primary, ["show-ref", "--verify", "--quiet", $"refs/heads/{localBranch}"], cancellationToken);
        var arguments = new List<string> { "worktree", "add" };
        if (localBranchExists.ExitCode == 0)
        {
            if (!string.Equals(sourceRef, localBranch, StringComparison.Ordinal))
                throw new InvalidOperationException($"Local branch '{localBranch}' already exists; select it directly instead of remote branch '{sourceRef}'.");
            arguments.Add(normalizedPath);
            arguments.Add(localBranch);
        }
        else
        {
            arguments.Add("-b");
            arguments.Add(localBranch);
            arguments.Add(normalizedPath);
            arguments.Add(sourceRef);
        }
        var add = await RunGitAsync(primary, arguments, cancellationToken);
        if (add.ExitCode != 0) throw GitFailure($"Could not attach existing branch '{localBranch}'.", add);
        return await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
    }

    internal async Task<IReadOnlyList<GitWorktreeEntry>> RemoveWorktreeAsync(
        string projectRoot,
        string path,
        bool force,
        bool deleteLocalBranch,
        bool forceBranchDelete,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
        var normalizedPath = WorktreePath.Normalize(path);
        var target = entries.SingleOrDefault(entry => WorktreePath.Comparer.Equals(entry.Path, normalizedPath))
            ?? throw new InvalidOperationException($"No registered worktree exists at '{normalizedPath}'.");
        if (target.IsPrimary) throw new InvalidOperationException("The primary worktree cannot be deleted.");
        var primary = entries.First().Path;
        var arguments = new List<string> { "worktree", "remove" };
        if (force) arguments.Add("--force");
        arguments.Add(normalizedPath);
        var remove = await RunGitAsync(primary, arguments, cancellationToken);
        if (remove.ExitCode != 0) throw GitFailure($"Could not remove worktree '{normalizedPath}'.", remove);

        if (deleteLocalBranch && target.Branch is not null)
        {
            var branchArguments = new List<string> { "branch", forceBranchDelete ? "-D" : "-d", target.Branch };
            var delete = await RunGitAsync(primary, branchArguments, cancellationToken);
            if (delete.ExitCode != 0) throw GitFailure($"Worktree was removed, but local branch '{target.Branch}' could not be deleted.", delete);
        }
        return await DiscoverEntriesAsync(projectRoot, cancellationToken: cancellationToken);
    }

    private static InvalidOperationException GitFailure(string prefix, GitProcessResult result) =>
        new($"{prefix} {result.StandardError.Trim()}".Trim());

    internal static IReadOnlyList<GitWorktreeEntry> ParseEntries(string output)
    {
        var result = new List<GitWorktreeEntry>();
        var worktreeOrdinal = 0;
        foreach (var block in output.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? worktree = null;
            string? branch = null;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal)) worktree = WorktreePath.Normalize(line[9..]);
                else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = line[18..];
            }
            if (worktree is not null && branch is not null)
            {
                result.Add(new GitWorktreeEntry(worktree, branch, worktreeOrdinal == 0));
            }
            worktreeOrdinal++;
        }
        return result;
    }

    private static bool HasGitMarker(string directory) => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static bool ShouldSkipDirectory(string directory, string root)
    {
        if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (IgnoredDirectoryNames.Contains(name)) return true;
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private async Task<string?> RunAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(directory, ["worktree", "list", "--porcelain"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private async Task<GitProcessResult> RunGitAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var info = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
            var outputTask = ReadBoundedAsync(process.StandardOutput);
            var errorTask = ReadBoundedAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                TryStop(process);
                await Task.WhenAll(outputTask, errorTask);
                throw new TimeoutException("Git operation timed out.");
            }
            catch
            {
                TryStop(process);
                await Task.WhenAll(outputTask, errorTask);
                throw;
            }
            return new GitProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Git could not be started.");
        }
    }

    private sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0) return output.ToString();
            if (output.Length < MaximumGitOutputCharacters)
            {
                output.Append(buffer, 0, Math.Min(read, MaximumGitOutputCharacters - output.Length));
            }
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
