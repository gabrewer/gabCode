using System.Windows;

namespace GabCode.Windows.Terminal.Hosting;

internal interface ITerminalMultilinePasteConfirmationService
{
    bool Confirm(Window? owner, TerminalPastePreview preview);

    void ShowClipboardReadFailure(Window? owner)
    {
    }

    void ShowTerminalUnavailable(Window? owner)
    {
    }

    void ShowUnsafePasteContent(Window? owner)
    {
    }
}
