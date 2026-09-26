using System.IO;

namespace GabCode.Windows.Projects;

internal static class WorktreeReferenceDisplay
{
    internal static string MarkdownFileName(string path, int maximumLength = 48)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Length <= maximumLength || maximumLength < 5) return fileName;

        var extension = Path.GetExtension(fileName);
        var retainedSuffix = extension.Length < maximumLength - 2 ? extension : string.Empty;
        var availableForName = maximumLength - retainedSuffix.Length - 1;
        var prefixLength = Math.Max(1, (availableForName + 1) / 2);
        var suffixLength = Math.Max(1, availableForName - prefixLength);
        var stem = retainedSuffix.Length == 0 ? fileName : fileName[..^retainedSuffix.Length];
        if (stem.Length <= prefixLength + suffixLength) return fileName;

        return string.Concat(stem[..prefixLength], "…", stem[^suffixLength..], retainedSuffix);
    }
}
