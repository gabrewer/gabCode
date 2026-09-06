using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class StartupWorkspaceSelectionTests
{
    [Fact]
    public void Empty_argument_suppresses_remembered_workspace()
    {
        var selection = StartupWorkspaceSelection.Resolve(["--empty"]);

        Assert.True(selection.IsExplicitEmpty);
        Assert.Null(selection.WorkspacePath);
    }

    [Fact]
    public void Plain_startup_restores_only_for_the_first_instance()
    {
        var selection = StartupWorkspaceSelection.Resolve([]);

        Assert.True(selection.ShouldRestoreRememberedWorkspace(isFirstInstance: true));
        Assert.False(selection.ShouldRestoreRememberedWorkspace(isFirstInstance: false));
    }

    [Fact]
    public void Explicit_workspace_never_uses_remembered_restore()
    {
        var selection = StartupWorkspaceSelection.Resolve(["C:\\work\\demo.gabcode-workspace"]);

        Assert.False(selection.ShouldRestoreRememberedWorkspace(isFirstInstance: true));
        Assert.False(selection.ShouldRestoreRememberedWorkspace(isFirstInstance: false));
    }

    [Fact]
    public void Single_descriptor_argument_is_selected()
    {
        var selection = StartupWorkspaceSelection.Resolve(["C:\\work\\demo.gabcode-workspace"]);

        Assert.False(selection.IsExplicitEmpty);
        Assert.Equal("C:\\work\\demo.gabcode-workspace", selection.WorkspacePath);
    }

    [Fact]
    public void Multiple_arguments_are_not_interpreted_as_workspace()
    {
        Assert.Throws<ArgumentException>(() => StartupWorkspaceSelection.Resolve(["one", "two"]));
    }
}
