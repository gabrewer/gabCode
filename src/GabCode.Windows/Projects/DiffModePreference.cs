using System.IO;

namespace GabCode.Windows.Projects;

/// <summary>Windows-local presentation preference; never a source for Git review data.</summary>
internal sealed class DiffModePreference
{
    private readonly string path;

    internal DiffModePreference(string? path = null) =>
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gabCode", "diff-mode.txt");

    internal string Read()
    {
        try
        {
            var value = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return value is "side-by-side" or "inline" or "file" ? value : "side-by-side";
        }
        catch (IOException) { return "side-by-side"; }
        catch (UnauthorizedAccessException) { return "side-by-side"; }
    }

    internal void Write(string mode)
    {
        if (mode is not ("side-by-side" or "inline" or "file")) throw new ArgumentOutOfRangeException(nameof(mode));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, mode);
    }
}
