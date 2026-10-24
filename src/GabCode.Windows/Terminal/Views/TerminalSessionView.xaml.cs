using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Controls;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Settings;

namespace GabCode.Windows.Terminal.Views;

internal partial class TerminalSessionView : UserControl, IAsyncDisposable
{
    private readonly TerminalSessionKind kind;
    private readonly string workingDirectory;
    private readonly Func<TerminalProfileResolution> resolveProfile;
    private readonly Func<TerminalFontSelection> resolveFont;
    private TerminalHostedSession? session;
    private TerminalSessionState state = TerminalSessionState.Created;
    private Exception? localFailure;
    private bool startAttempted;
    private bool retrying;

    internal TerminalSessionView(
        TerminalSessionKind kind,
        string workingDirectory,
        Func<TerminalProfileResolution> resolveProfile,
        Func<TerminalFontSelection>? resolveFont = null)
    {
        this.kind = kind;
        this.workingDirectory = workingDirectory;
        this.resolveProfile = resolveProfile ?? throw new ArgumentNullException(nameof(resolveProfile));
        this.resolveFont = resolveFont ?? (() => TerminalFontSelection.Default);
        InitializeComponent();
        var terminalName = kind.GetDisplayName();
        AutomationProperties.SetName(this, terminalName);
        AutomationProperties.SetName(RetryButton, $"Retry {terminalName}");
        SetProfileStatus("Shell profile not resolved");
        SetStateStatus("Not started");
        Loaded += TerminalSessionView_Loaded;
        SizeChanged += TerminalSessionView_SizeChanged;
    }

    internal event EventHandler? SessionChanged;

    internal TerminalSessionState State => state;

    internal int? ProcessId => session?.ProcessId;

    internal bool IsActive => session?.IsActive is true;

    internal bool HasStarted => startAttempted;

    internal Exception? Failure => session?.Failure ?? localFailure;

    internal string ProfileStatusMessage => ProfileStatusText.Text;

    internal TerminalHostedSession? Session => session;

    internal FrameworkElement? TerminalControlInstance => session?.Control;

    internal async Task EnsureStartedAsync()
    {
        if (startAttempted || retrying)
        {
            return;
        }

        startAttempted = true;
        await StartNewSessionAsync();
    }

    internal async Task RetryAsync()
    {
        if (retrying || state != TerminalSessionState.Failed)
        {
            return;
        }

        var retryFocusRequested = RetryButton.IsKeyboardFocused;
        var owner = Window.GetWindow(this);
        var nativeFocusAtRetry = NativeFocusHandle();
        var superseded = false;
        EventHandler? onDeactivated = (_, _) => superseded = true;
        KeyboardFocusChangedEventHandler? onFocus = (_, args) =>
        {
            if (args.NewFocus is DependencyObject next && !ReferenceEquals(next, owner) &&
                !ReferenceEquals(next, this) && !IsAncestorOf(next)) superseded = true;
        };
        if (retryFocusRequested && owner is not null)
        {
            owner.Deactivated += onDeactivated;
            Keyboard.AddGotKeyboardFocusHandler(owner, onFocus);
        }
        retrying = true;
        RetryButton.IsEnabled = false;
        try
        {
            if (session is not null)
            {
                session.StateChanged -= Session_StateChanged;
                await session.CloseAsync();
            }

            TerminalSurfaceHost.Content = null;
            session = null;
            localFailure = null;
            startAttempted = true;
            await StartNewSessionAsync();
            var source = owner is null ? null : PresentationSource.FromVisual(owner) as System.Windows.Interop.HwndSource;
            var nativeFocus = NativeFocusHandle();
            if (retryFocusRequested && !superseded && owner is { IsActive: true } && source is not null &&
                NativeForegroundWindow() == source.Handle &&
                (nativeFocus == nativeFocusAtRetry || nativeFocus == source.Handle || nativeFocus == IntPtr.Zero || session?.HasNativeFocus is true))
                TryFocusReadyTerminal();
        }
        finally
        {
            if (retryFocusRequested && owner is not null)
            {
                owner.Deactivated -= onDeactivated;
                Keyboard.RemoveGotKeyboardFocusHandler(owner, onFocus);
            }
            retrying = false;
            RetryButton.IsEnabled = true;
        }
    }

    internal async Task CloseAsync()
    {
        if (session is null)
        {
            return;
        }

        await session.CloseAsync();
        UpdateState(session.State);
    }

    internal void FocusTerminal()
    {
        if (session is null)
        {
            return;
        }

        _ = session.Focus();
    }

    internal bool TryFocusReadyTerminal()
    {
        return state == TerminalSessionState.Running && session?.Control.IsLoaded is true && session.Focus();
    }

