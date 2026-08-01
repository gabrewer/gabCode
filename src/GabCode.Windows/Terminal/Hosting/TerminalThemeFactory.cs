using Microsoft.Terminal.Wpf;

namespace GabCode.Windows.Terminal.Hosting;

internal static class TerminalThemeFactory
{
    internal static TerminalTheme CreateDefault() => new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = 0xCCCCCC,
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable =
        [
            0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
            0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
            0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
            0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
        ],
    };
}
