using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class ProjectWindowRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Occupied_window_alone_requires_new_window(bool occupied, bool expectedNewWindow)
    {
        Assert.Equal(expectedNewWindow, ProjectWindowRouting.ShouldLaunchNewWindow(occupied));
    }
}
