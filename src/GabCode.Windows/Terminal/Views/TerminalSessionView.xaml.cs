using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Controls;
using GabCode.Windows.Terminal.Conpty;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;

namespace GabCode.Windows.Terminal.Views;

internal partial class TerminalSessionView : UserControl, IAsyncDisposable
{
    private static WeakReference<TerminalSessionView>? focusedTerminal;
    private readonly TerminalSessionKind kind;
    private readonly string workingDirectory;
    private readonly Func<TerminalProfileResolution> resolveProfile;
    private TerminalHostedSession? session;
    private TerminalSessionState state = TerminalSessionState.Created;
    private Exception? localFailure;
    private bool startAttempted;
    private bool retrying;
    private bool terminalOwnedFocus;

    internal TerminalSessionView(
        TerminalSessionKind kind,
        string workingDirectory,
        Func<TerminalProfileResolution> resolveProfile)
    {
        this.kind = kind;
        this.workingDirectory = workingDirectory;
        this.resolveProfile = resolveProfile ?? throw new ArgumentNullException(nameof(resolveProfile));
        InitializeComponent();
        var terminalName = kind.GetDisplayName();
        AutomationProperties.SetName(this, terminalName);
        AutomationProperties.SetName(RetryButton, $"Retry {terminalName}");
        SetProfileStatus("Shell profile not resolved");
        SetStateStatus("Not started");
        Loaded += TerminalSessionView_Loaded;
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

        retrying = true;
        RetryButton.IsEnabled = false;
        try
        {
            if (session is not null)
            {
                session.StateChanged -= Session_StateChanged;
                session.Control.GotFocus -= TerminalControl_GotFocus;
                session.Control.LostFocus -= TerminalControl_LostFocus;
                await session.CloseAsync();
            }

            TerminalSurfaceHost.Content = null;
            session = null;
            localFailure = null;
            startAttempted = true;
            await StartNewSessionAsync();
        }
        finally
        {
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

        session.Focus();
        ClaimTerminalFocus();
    }

    public async ValueTask DisposeAsync()
    {
        Loaded -= TerminalSessionView_Loaded;
        if (session is not null)
        {
            session.StateChanged -= Session_StateChanged;
            session.Control.GotFocus -= TerminalControl_GotFocus;
            session.Control.LostFocus -= TerminalControl_LostFocus;
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
            session = new TerminalHostedSession(kind, workingDirectory, profile);
            session.StateChanged += Session_StateChanged;
            session.Control.GotFocus += TerminalControl_GotFocus;
            session.Control.LostFocus += TerminalControl_LostFocus;
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
        var failedTerminalOwnedFocus = terminalOwnedFocus || session?.Control.IsKeyboardFocusWithin is true;
        ReleaseTerminalFocus();
        FailureMessageText.Text = exception.Message;
        TerminalSurfaceHost.Visibility = Visibility.Collapsed;
        FailureSurface.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        if (failedTerminalOwnedFocus)
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (state == TerminalSessionState.Failed && RetryButton.IsVisible && RetryButton.IsEnabled)
                    {
                        _ = RetryButton.Focus();
                    }
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void TerminalControl_GotFocus(object sender, RoutedEventArgs e) => ClaimTerminalFocus();

    private void TerminalControl_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                var focusedElement = Keyboard.FocusedElement as DependencyObject;
                if (focusedElement is not null &&
                    !ReferenceEquals(focusedElement, this) &&
                    !IsAncestorOf(focusedElement))
                {
                    ReleaseTerminalFocus();
                }
            },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ClaimTerminalFocus()
    {
        if (focusedTerminal?.TryGetTarget(out var previous) is true && !ReferenceEquals(previous, this))
        {
            previous.terminalOwnedFocus = false;
        }

        terminalOwnedFocus = true;
        focusedTerminal = new WeakReference<TerminalSessionView>(this);
    }

    private void ReleaseTerminalFocus()
    {
        terminalOwnedFocus = false;
        if (focusedTerminal?.TryGetTarget(out var current) is true && ReferenceEquals(current, this))
        {
            focusedTerminal = null;
        }
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
