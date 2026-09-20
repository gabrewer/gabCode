namespace GabCode.Windows.Projects;

internal sealed class WorkspaceMutationGate
{
    private bool entered;

    internal bool IsEntered => entered;

    internal bool TryEnter()
    {
        if (entered) return false;
        entered = true;
        return true;
    }

    internal void Exit()
    {
        if (!entered) throw new InvalidOperationException("Workspace mutation gate is not entered.");
        entered = false;
    }
}
