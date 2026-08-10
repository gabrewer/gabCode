using System.IO;

namespace GabCode.Windows.Projects;

internal enum SidebarSide { Left, Right }

internal sealed class SidebarSidePreference
{
    private readonly string path;
    internal SidebarSidePreference(string? path = null) => this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gabCode", "sidebar-side.txt");
    internal SidebarSide Read() => File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), "right", StringComparison.OrdinalIgnoreCase) ? SidebarSide.Right : SidebarSide.Left;
    internal void Write(SidebarSide side) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, side == SidebarSide.Right ? "right" : "left"); }
}
