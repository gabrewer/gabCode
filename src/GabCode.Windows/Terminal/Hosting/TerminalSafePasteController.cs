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

        if (clipboardSnapshot.Contains("\x1b[200~", StringComparison.Ordinal) ||
            clipboardSnapshot.Contains("\x1b[201~", StringComparison.Ordinal) ||
            clipboardSnapshot.Contains("\u009B200~", StringComparison.Ordinal) ||
            clipboardSnapshot.Contains("\u009B201~", StringComparison.Ordinal))
        {
            confirmation.ShowUnsafePasteContent(owner);
            return;
        }

        var preview = TerminalPastePreview.Create(clipboardSnapshot);
        if (confirmation.Confirm(owner, preview))
        {
            // Keep the user payload intact while using the terminal-standard framing that
            // lets bracketed-paste-aware line editors insert it for review instead of
            // treating each embedded newline as an immediate submission.
            writeInput($"\x1b[200~{clipboardSnapshot}\x1b[201~");
        }
    }
}
