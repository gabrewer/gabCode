using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GabCode.Windows.Projects;

internal sealed record GitDiscoveryProgress(string Phase, int FoldersScanned, int RepositoriesFound);

internal sealed class GitWorktreeDiscovery
{
    private const int MaximumCandidates = 2_000;

    internal async Task<IReadOnlyDictionary<string, string>> DiscoverAsync(string projectRoot, IProgress<GitDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var repositories = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var coveredWorktrees = new List<string>();
        var foldersScanned = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var candidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++foldersScanned > MaximumCandidates) throw new InvalidOperationException($"Project root contains more than {MaximumCandidates} directories to inspect.");
            progress?.Report(new GitDiscoveryProgress("Searching for Git repositories", foldersScanned, repositories.Count));
            if (coveredWorktrees.Any(path => candidate.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (HasGitMarker(candidate))
            {
                var output = await RunAsync(candidate, cancellationToken);
                if (output is null) continue;
                var branches = Parse(output);
                if (branches.Count == 0) continue;
                var identity = branches.Values.First();
                if (repositories.TryAdd(identity, branches))
                {
                    coveredWorktrees.AddRange(branches.Values.Distinct(StringComparer.OrdinalIgnoreCase));
                    progress?.Report(new GitDiscoveryProgress("Resolving Git worktrees", foldersScanned, repositories.Count));
                }
                continue;
            }
            foreach (var child in Directory.EnumerateDirectories(candidate)) pending.Push(child);
        }
        if (repositories.Count != 1) throw new InvalidOperationException(repositories.Count == 0 ? $"No Git repository was found beneath '{root}'." : $"More than one Git repository was found beneath '{root}'.");
        progress?.Report(new GitDiscoveryProgress("Git worktrees resolved", foldersScanned, repositories.Count));
        return repositories.Values.Single();
    }

    internal async Task<string> ResolveAsync(string projectRoot, string branch, IProgress<GitDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var branches = await DiscoverAsync(projectRoot, progress, cancellationToken);
        if (!branches.TryGetValue(branch, out var worktree)) throw new InvalidOperationException($"No registered worktree exists for branch '{branch}'.");
        return worktree;
    }

    private static bool HasGitMarker(string directory) => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static IReadOnlyDictionary<string, string> Parse(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? worktree = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) worktree = Path.GetFullPath(line[9..]);
            else if (worktree is not null && line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) result[line[18..]] = worktree;
        }
        return result;
    }

    private static async Task<string?> RunAsync(string directory, CancellationToken token)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("worktree"); info.ArgumentList.Add("list"); info.ArgumentList.Add("--porcelain");
        try { using var process = Process.Start(info) ?? throw new InvalidOperationException(); var output = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token); return process.ExitCode == 0 ? output : null; }
        catch (Win32Exception) { throw new InvalidOperationException("Git could not be started."); }
    }
}
