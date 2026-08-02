using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class TerminalNativePasteInterceptor : IDisposable
{
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private static readonly UIntPtr SubclassId = (UIntPtr)1;
    private readonly FrameworkElement terminalControl;
    private readonly Action<string> paste;
    private readonly Action clipboardReadFailure;
    private readonly Func<string> getSelectedText;
    private readonly SubclassProc subclassProc;
    private GCHandle selfHandle;
    private IntPtr terminalWindow;
    private bool disposed;

    internal TerminalNativePasteInterceptor(
        FrameworkElement terminalControl,
        Action<string> paste,
        Action clipboardReadFailure,
        Func<string>? getSelectedText = null)
    {
        this.terminalControl = terminalControl ?? throw new ArgumentNullException(nameof(terminalControl));
        this.paste = paste ?? throw new ArgumentNullException(nameof(paste));
        this.clipboardReadFailure = clipboardReadFailure ?? throw new ArgumentNullException(nameof(clipboardReadFailure));
        this.getSelectedText = getSelectedText ?? (() => string.Empty);
        subclassProc = WindowProcedure;
    }

    internal void Attach()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (terminalWindow != IntPtr.Zero)
        {
            return;
        }

        terminalWindow = FindOwnedTerminalWindow(terminalControl);
        if (terminalWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("The hosted terminal window was not available for paste handling.");
        }

        selfHandle = GCHandle.Alloc(this);
        if (!SetWindowSubclass(terminalWindow, subclassProc, SubclassId, GCHandle.ToIntPtr(selfHandle)))
        {
            selfHandle.Free();
            terminalWindow = IntPtr.Zero;
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "gabCode could not configure terminal paste handling.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (terminalWindow != IntPtr.Zero)
        {
            _ = RemoveWindowSubclass(terminalWindow, subclassProc, SubclassId);
            terminalWindow = IntPtr.Zero;
        }

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        if (message == WmRButtonDown)
        {
            HandleRightClick();
            return IntPtr.Zero;
        }

        if (message == WmRButtonUp)
        {
            return IntPtr.Zero;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void HandleRightClick()
    {
        try
        {
            // The pinned WPF control exposes selected text only through an API that clears the
            // native selection. On right-click that is exactly the native control's behavior;
            // retain the copy outcome ourselves before considering the no-selection paste path.
            var selectedText = getSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                Clipboard.SetText(selectedText);
                return;
            }

            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                paste(Clipboard.GetText(TextDataFormat.UnicodeText));
            }
        }
        catch (COMException)
        {
            clipboardReadFailure();
        }
    }

    private static IntPtr FindOwnedTerminalWindow(FrameworkElement control)
    {
        if (PresentationSource.FromVisual(control) is not HwndSource source)
        {
            return IntPtr.Zero;
        }

        var topLeft = control.PointToScreen(new Point(0, 0));
        var bottomRight = control.PointToScreen(new Point(control.ActualWidth, control.ActualHeight));
        var controlBounds = new Rect(topLeft, bottomRight);
        IntPtr terminal = IntPtr.Zero;
        _ = EnumChildWindows(source.Handle, (candidate, _) =>
        {
            var className = new StringBuilder(256);
            _ = GetClassName(candidate, className, className.Capacity);
            if (!string.Equals(className.ToString(), "HwndTerminalClass", StringComparison.Ordinal) ||
                !GetWindowRect(candidate, out var rectangle))
            {
                return true;
            }

            var candidateBounds = new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
            if (controlBounds.Contains(candidateBounds.TopLeft) && controlBounds.Contains(candidateBounds.BottomRight))
            {
                terminal = candidate;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return terminal;
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr subclassId, IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}
