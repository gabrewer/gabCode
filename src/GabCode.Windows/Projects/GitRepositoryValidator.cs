using System.Diagnostics;
using System.IO;

namespace GabCode.Windows.Projects;

internal sealed class GitRepositoryValidator
{
    private readonly string executablePath;
    private readonly TimeSpan timeout;

    internal GitRepositoryValidator(string executablePath = "git", TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.executablePath = executablePath;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    internal async Task<string> FindRepositoryAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var fullFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(fullFolder))
        {
            throw new DirectoryNotFoundException(fullFolder);
        }

        var result = await RunGitAsync(fullFolder, ["-C", fullFolder, "rev-parse", "--show-toplevel"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "The selected folder is not inside a Git repository."
                    : result.Error.Trim());
        }

        var repository = result.Output.Trim();
        if (string.IsNullOrWhiteSpace(repository) || !Directory.Exists(repository))
        {
            throw new InvalidOperationException("Git did not return an accessible repository path.");
        }

        return Path.GetFullPath(repository);
    }

    private async Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return new GitProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            TryStop(process);
            throw new TimeoutException("Git repository validation timed out.");
        }
        catch
        {
            TryStop(process);
            throw;
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record GitProcessResult(int ExitCode, string Output, string Error);
}
