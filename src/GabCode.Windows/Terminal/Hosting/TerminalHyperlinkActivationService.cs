using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalHyperlinkActivationService
{
    private static readonly Regex LineColumnSuffix = new(@":(?<line>\d+)(?::(?<column>\d+))?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string launchDirectory;

    internal TerminalHyperlinkActivationService(string launchDirectory) =>
        this.launchDirectory = Path.GetFullPath(launchDirectory);

    internal bool TryActivate(string? target)
    {
        if (!TryResolveTarget(target, out var resolved))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = resolved,
                UseShellExecute = true,
            });
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal bool TryResolveTarget(string? target, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(target) || target.Contains('\r') || target.Contains('\n')) return false;
        target = target.Trim();

        if (!Path.IsPathRooted(target) && Uri.TryCreate(target, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
        {
            if (uri.Scheme is "http" or "https")
            {
                resolved = uri.AbsoluteUri;
                return true;
            }
            if (!uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                // A colon in a relative Windows reference may be a line suffix (file.cs:12).
                if (!LineColumnSuffix.IsMatch(target)) return false;
            }
            else
            {
                if (!uri.IsLoopback && !string.IsNullOrEmpty(uri.Host)) return false;
                target = uri.LocalPath;
            }
        }

        target = LineColumnSuffix.Replace(target, string.Empty);
        if (string.IsNullOrWhiteSpace(target) || target.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        var path = Path.IsPathRooted(target) ? target : Path.Combine(launchDirectory, target);
        try { path = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        resolved = path;
        return true;
    }
}
