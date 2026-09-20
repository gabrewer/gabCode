using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorkspaceMutationGateTests
{
    [Fact]
    public void Close_workspace_excludes_other_workspace_mutations_until_released()
    {
        var gate = new WorkspaceMutationGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void Releasing_without_an_owner_is_rejected()
    {
        var gate = new WorkspaceMutationGate();

        Assert.Throws<InvalidOperationException>(gate.Exit);
    }
}
