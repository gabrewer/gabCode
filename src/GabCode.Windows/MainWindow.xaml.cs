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
        WorktreePathText.ToolTip = this.workingDirectory;
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
        piTerminal?.FocusTerminal();
    }

    internal void ShowCommandsInMain()
    {
        if (terminalLayout is null)
        {
            return;
        }

        terminalLayout.ShowCommandsInMain();
        commandsTerminal?.FocusTerminal();
    }

    private void CreateTerminalWorkspace()
    {
        piTerminal = new TerminalSessionView(TerminalSessionKind.First, workingDirectory, profileResolver.Resolve);
        commandsTerminal = new TerminalSessionView(TerminalSessionKind.Second, workingDirectory, profileResolver.Resolve);
        terminalLayout = new RetainedTerminalLayout(MainTerminalRegion, BottomTerminalRegion, piTerminal, commandsTerminal);
        ShowPiInMain();
    }

    private void ShowWorkingDirectoryFailure()
    {
        TerminalWorkspace.Visibility = Visibility.Collapsed;
        SwapTerminalsButton.IsEnabled = false;
        WorktreeFailureMessage.Text = $"The controlled terminal directory does not exist or is unavailable: {workingDirectory}";
        WorktreeFailureSurface.Visibility = Visibility.Visible;
    }

    private void SwapTerminalsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPiInMain)
        {
            ShowCommandsInMain();
        }
        else
        {
            ShowPiInMain();
        }
    }

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
            if (focusedElement is not null)
            {
                _ = Dispatcher.BeginInvoke(
                    () => Keyboard.Focus(focusedElement),
                    DispatcherPriority.Input);
            }

            return;
        }

        closeInProgress = true;
        IsEnabled = false;
        _ = CloseSessionsAndWindowAsync();
    }

    private async Task CloseSessionsAndWindowAsync()
    {
        try
        {
            await Task.WhenAll(
                piTerminal?.CloseAsync() ?? Task.CompletedTask,
                commandsTerminal?.CloseAsync() ?? Task.CompletedTask);
            allowClose = true;
            Close();
        }
        catch (Exception)
        {
            closeInProgress = false;
            IsEnabled = true;
            _ = MessageBox.Show(
                this,
                "gabCode could not confirm that every terminal process stopped. The window will remain open so cleanup can be retried.",
                "Terminal cleanup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
