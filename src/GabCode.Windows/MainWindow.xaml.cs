using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Hosting;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Views;

namespace GabCode.Windows;

public partial class MainWindow : Window
{
    private ProjectContext? project;
    private readonly WorkspaceProjectLoader projectLoader = new();
    private readonly WorkspaceProjectCreator projectCreator = new();
    private readonly TerminalProfileResolver profileResolver;
    private readonly ITerminalExitConfirmationService exitConfirmation;
    private RetainedTerminalLayout? terminalLayout;
    private TerminalSessionView? piTerminal;
    private TerminalSessionView? commandsTerminal;
    private bool closeInProgress;
    private bool allowClose;

    public MainWindow()
        : this(null, TerminalProfileResolver.CreateDefault(), new TerminalExitConfirmationService(), isProjectInitialization: true)
    {
    }

    public MainWindow(string workingDirectory)
        : this(new ProjectContext("gabCode", workingDirectory), TerminalProfileResolver.CreateDefault(), new TerminalExitConfirmationService(), isProjectInitialization: true)
    {
    }

    internal MainWindow(ProjectContext project, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation)
        : this(project ?? throw new ArgumentNullException(nameof(project)), profileResolver, exitConfirmation, isProjectInitialization: true)
    {
    }

    internal MainWindow(string workingDirectory, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation)
        : this(new ProjectContext("gabCode", workingDirectory), profileResolver, exitConfirmation, isProjectInitialization: true)
    {
    }

    private MainWindow(ProjectContext? project, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation, bool isProjectInitialization)
    {
        this.project = project;
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        this.exitConfirmation = exitConfirmation ?? throw new ArgumentNullException(nameof(exitConfirmation));
        InitializeComponent();
        Closing += MainWindow_Closing;

        if (project is null)
        {
            SwapTerminalsButton.IsEnabled = false;
            return;
        }

        if (!Directory.Exists(project.ProjectFolder))
        {
            ShowWorkingDirectoryFailure(project.ProjectFolder);
            return;
        }

        ActivateProject(project);
    }

    internal TerminalSessionView? PiTerminal => piTerminal;
    internal TerminalSessionView? CommandsTerminal => commandsTerminal;
    internal string? ProjectFolder => project?.ProjectFolder;
    internal bool IsPiInMain => terminalLayout?.IsPiInMain is true;
    internal int ActiveTerminalCount => (piTerminal?.IsActive is true ? 1 : 0) + (commandsTerminal?.IsActive is true ? 1 : 0);

    internal void ShowPiInMain()
    {
        terminalLayout?.ShowPiInMain();
        piTerminal?.FocusTerminal();
    }

    internal void ShowCommandsInMain()
    {
        terminalLayout?.ShowCommandsInMain();
        commandsTerminal?.FocusTerminal();
    }

    private void ActivateProject(ProjectContext nextProject)
    {
        project = nextProject;
        Title = nextProject.WindowTitle;
        AutomationProperties.SetName(this, Title);
        WorktreePathText.Text = nextProject.ProjectFolder;
        WorktreePathText.ToolTip = nextProject.ProjectFolder;
        WorktreeFailureSurface.Visibility = Visibility.Collapsed;
        EmptyProjectSurface.Visibility = Visibility.Collapsed;
        TerminalWorkspace.Visibility = Visibility.Visible;
        SwapTerminalsButton.IsEnabled = true;
        CreateTerminalWorkspace();
    }

    private void CreateTerminalWorkspace()
    {
        var directory = project!.ProjectFolder;
        piTerminal = new TerminalSessionView(TerminalSessionKind.First, directory, profileResolver.Resolve);
        commandsTerminal = new TerminalSessionView(TerminalSessionKind.Second, directory, profileResolver.Resolve);
        terminalLayout = new RetainedTerminalLayout(MainTerminalRegion, BottomTerminalRegion, piTerminal, commandsTerminal);
        ShowPiInMain();
    }

