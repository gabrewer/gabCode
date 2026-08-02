using System.Windows;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalSafePasteController
{
    private readonly ITerminalMultilinePasteConfirmationService confirmation;

    internal TerminalSafePasteController(ITerminalMultilinePasteConfirmationService confirmation)
    {
        this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    internal void Paste(Window? owner, string clipboardSnapshot, Action<string> writeInput)
    {
        ArgumentNullException.ThrowIfNull(clipboardSnapshot);
        ArgumentNullException.ThrowIfNull(writeInput);

        if (!clipboardSnapshot.Contains('\r') && !clipboardSnapshot.Contains('\n'))
        {
            writeInput(clipboardSnapshot);
            return;
        }

        var preview = TerminalPastePreview.Create(clipboardSnapshot);
        if (confirmation.Confirm(owner, preview))
        {
            writeInput(clipboardSnapshot);
        }
    }
}
