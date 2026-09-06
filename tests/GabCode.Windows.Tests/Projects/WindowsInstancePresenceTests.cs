using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WindowsInstancePresenceTests
{
    [Fact]
    public void Second_live_owner_is_not_the_first_instance()
    {
        using var first = WindowsInstancePresence.Acquire();
        using var second = WindowsInstancePresence.Acquire();

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }
}