    internal bool TryFocusFailedRetry() => state == TerminalSessionState.Failed && RetryButton.IsVisible && RetryButton.IsEnabled && RetryButton.Focus();

    internal void ApplyFont(TerminalFontSelection selection) => session?.ApplyFont(selection);

    internal void RefreshLayout() => session?.RefreshLayout();

    public async ValueTask DisposeAsync()
    {
        Loaded -= TerminalSessionView_Loaded;
        SizeChanged -= TerminalSessionView_SizeChanged;
        if (session is not null)
        {
            session.StateChanged -= Session_StateChanged;
            await session.DisposeAsync();
        }
    }

    private async Task StartNewSessionAsync()
    {
        UpdateState(TerminalSessionState.Starting);
        FailureSurface.Visibility = Visibility.Collapsed;
        TerminalSurfaceHost.Visibility = Visibility.Visible;
        try
        {
            var profile = resolveProfile();
            SetProfileStatus(profile.StatusMessage);
            session = new TerminalHostedSession(kind, workingDirectory, profile, resolveFont());
            session.StateChanged += Session_StateChanged;
            TerminalSurfaceHost.Content = session.Control;
            await session.StartAsync();
            UpdateState(session.State);
        }
        catch (Exception exception)
        {
            localFailure = exception;
            UpdateState(TerminalSessionState.Failed);
            ShowFailure(exception);
        }
    }

    private void Session_StateChanged(object? sender, TerminalSessionState nextState)
    {
        UpdateState(nextState);
        if (nextState == TerminalSessionState.Failed)
        {
            ShowFailure(session?.Failure ?? new InvalidOperationException("The terminal transport failed."));
        }
    }

    private void UpdateState(TerminalSessionState nextState)
    {
        state = nextState;
        SetStateStatus(nextState switch
        {
            TerminalSessionState.Created => "Not started",
            TerminalSessionState.Starting => "Starting",
            TerminalSessionState.Running => "Ready",
            TerminalSessionState.Exited => "Exited",
            TerminalSessionState.Failed => "Failed",
            TerminalSessionState.Closing => "Closing",
            TerminalSessionState.Closed => "Closed",
            _ => nextState.ToString(),
        });
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetProfileStatus(string status)
    {
        ProfileStatusText.Text = status;
        AutomationProperties.SetName(ProfileStatusText, $"Shell profile status: {status}");
    }

    private void SetStateStatus(string status)
    {
        SessionStateText.Text = status;
        AutomationProperties.SetName(SessionStateText, $"{kind.GetDisplayName()} lifecycle: {status}");
    }

    private void ShowFailure(Exception exception)
    {
        var failedTerminalOwnedFocus = session?.HasNativeFocus is true;
        FailureMessageText.Text = exception.Message;
        TerminalSurfaceHost.Visibility = Visibility.Collapsed;
        FailureSurface.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        if (failedTerminalOwnedFocus)
        {
            var owner = Window.GetWindow(this);
            var nativeFocusAtQueue = NativeFocusHandle();
            var interrupted = false;
            EventHandler? onDeactivated = (_, _) => interrupted = true;
            if (owner is not null) owner.Deactivated += onDeactivated;
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (owner is not null) owner.Deactivated -= onDeactivated;
                    var next = Keyboard.FocusedElement as DependencyObject;
                    var source = owner is null ? null : PresentationSource.FromVisual(owner) as System.Windows.Interop.HwndSource;
                    var nativeFocus = NativeFocusHandle();
                    if (!interrupted && owner is { IsActive: true } && source is not null && NativeForegroundWindow() == source.Handle &&
                        (nativeFocus == nativeFocusAtQueue || nativeFocus == source.Handle || nativeFocus == IntPtr.Zero) &&
                        state == TerminalSessionState.Failed &&
                        RetryButton.IsVisible && RetryButton.IsEnabled &&
                        (next is null || ReferenceEquals(next, owner) || ReferenceEquals(next, this) || IsAncestorOf(next)))
                        _ = RetryButton.Focus();
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetFocus")]
    private static extern IntPtr NativeFocusHandle();

    private void TerminalSessionView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (session is null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        _ = Dispatcher.BeginInvoke(() => session?.RefreshLayout(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void TerminalSessionView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureStartedAsync();
        }
        catch (Exception exception)
        {
            localFailure = exception;
            UpdateState(TerminalSessionState.Failed);
            ShowFailure(exception);
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RetryAsync();
        }
        catch (Exception exception)
        {
            localFailure = exception;
            UpdateState(TerminalSessionState.Failed);
            ShowFailure(exception);
        }
    }
}
