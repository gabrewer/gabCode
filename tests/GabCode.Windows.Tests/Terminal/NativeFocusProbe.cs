using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GabCode.Windows.Tests.Terminal;

/// <summary>Observes actual foreground/focus transitions. Never activates a window or injects a key.</summary>
internal sealed class NativeFocusProbe : IDisposable
{
    private readonly Window window;
    private readonly HwndSource source;
    private readonly List<(IntPtr Foreground, IntPtr Focus, IntPtr Origin)> transitions = [];
    private bool disposed;

    internal NativeFocusProbe(Window window)
    {
        this.window = window;
        source = (HwndSource?)PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("Focus probe requires an already-shown WPF window.");
        source.AddHook(Observe);
    }

    internal IReadOnlyList<(IntPtr Foreground, IntPtr Focus, IntPtr Origin)> Transitions => transitions;

    internal bool IsForeground => GetForegroundWindow() == source.Handle;

    internal IntPtr WindowHandle => source.Handle;

    internal bool CanTargetKeyboardInput()
    {
        if (disposed || !window.IsVisible || !IsForeground) return false;
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null || Window.GetWindow(focused) != window) return false;
        var nativeFocus = GetFocus();
        // GetFocus is thread-local: only call this on the UI thread owning the shown window.
        return nativeFocus != IntPtr.Zero && (nativeFocus == source.Handle || IsChild(source.Handle, nativeFocus));
    }

    private IntPtr Observe(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_SETFOCUS / WM_KILLFOCUS / WM_ACTIVATE: record instead of claiming a key was delivered.
        if (message is 0x0007 or 0x0008 or 0x0006)
            transitions.Add((GetForegroundWindow(), GetFocus(), hwnd));
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.RemoveHook(Observe);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
}
