using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
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
    private readonly GitWorktreeDiscovery worktreeDiscovery = new();
    private readonly IGabCodeInstanceLauncher instanceLauncher = new GabCodeInstanceLauncher();
    private readonly TerminalProfileResolver profileResolver;
    private readonly ITerminalExitConfirmationService exitConfirmation;
    private RetainedTerminalLayout? terminalLayout;
    private TerminalSessionView? piTerminal;
    private TerminalSessionView? commandsTerminal;
    private bool closeInProgress;
    private bool allowClose;
    private CancellationTokenSource? discoveryCancellation;

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

    internal async Task<bool> OpenWorkspaceAsync(string workspacePath)
    {
        try
        {
            var nextProject = await projectLoader.LoadAsync(workspacePath);
            if (ProjectWindowRouting.ShouldLaunchNewWindow(project is not null))
            {
                instanceLauncher.Launch(workspacePath);
                return true;
            }
            ActivateProject(nextProject);
            await new LastWorkspacePreference().WriteAsync(workspacePath);
            return true;
        }
        catch (Exception exception)
        {
            if (project is null)
            {
                EmptyProjectMessage.Text = $"The last workspace could not be reopened: {exception.Message} Choose another workspace or create one for an existing Git folder.";
                EmptyProjectSurface.Visibility = Visibility.Visible;
                WorktreeFailureSurface.Visibility = Visibility.Collapsed;
            }
            else
            {
                WorktreeFailureMessage.Text = exception.Message;
                WorktreeFailureSurface.Visibility = Visibility.Visible;
            }
            return false;
        }
    }

    private async void CreateWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var projectFolderDialog = new OpenFolderDialog { Title = "Select Project Folder" };
        if (projectFolderDialog.ShowDialog(this) != true) return;
        var workspaceName = WorkspaceCreationDefaults.GetWorkspaceName(projectFolderDialog.FolderName);
        var projectRoot = projectFolderDialog.FolderName;
        IReadOnlyDictionary<string, string> branches;
        discoveryCancellation = new CancellationTokenSource();
        CancelDiscoveryButton.Visibility = Visibility.Visible;
        OpenWorkspaceButton.IsEnabled = false;
        CreateWorkspaceButton.IsEnabled = false;
        var discoveryProgress = new Progress<GitDiscoveryProgress>(status =>
            EmptyProjectMessage.Text = $"{status.Phase}: {status.FoldersScanned} folders scanned; {status.RepositoriesFound} repositories found.");
        try
        {
            branches = await worktreeDiscovery.DiscoverAsync(projectRoot, discoveryProgress, discoveryCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            EmptyProjectMessage.Text = "Git repository discovery was cancelled. Choose a project folder to try again.";
            return;
        }
        catch (Exception exception)
        {
            WorktreeFailureMessage.Text = exception.Message;
            WorktreeFailureSurface.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            discoveryCancellation.Dispose();
            discoveryCancellation = null;
            CancelDiscoveryButton.Visibility = Visibility.Collapsed;
            OpenWorkspaceButton.IsEnabled = true;
            CreateWorkspaceButton.IsEnabled = true;
        }
        var branchDialog = new WorkspaceBranchDialog(branches.Keys.Order().ToArray()) { Owner = this };
        if (branchDialog.ShowDialog() != true) return;
        var nameDialog = new WorkspaceNameDialog(workspaceName) { Owner = this };
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
            var created = await projectCreator.CreateAsync(saveDialog.FileName, nameDialog.WorkspaceName, projectRoot, branchDialog.Branch, ProjectWindowRouting.ShouldLaunchNewWindow(project is not null));
            if (project is null) ActivateProject(await projectLoader.LoadAsync(saveDialog.FileName));
        }
        catch (Exception exception)
        {
            WorktreeFailureMessage.Text = exception.Message;
            WorktreeFailureSurface.Visibility = Visibility.Visible;
        }
    }

    private void FileMenuItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (FileMenuItem.Template.FindName("PART_Popup", FileMenuItem) is Popup popup)
        {
            popup.PlacementTarget = FileMenuItem;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = (_, targetSize, _) =>
                [new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Vertical)];
            popup.Opened += (_, _) =>
            {
                if (popup.Child is Border border) border.Background = System.Windows.Media.Brushes.Black;
            };
        }
    }

    private void CancelDiscoveryButton_Click(object sender, RoutedEventArgs e) => discoveryCancellation?.Cancel();

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
