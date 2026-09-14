using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WindowsInstancePresenceTests
{
    [Fact]
    public void Second_live_owner_is_not_the_first_instance()
    {
        var isolationScope = $"test.{Guid.NewGuid():N}";
        using var first = WindowsInstancePresence.Acquire(isolationScope);
        using var second = WindowsInstancePresence.Acquire(isolationScope);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }
}
