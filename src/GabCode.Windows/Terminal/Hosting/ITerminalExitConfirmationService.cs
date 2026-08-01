using System.Windows;

namespace GabCode.Windows.Terminal.Hosting;

internal interface ITerminalExitConfirmationService
{
    TerminalExitDecision Confirm(Window owner, int activeTerminalCount);
}
