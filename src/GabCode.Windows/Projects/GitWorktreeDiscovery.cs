using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GabCode.Windows.Projects;

internal sealed record GitDiscoveryProgress(string Phase, int FoldersScanned, int RepositoriesFound);

internal sealed record GitWorktreeEntry(string Path, string? Branch, bool IsPrimary);

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
        info.ArgumentList.Add("worktree"); info.ArgumentList.Add("list"); info.ArgumentList.Add("--porcelain");
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
                throw new TimeoutException("Git worktree discovery timed out.");
            }
            catch
            {
                TryStop(process);
                await Task.WhenAll(outputTask, errorTask);
                throw;
            }

            var output = await outputTask;
            _ = await errorTask;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Git could not be started.");
        }
    }

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