    private void ShowWorkingDirectoryFailure(string directory)
    {
        EmptyProjectSurface.Visibility = Visibility.Collapsed;
        TerminalWorkspace.Visibility = Visibility.Collapsed;
        SwapTerminalsButton.IsEnabled = false;
        WorktreeFailureMessage.Text = $"The project folder does not exist or is unavailable: {directory}";
        WorktreeFailureSurface.Visibility = Visibility.Visible;
    }

    private async void OpenWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "gabCode workspace (*.gabcode-workspace)|*.gabcode-workspace", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenWorkspaceAsync(dialog.FileName);
    }

    internal async Task OpenWorkspaceAsync(string workspacePath)
    {
        try
        {
            var nextProject = await projectLoader.LoadAsync(workspacePath);
            if (!await ReplaceProjectAsync(nextProject)) return;
            await new LastWorkspacePreference().WriteAsync(workspacePath);
        }
        catch (Exception exception)
        {
            WorktreeFailureMessage.Text = exception.Message;
            WorktreeFailureSurface.Visibility = Visibility.Visible;
        }
    }

    private async void CreateWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog { Title = "Choose an existing Git folder" };
        if (folderDialog.ShowDialog(this) != true) return;
        var nameDialog = new WorkspaceNameDialog(Path.GetFileName(folderDialog.FolderName)) { Owner = this };
        if (nameDialog.ShowDialog() != true) return;
        var saveDialog = new SaveFileDialog
        {
            Filter = "gabCode workspace (*.gabcode-workspace)|*.gabcode-workspace",
            DefaultExt = ".gabcode-workspace",
            FileName = $"{nameDialog.WorkspaceName}.gabcode-workspace",
            OverwritePrompt = false,
        };
        if (saveDialog.ShowDialog(this) != true) return;
        try
        {
            _ = await projectCreator.CreateAsync(saveDialog.FileName, nameDialog.WorkspaceName, folderDialog.FolderName);
        }
        catch (Exception exception)
        {
            WorktreeFailureMessage.Text = exception.Message;
            WorktreeFailureSurface.Visibility = Visibility.Visible;
        }
    }

    private async Task<bool> ReplaceProjectAsync(ProjectContext nextProject)
    {
        if (piTerminal is not null || commandsTerminal is not null)
        {
            if (ActiveTerminalCount != 0 && exitConfirmation.Confirm(this, ActiveTerminalCount) == TerminalExitDecision.Cancel) return false;
            await Task.WhenAll(piTerminal?.CloseAsync() ?? Task.CompletedTask, commandsTerminal?.CloseAsync() ?? Task.CompletedTask);
            piTerminal = null;
            commandsTerminal = null;
            terminalLayout = null;
        }
        ActivateProject(nextProject);
        return true;
    }

    private void SwapTerminalsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPiInMain) ShowCommandsInMain(); else ShowPiInMain();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose || piTerminal is null || commandsTerminal is null || (!piTerminal.HasStarted && !commandsTerminal.HasStarted)) return;
        e.Cancel = true;
        if (closeInProgress) return;

        var focusedElement = Keyboard.FocusedElement;
        var activeCount = ActiveTerminalCount;
        if (activeCount != 0 && exitConfirmation.Confirm(this, activeCount) == TerminalExitDecision.Cancel)
        {
            if (focusedElement is not null) _ = Dispatcher.BeginInvoke(() => Keyboard.Focus(focusedElement), DispatcherPriority.Input);
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
            await Task.WhenAll(piTerminal?.CloseAsync() ?? Task.CompletedTask, commandsTerminal?.CloseAsync() ?? Task.CompletedTask);
            allowClose = true;
            Close();
        }
        catch (Exception)
        {
            closeInProgress = false;
            IsEnabled = true;
            _ = MessageBox.Show(this, "gabCode could not confirm that every terminal process stopped. The window will remain open so cleanup can be retried.", "Terminal cleanup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
