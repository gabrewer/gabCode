namespace GabCode.Windows.Terminal.Hosting;

internal enum TerminalSessionKind
{
    First,
    Second,
}

internal static class TerminalSessionKindExtensions
{
    internal static string GetDisplayName(this TerminalSessionKind kind) => kind switch
    {
        TerminalSessionKind.First => "Terminal 1",
        TerminalSessionKind.Second => "Terminal 2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
