using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class SidebarSidePreferenceTests
{
    [Fact]
    public void Defaults_left_and_persists_right_in_platform_local_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gabcode-sidebar-{Guid.NewGuid():N}.txt");
        var preference = new SidebarSidePreference(path);
        Assert.Equal(SidebarSide.Left, preference.Read());
        preference.Write(SidebarSide.Right);
        Assert.Equal(SidebarSide.Right, new SidebarSidePreference(path).Read());
    }
}
