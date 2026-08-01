using System.Windows;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalExitConfirmationService : ITerminalExitConfirmationService
{
    public TerminalExitDecision Confirm(Window owner, int activeTerminalCount)
    {
        var dialog = new TerminalExitConfirmationDialog(activeTerminalCount)
        {
            Owner = owner,
        };
        _ = dialog.ShowDialog();
        return dialog.Decision;
    }
}
