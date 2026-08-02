using System.Text;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalPastePreview
{
    internal const int MaximumLines = 5;
    internal const int MaximumCharacters = 500;

    private TerminalPastePreview(string text, bool isTruncated)
    {
        Text = text;
        IsTruncated = isTruncated;
    }

    internal string Text { get; }

    internal bool IsTruncated { get; }

    internal string AccessibleDescription => IsTruncated
        ? "Pasted text preview. Preview truncated."
        : "Pasted text preview.";

    internal static TerminalPastePreview Create(string clipboardText)
    {
        ArgumentNullException.ThrowIfNull(clipboardText);

        var preview = new StringBuilder(Math.Min(clipboardText.Length, MaximumCharacters));
        var lineCount = 1;
        var index = 0;
        var truncated = false;
        while (index < clipboardText.Length)
        {
            var character = clipboardText[index];
            if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < clipboardText.Length && clipboardText[index + 1] == '\n')
                {
                    index++;
                }

                if (lineCount == MaximumLines || !TryAppend(preview, Environment.NewLine))
                {
                    truncated = true;
                    break;
                }

                lineCount++;
                index++;
                continue;
            }

            var displayed = ToVisibleText(character);
            if (!TryAppend(preview, displayed))
            {
                truncated = true;
                break;
            }

            index++;
        }

        return new TerminalPastePreview(preview.ToString(), truncated || index < clipboardText.Length);
    }

    private static bool TryAppend(StringBuilder preview, string value)
    {
        if (preview.Length + value.Length > MaximumCharacters)
        {
            return false;
        }

        _ = preview.Append(value);
        return true;
    }

    private static string ToVisibleText(char character) => character switch
    {
        '\t' => "⇥",
        '\x1B' => "␛",
        >= '\0' and < ' ' => char.ConvertFromUtf32(0x2400 + character),
        '\x7F' => "␡",
        _ => character.ToString(),
    };
}
