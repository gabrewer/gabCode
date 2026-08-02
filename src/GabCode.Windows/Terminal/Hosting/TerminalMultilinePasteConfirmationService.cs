using System.Windows;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalMultilinePasteConfirmationService : ITerminalMultilinePasteConfirmationService
{
    public bool Confirm(Window? owner, TerminalPastePreview preview)
    {
        var dialog = new TerminalMultilinePasteConfirmationDialog(preview);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() is true;
    }

    public void ShowClipboardReadFailure(Window? owner) => _ = MessageBox.Show(
        owner,
        "gabCode couldn’t read text from the clipboard. Nothing was pasted.",
        "Terminal paste unavailable",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    public void ShowTerminalUnavailable(Window? owner) => _ = MessageBox.Show(
        owner,
        "The terminal is no longer available. Nothing was pasted.",
        "Terminal paste unavailable",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
}
