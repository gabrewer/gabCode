namespace GabCode.Windows.Terminal.Conpty;

internal enum TerminalSessionState
{
    Created,
    Starting,
    Running,
    Exited,
    Failed,
    Closing,
    Closed,
}
