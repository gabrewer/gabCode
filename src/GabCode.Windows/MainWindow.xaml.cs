using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows;

public partial class MainWindow : Window
{
    private readonly string workingDirectory;
    private readonly TerminalProfileResolver profileResolver;
    private readonly ITerminalExitConfirmationService exitConfirmation;
    private RetainedTerminalLayout? terminalLayout;
    private TerminalSessionView? piTerminal;
    private TerminalSessionView? commandsTerminal;
    private bool closeInProgress;
    private bool allowClose;

    public MainWindow()
        : this(Environment.CurrentDirectory)
    {
    }

    public MainWindow(string workingDirectory)
        : this(workingDirectory, TerminalProfileResolver.CreateDefault(), new TerminalExitConfirmationService())
    {
    }

    internal MainWindow(
        string workingDirectory,
        TerminalProfileResolver profileResolver,
        ITerminalExitConfirmationService exitConfirmation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.workingDirectory = Path.GetFullPath(workingDirectory);
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        this.exitConfirmation = exitConfirmation ?? throw new ArgumentNullException(nameof(exitConfirmation));
        InitializeComponent();
        WorktreePathText.Text = this.workingDirectory;
        Closing += MainWindow_Closing;

        if (!Directory.Exists(this.workingDirectory))
        {
            ShowWorkingDirectoryFailure();
            return;
        }

        CreateTerminalWorkspace();
    }

    internal TerminalSessionView? PiTerminal => piTerminal;

    internal TerminalSessionView? CommandsTerminal => commandsTerminal;

    internal bool IsPiInMain => terminalLayout?.IsPiInMain is true;

    internal int ActiveTerminalCount =>
        (piTerminal?.IsActive is true ? 1 : 0) + (commandsTerminal?.IsActive is true ? 1 : 0);

    internal void ShowPiInMain()
    {
        if (terminalLayout is null)
        {
            return;
        }

        terminalLayout.ShowPiInMain();
        MainRegionLabel.Text = "Main terminal — Pi";
        BottomRegionLabel.Text = "Bottom terminal — Commands";
        piTerminal?.FocusTerminal();
    }

    internal void ShowCommandsInMain()
    {
        if (terminalLayout is null)
        {
            return;
        }

        terminalLayout.ShowCommandsInMain();
        MainRegionLabel.Text = "Main terminal — Commands";
        BottomRegionLabel.Text = "Bottom terminal — Pi";
        commandsTerminal?.FocusTerminal();
    }

    private void CreateTerminalWorkspace()
    {
        piTerminal = new TerminalSessionView(TerminalSessionKind.Pi, workingDirectory, profileResolver.Resolve);
        commandsTerminal = new TerminalSessionView(TerminalSessionKind.Commands, workingDirectory, profileResolver.Resolve);
        piTerminal.SessionChanged += Terminal_SessionChanged;
        commandsTerminal.SessionChanged += Terminal_SessionChanged;
        terminalLayout = new RetainedTerminalLayout(MainTerminalRegion, BottomTerminalRegion, piTerminal, commandsTerminal);
        ShowPiInMain();
        UpdateLifecycleStatus();
    }

    private void ShowWorkingDirectoryFailure()
    {
        TerminalWorkspace.Visibility = Visibility.Collapsed;
        PiMainSelector.IsEnabled = false;
        CommandsMainSelector.IsEnabled = false;
        WorktreeFailureMessage.Text = $"The controlled terminal directory does not exist or is unavailable: {workingDirectory}";
        WorktreeFailureSurface.Visibility = Visibility.Visible;
        SetLifecycleStatus("Terminal lifecycle: directory unavailable");
    }

    private void UpdateLifecycleStatus()
    {
        if (piTerminal is null || commandsTerminal is null)
        {
            return;
        }

        SetLifecycleStatus($"Terminal lifecycle — Pi: {GetDisplayState(piTerminal.State)} · Commands: {GetDisplayState(commandsTerminal.State)}");
    }

    private static string GetDisplayState(Terminal.Conpty.TerminalSessionState state) => state switch
    {
        Terminal.Conpty.TerminalSessionState.Created => "Not started",
        Terminal.Conpty.TerminalSessionState.Starting => "Starting",
        Terminal.Conpty.TerminalSessionState.Running => "Ready",
        Terminal.Conpty.TerminalSessionState.Exited => "Exited",
        Terminal.Conpty.TerminalSessionState.Failed => "Failed",
        Terminal.Conpty.TerminalSessionState.Closing => "Closing",
        Terminal.Conpty.TerminalSessionState.Closed => "Closed",
        _ => state.ToString(),
    };

    private void Terminal_SessionChanged(object? sender, EventArgs e) => UpdateLifecycleStatus();

    private void PiMainSelector_Click(object sender, RoutedEventArgs e) => ShowPiInMain();

    private void CommandsMainSelector_Click(object sender, RoutedEventArgs e) => ShowCommandsInMain();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose || piTerminal is null || commandsTerminal is null ||
            (!piTerminal.HasStarted && !commandsTerminal.HasStarted))
        {
            return;
        }

        e.Cancel = true;
        if (closeInProgress)
        {
            return;
        }

        var focusedElement = Keyboard.FocusedElement;
        var activeCount = ActiveTerminalCount;
        if (activeCount != 0 && exitConfirmation.Confirm(this, activeCount) == TerminalExitDecision.Cancel)
        {
            SetLifecycleStatus("Terminal lifecycle: exit canceled; terminals remain active");
            if (focusedElement is not null)
            {
                _ = Dispatcher.BeginInvoke(
                    () => Keyboard.Focus(focusedElement),
                    DispatcherPriority.Input);
            }

            return;
        }

        closeInProgress = true;
        SetLifecycleStatus("Terminal lifecycle: closing terminals");
        IsEnabled = false;
        _ = CloseSessionsAndWindowAsync();
    }

    private void SetLifecycleStatus(string status)
    {
        TerminalLifecycleStatus.Text = status;
        AutomationProperties.SetName(TerminalLifecycleStatus, status);
    }

    private async Task CloseSessionsAndWindowAsync()
    {
        try
        {
            await Task.WhenAll(
                piTerminal?.CloseAsync() ?? Task.CompletedTask,
                commandsTerminal?.CloseAsync() ?? Task.CompletedTask);
            if (piTerminal is not null)
            {
                piTerminal.SessionChanged -= Terminal_SessionChanged;
            }

            if (commandsTerminal is not null)
            {
                commandsTerminal.SessionChanged -= Terminal_SessionChanged;
            }

            allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            closeInProgress = false;
            IsEnabled = true;
            SetLifecycleStatus($"Terminal cleanup failed: {exception.Message}");
            _ = MessageBox.Show(
                this,
                "gabCode could not confirm that every terminal process stopped. The window will remain open so cleanup can be retried.",
                "Terminal cleanup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
