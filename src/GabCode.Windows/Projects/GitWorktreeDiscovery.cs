using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class GitWorktreeDiscovery
{
    private const int MaximumCandidates = 2_000;

    internal async Task<IReadOnlyDictionary<string, string>> DiscoverAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var repositories = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var candidateCount = 0;
        foreach (var candidate in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++candidateCount > MaximumCandidates) throw new InvalidOperationException($"Project root contains more than {MaximumCandidates} directories to inspect.");
            var output = await RunAsync(candidate, cancellationToken);
            if (output is null) continue;
            var branches = Parse(output);
            if (branches.Count != 0) repositories.TryAdd(branches.Values.First(), branches);
        }
        if (repositories.Count != 1) throw new InvalidOperationException(repositories.Count == 0 ? $"No Git repository was found beneath '{root}'." : $"More than one Git repository was found beneath '{root}'.");
        return repositories.Values.Single();
    }

    internal async Task<string> ResolveAsync(string projectRoot, string branch, CancellationToken cancellationToken = default)
    {
        var branches = await DiscoverAsync(projectRoot, cancellationToken);
        if (!branches.TryGetValue(branch, out var worktree)) throw new InvalidOperationException($"No registered worktree exists for branch '{branch}'.");
        return worktree;
    }

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
