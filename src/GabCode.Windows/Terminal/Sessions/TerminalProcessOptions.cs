using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace GabCode.Windows.Terminal.Conpty;

internal sealed class TerminalProcessOptions
{
    public TerminalProcessOptions(
        string executablePath,
        string arguments,
        string workingDirectory,
        TimeSpan gracefulShutdownTimeout,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (gracefulShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulShutdownTimeout));
        }

        ExecutablePath = ResolveExecutablePath(executablePath);
        Arguments = arguments;
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        GracefulShutdownTimeout = gracefulShutdownTimeout;
        EnvironmentVariables = CaptureEnvironment(environmentOverrides);
        EnvironmentBlock = BuildEnvironmentBlock(EnvironmentVariables);
    }

    public string ExecutablePath { get; }

    public string Arguments { get; }

    public string WorkingDirectory { get; }

    public TimeSpan GracefulShutdownTimeout { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    internal string EnvironmentBlock { get; }

    private static IReadOnlyDictionary<string, string> CaptureEnvironment(
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                ValidateEnvironmentVariable(name, value);
                if (value is null)
                {
                    _ = environment.Remove(name);
                }
                else
                {
                    environment[name] = value;
                }
            }
        }

        return new ReadOnlyDictionary<string, string>(environment);
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            _ = block.Append(name).Append('=').Append(value).Append('\0');
        }

        _ = block.Append('\0');
        return block.ToString();
    }

    private static void ValidateEnvironmentVariable(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=') || name.Contains('\0'))
        {
            throw new ArgumentException("Environment variable names cannot contain '=' or a null character.", nameof(name));
        }

        if (value?.Contains('\0') is true)
        {
            throw new ArgumentException("Environment variable values cannot contain a null character.", nameof(value));
        }
    }

    private static string ResolveExecutablePath(string executablePath)
    {
        if (Path.IsPathFullyQualified(executablePath))
        {
            return Path.GetFullPath(executablePath);
        }

        var extensions = Path.HasExtension(executablePath)
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in searchDirectories)
        {
            var unquotedDirectory = directory.Trim('"');
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(unquotedDirectory, executablePath + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return executablePath;
    }
}
