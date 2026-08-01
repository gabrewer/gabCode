using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalKeyboardInputForwarder : IDisposable
{
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int CharacterMessage = 0x0102;
    private const int DeadCharacterMessage = 0x0103;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const int SystemCharacterMessage = 0x0106;
    private const int SystemDeadCharacterMessage = 0x0107;
    private const int UnicodeCharacterMessage = 0x0109;
    private const int VirtualKeyF4 = 0x73;
    private const int VirtualKeyMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly FrameworkElement control;
    private readonly HwndSource source;
    private IntPtr terminalWindow;
    private bool disposed;

    internal TerminalKeyboardInputForwarder(FrameworkElement control)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        source = PresentationSource.FromVisual(control) as HwndSource
            ?? throw new InvalidOperationException("The terminal control must be attached to an HwndSource before keyboard forwarding starts.");
        source.AddHook(WndProc);
        control.GotFocus += Control_GotFocus;
        CaptureTerminalWindow();
    }

    internal void CaptureTerminalWindow()
    {
        if (disposed || terminalWindow != IntPtr.Zero)
        {
            return;
        }

        var focusedWindow = GetFocus();
        if (focusedWindow != IntPtr.Zero && IsChild(source.Handle, focusedWindow) && IsOwnedTerminalWindow(focusedWindow))
        {
            terminalWindow = focusedWindow;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        control.GotFocus -= Control_GotFocus;
        source.RemoveHook(WndProc);
        terminalWindow = IntPtr.Zero;
    }

    private void Control_GotFocus(object sender, RoutedEventArgs e) => CaptureTerminalWindow();

    private bool IsOwnedTerminalWindow(IntPtr window)
    {
        if (!GetWindowRect(window, out var windowBounds) || control.ActualWidth <= 0 || control.ActualHeight <= 0)
        {
            return false;
        }

        var controlTopLeft = control.PointToScreen(new Point(0, 0));
        var controlBottomRight = control.PointToScreen(new Point(control.ActualWidth, control.ActualHeight));
        var windowCenterX = windowBounds.Left + ((windowBounds.Right - windowBounds.Left) / 2);
        var windowCenterY = windowBounds.Top + ((windowBounds.Bottom - windowBounds.Top) / 2);
        return windowCenterX >= controlTopLeft.X && windowCenterX < controlBottomRight.X &&
            windowCenterY >= controlTopLeft.Y && windowCenterY < controlBottomRight.Y;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed || hwnd != source.Handle || !IsKeyboardMessage(message))
        {
            return IntPtr.Zero;
        }

        if (terminalWindow == IntPtr.Zero || GetFocus() != terminalWindow || IsNativeWindowCommand(message, wParam))
        {
            return IntPtr.Zero;
        }

        _ = SendMessage(terminalWindow, unchecked((uint)message), wParam, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    private static bool IsKeyboardMessage(int message) => message is
        KeyDownMessage or KeyUpMessage or CharacterMessage or DeadCharacterMessage or
        SystemKeyDownMessage or SystemKeyUpMessage or SystemCharacterMessage or
        SystemDeadCharacterMessage or UnicodeCharacterMessage;

    private static bool IsNativeWindowCommand(int message, IntPtr wParam) =>
        message == SystemKeyDownMessage && wParam == (IntPtr)VirtualKeyF4 && GetKeyState(VirtualKeyMenu) < 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr childWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
