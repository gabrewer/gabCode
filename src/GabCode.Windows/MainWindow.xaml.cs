using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private readonly WorkspaceSelectionPreference selectionPreference = new();
    private string? activeWorkspacePath;
    private readonly GitWorktreeDiscovery worktreeDiscovery = new();
    private readonly IGabCodeInstanceLauncher instanceLauncher;
    private readonly TerminalProfileResolver profileResolver;
    private readonly ITerminalExitConfirmationService exitConfirmation;
    private RetainedTerminalLayout? terminalLayout;
    private WorktreeTerminalRegistry? terminalRegistry;
    private TerminalSessionView? piTerminal;
    private TerminalSessionView? commandsTerminal;
    private bool closeInProgress;
    private bool allowClose;
    private CancellationTokenSource? discoveryCancellation;
    private CancellationTokenSource? worktreeActionCancellation;
    private readonly SidebarSidePreference sidebarPreference = new();
    private readonly VisualStudioCodePreference visualStudioCodePreference = new();
    private WorktreeNavigationState? worktreeState;
    private WorktreeRefreshCoordinator? refreshCoordinator;
    private bool applyingWorktreeSelection;
    private readonly HashSet<WorktreeTerminalPair> observedTerminalPairs = [];
    private static readonly RoutedCommand RefreshWorktreesCommand = new("Refresh Worktrees", typeof(MainWindow));

    public MainWindow()
        : this(null, TerminalProfileResolver.CreateDefault(), new TerminalExitConfirmationService(), isProjectInitialization: true)
    {
    }

    public MainWindow(string workingDirectory)
        : this(new ProjectContext("gabCode", workingDirectory), TerminalProfileResolver.CreateDefault(), new TerminalExitConfirmationService(), isProjectInitialization: true)
    {
    }

    internal MainWindow(ProjectContext project, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation, IGabCodeInstanceLauncher? instanceLauncher = null)
        : this(project ?? throw new ArgumentNullException(nameof(project)), profileResolver, exitConfirmation, isProjectInitialization: true, instanceLauncher)
    {
    }

    internal MainWindow(string workingDirectory, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation)
        : this(new ProjectContext("gabCode", workingDirectory), profileResolver, exitConfirmation, isProjectInitialization: true)
    {
    }

    private MainWindow(ProjectContext? project, TerminalProfileResolver profileResolver, ITerminalExitConfirmationService exitConfirmation, bool isProjectInitialization, IGabCodeInstanceLauncher? instanceLauncher = null)
    {
        this.project = project;
        this.profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        this.exitConfirmation = exitConfirmation ?? throw new ArgumentNullException(nameof(exitConfirmation));
        this.instanceLauncher = instanceLauncher ?? new GabCodeInstanceLauncher();
        InitializeComponent();
        InputBindings.Add(new KeyBinding(RefreshWorktreesCommand, Key.F5, ModifierKeys.None) { CommandTarget = this });
        CommandBindings.Add(new CommandBinding(RefreshWorktreesCommand, RefreshWorktreesCommand_Executed, RefreshWorktreesCommand_CanExecute));
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
    internal int ActiveTerminalCount => terminalRegistry?.ActiveTerminalCount ?? 0;

    internal void ShowPiInMain()
    {
        terminalLayout?.ShowPiInMain();
        piTerminal?.RefreshLayout();
        commandsTerminal?.RefreshLayout();
        piTerminal?.FocusTerminal();
    }

    internal void ShowCommandsInMain()
    {
        terminalLayout?.ShowCommandsInMain();
        piTerminal?.RefreshLayout();
        commandsTerminal?.RefreshLayout();
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
        ApplySidebarSide(sidebarPreference.Read());
        _ = RefreshWorktreesAsync();
    }

    private void CreateTerminalWorkspace()
    {
        var pair = (terminalRegistry ??= new WorktreeTerminalRegistry(profileResolver.Resolve)).GetOrCreate(project!.ProjectFolder);
        ObserveTerminalPair(pair);
        MarkTerminalPairOwned(pair);
        pair.Attach(MainTerminalRegion, BottomTerminalRegion);
        piTerminal = pair.First;
        commandsTerminal = pair.Second;
        terminalLayout = pair.Layout;
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
                instanceLauncher.Launch(Path.GetFullPath(workspacePath));
                return true;
            }
            ActivateProject(nextProject);
            activeWorkspacePath = Path.GetFullPath(workspacePath);
            await new LastWorkspacePreference().WriteAsync(activeWorkspacePath);
            if (nextProject.UsedPrimaryFallback)
                RefreshStatusText.Text = $"The previously selected worktree is no longer available. Opened {Path.GetFileName(nextProject.ProjectFolder)} instead.";
            return true;
        }
        catch (Exception exception)
        {
            var heading = exception is FormatException ? "Invalid workspace file" : "Workspace could not be opened";
            var reason = DescribeWorkspaceOpenFailure(exception);
            var details = $"Reason: {reason}\nWorkspace file: {Path.GetFullPath(workspacePath)}";
            WorktreeFailureHeading.Text = heading;
            if (project is null)
            {
                EmptyProjectHeading.Text = heading;
                EmptyProjectMessage.Text = $"{details}\nChoose another workspace or create one for an existing Git folder.";
                EmptyProjectSurface.Visibility = Visibility.Visible;
                WorktreeFailureSurface.Visibility = Visibility.Collapsed;
            }
            else
            {
                WorktreeFailureMessage.Text = details;
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
            await (terminalRegistry?.CloseAllAsync() ?? Task.CompletedTask);
            piTerminal = null;
            commandsTerminal = null;
            terminalLayout = null;
        }
        ActivateProject(nextProject);
        return true;
    }

    private void RefreshWorktreesCommand_Executed(object sender, ExecutedRoutedEventArgs e) => _ = RefreshWorktreesAsync();

    private void RefreshWorktreesCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = project is not null && discoveryCancellation is null;
        e.Handled = true;
    }

    private void SwapTerminalsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPiInMain) ShowCommandsInMain(); else ShowPiInMain();
    }

    private async void RefreshWorktrees_Click(object sender, RoutedEventArgs e) => await RefreshWorktreesAsync();

    private async Task RefreshWorktreesAsync()
    {
        if (project is null || discoveryCancellation is not null || worktreeActionCancellation is not null) return;
        discoveryCancellation = new CancellationTokenSource();
        var generation = refreshCoordinator?.BeginRefresh() ?? 0;
        RefreshWorktreesButton.IsEnabled = false;
        CancelRefreshButton.Visibility = Visibility.Visible;
        RefreshStatusText.Text = "Refreshing worktrees…";
        try
        {
            var entries = await worktreeDiscovery.DiscoverEntriesAsync(project.ProjectFolder, cancellationToken: discoveryCancellation.Token);
            ReconcileWorktrees(entries, generation);
            RefreshStatusText.Text = string.Empty;
        }
        catch (OperationCanceledException) { RefreshStatusText.Text = "Worktree refresh cancelled."; }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not refresh worktrees: {exception.Message}"; }
        finally { discoveryCancellation.Dispose(); discoveryCancellation = null; RefreshWorktreesButton.IsEnabled = true; CancelRefreshButton.Visibility = Visibility.Collapsed; }
    }

    private void ReconcileWorktrees(IReadOnlyList<GitWorktreeEntry> entries, long generation = 0)
    {
        var registered = entries.Where(entry => entry.Branch is not null).Select(entry => new RegisteredWorktree(entry.Path, entry.Branch!, entry.IsPrimary));
        worktreeState ??= new WorktreeNavigationState(registered);
        foreach (var pair in terminalRegistry?.Pairs ?? []) MarkTerminalPairOwned(pair);
        refreshCoordinator ??= new WorktreeRefreshCoordinator(worktreeState);
        if (generation == 0) generation = refreshCoordinator.BeginRefresh();
        if (!refreshCoordinator.TryReconcile(generation, registered)) return;
        PopulateWorktrees();
    }

    private void CancelRefresh_Click(object sender, RoutedEventArgs e)
    {
        discoveryCancellation?.Cancel();
        worktreeActionCancellation?.Cancel();
    }

    private void PopulateWorktrees()
    {
        if (worktreeState is null) return;
        applyingWorktreeSelection = true;
        WorktreeList.Items.Clear();
        foreach (var entry in worktreeState.Entries)
        {
            var hasRunningTerminals = terminalRegistry?.Pairs.Any(pair => WorktreePath.Comparer.Equals(pair.Path, entry.Path) && pair.ActiveTerminalCount > 0) is true;
            var isSelected = WorktreePath.Comparer.Equals(entry.Path, project?.ProjectFolder);
            var item = new ListBoxItem { Tag = entry, Content = WorktreeSidebarItem.Create(entry, isSelected, hasRunningTerminals) };
            AutomationProperties.SetName(item, AutomationProperties.GetName((WorktreeSidebarItem)item.Content));
            WorktreeList.Items.Add(item);
            if (WorktreePath.Comparer.Equals(entry.Path, project?.ProjectFolder)) WorktreeList.SelectedItem = item;
        }
        if (worktreeState.Orphaned.Count != 0)
        {
            WorktreeList.Items.Add(new TextBlock { Text = "Orphaned terminals", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var entry in worktreeState.Orphaned)
            {
                var close = new Button { Content = "Close Terminals", Tag = entry, Margin = new Thickness(4, 0, 0, 0) };
                close.Click += CloseOrphanTerminals_Click;
                var sidebarItem = WorktreeSidebarItem.Create(entry, WorktreePath.Comparer.Equals(entry.Path, project?.ProjectFolder), (terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0) > 0);
                var panel = new StackPanel();
                panel.Children.Add(sidebarItem);
                panel.Children.Add(close);
                var item = new ListBoxItem { Tag = entry, Content = panel };
                AutomationProperties.SetName(item, $"Orphaned terminals: {AutomationProperties.GetName(sidebarItem)}"); WorktreeList.Items.Add(item);
            }
        }
        applyingWorktreeSelection = false;
        UpdateSidebarIndicators();
    }

    private void MarkTerminalPairOwned(WorktreeTerminalPair pair) => worktreeState?.MarkTerminalPairCreated(pair.Path);

    private void ObserveTerminalPair(WorktreeTerminalPair pair)
    {
        if (!observedTerminalPairs.Add(pair)) return;
        pair.SessionChanged += TerminalPair_SessionChanged;
    }

    private void TerminalPair_SessionChanged(object? sender, EventArgs e)
    {
        if (closeInProgress || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) UpdateSidebarIndicators();
        else _ = Dispatcher.BeginInvoke(UpdateSidebarIndicators, DispatcherPriority.DataBind);
    }

    private void UpdateSidebarIndicators()
    {
        foreach (var item in WorktreeList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is not WorktreeNavigationEntry entry) continue;
            var sidebarItem = item.Content as WorktreeSidebarItem ??
                (item.Content as StackPanel)?.Children.OfType<WorktreeSidebarItem>().FirstOrDefault();
            if (sidebarItem is null) continue;
            var selected = WorktreePath.Comparer.Equals(entry.Path, project?.ProjectFolder);
            var running = (terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0) > 0;
            sidebarItem.UpdateState(selected, running);
            var name = AutomationProperties.GetName(sidebarItem);
            AutomationProperties.SetName(item, entry.MissingRefreshes >= 2 ? $"Orphaned terminals: {name}" : name);
        }
    }

    private void WorktreeList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not ListBoxItem { Tag: WorktreeNavigationEntry entry } item || item.ContextMenu is null) return;
        var delete = item.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(menu => string.Equals(menu.Header?.ToString(), "Delete worktree", StringComparison.Ordinal));
        if (delete is not null)
        {
            delete.Visibility = entry.IsPrimary ? Visibility.Collapsed : Visibility.Visible;
            delete.IsEnabled = !entry.IsPrimary;
        }
    }

    private static WorktreeNavigationEntry? ContextEntry(object sender)
    {
        if (sender is not MenuItem menu || menu.Parent is not ContextMenu context || context.PlacementTarget is not ListBoxItem { Tag: WorktreeNavigationEntry entry }) return null;
        return entry;
    }

    private async void CreateWorktreeFromMain_Click(object sender, RoutedEventArgs e)
    {
        var baseBranch = project?.MainBranch ?? ContextEntry(sender)?.Branch;
        if (!string.IsNullOrWhiteSpace(baseBranch)) await CreateNewWorktreeAsync(baseBranch);
    }

    private async void CreateWorktreeFromSelectedBranch_Click(object sender, RoutedEventArgs e)
    {
        var baseBranch = ContextEntry(sender)?.Branch;
        if (!string.IsNullOrWhiteSpace(baseBranch)) await CreateNewWorktreeAsync(baseBranch);
    }

    private async void CreateWorktreeFromExistingBranch_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        try
        {
            IReadOnlyList<GitBranchReference> branches = await worktreeDiscovery.ListBranchesAsync(project.ProjectFolder);
            GitBranchReference selected;
            while (true)
            {
                var picker = new ExistingWorktreeBranchDialog(branches) { Owner = this };
                if (picker.ShowDialog() == true) { selected = picker.SelectedBranch; break; }
                if (!picker.RefreshRequested) return;
                branches = await worktreeDiscovery.RefreshRemoteBranchesAsync(project.ProjectFolder);
            }
            var sourceRef = selected.Name;
            var localBranch = selected.IsRemote && selected.Name.Contains('/', StringComparison.Ordinal)
                ? selected.Name[(selected.Name.IndexOf('/') + 1)..]
                : selected.Name;
            if (selected.IsRemote && branches.Any(branch => !branch.IsRemote && string.Equals(branch.Name, localBranch, StringComparison.Ordinal)))
            {
                RefreshStatusText.Text = $"Local branch '{localBranch}' already exists. Select that local branch or choose a different remote branch.";
                return;
            }
            var defaultName = localBranch.Replace('/', '-');
            var root = WorktreeActionRoot(project.ProjectFolder);
            var dialog = new WorktreeCreationDialog(selected.Name, defaultName, WorktreeActionNaming.SuggestPath(defaultName, root), branchEditable: false, latestRemoteAvailable: false, validateAsync: ValidateExistingWorktreeAsync, suggestedBranch: localBranch) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await RunWorktreeActionAsync("Creating worktree…", async cancellationToken =>
            {
                var entries = await worktreeDiscovery.CreateExistingWorktreeAsync(project.ProjectFolder, localBranch, sourceRef, dialog.WorktreePath, cancellationToken);
                await CompleteWorktreeCreationAsync(entries, localBranch, dialog);
            });
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not create worktree: {exception.Message}"; }
    }

    private async Task CreateNewWorktreeAsync(string baseBranch)
    {
        if (project is null) return;
        try
        {
            var root = WorktreeActionRoot(project.ProjectFolder);
            var latestRemoteAvailable = await worktreeDiscovery.HasUsableRemoteAsync(project.ProjectFolder, baseBranch);
            var dialog = new WorktreeCreationDialog(baseBranch, "new-worktree", WorktreeActionNaming.SuggestPath("new-worktree", root), latestRemoteAvailable: latestRemoteAvailable, validateAsync: ValidateNewWorktreeAsync) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await RunWorktreeActionAsync("Creating worktree…", async cancellationToken =>
            {
                var entries = await worktreeDiscovery.CreateWorktreeAsync(project.ProjectFolder, baseBranch, dialog.BranchName, dialog.WorktreePath, dialog.FetchLatest, cancellationToken);
                await CompleteWorktreeCreationAsync(entries, dialog.BranchName, dialog);
            });
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not create worktree: {exception.Message}"; }
    }

    private async Task RunWorktreeActionAsync(string progress, Func<CancellationToken, Task> action)
    {
        if (worktreeActionCancellation is not null) return;
        worktreeActionCancellation = new CancellationTokenSource();
        WorktreeList.IsEnabled = false;
        RefreshWorktreesButton.IsEnabled = false;
        CancelRefreshButton.Visibility = Visibility.Visible;
        RefreshStatusText.Text = progress;
        try { await action(worktreeActionCancellation.Token); }
        catch (OperationCanceledException)
        {
            try
            {
                if (project is not null) ReconcileWorktrees(await worktreeDiscovery.DiscoverEntriesAsync(project.ProjectFolder));
                RefreshStatusText.Text = "Worktree action cancelled.";
            }
            catch (Exception exception) { RefreshStatusText.Text = $"Worktree action cancelled; reconciliation failed: {exception.Message}"; }
        }
        finally
        {
            worktreeActionCancellation.Dispose();
            worktreeActionCancellation = null;
            WorktreeList.IsEnabled = true;
            RefreshWorktreesButton.IsEnabled = true;
            CancelRefreshButton.Visibility = Visibility.Collapsed;
        }
    }

    private Task<string?> ValidateNewWorktreeAsync(string branch, string path, CancellationToken cancellationToken) =>
        project is null ? Task.FromResult<string?>("No workspace is active.") : worktreeDiscovery.ValidateNewWorktreeAsync(project.ProjectFolder, branch, path, cancellationToken: cancellationToken);

    private Task<string?> ValidateExistingWorktreeAsync(string branch, string path, CancellationToken cancellationToken) =>
        project is null ? Task.FromResult<string?>("No workspace is active.") : worktreeDiscovery.ValidateNewWorktreeAsync(project.ProjectFolder, branch, path, allowExistingLocalBranch: true, cancellationToken: cancellationToken);

    private async Task CompleteWorktreeCreationAsync(IReadOnlyList<GitWorktreeEntry> entries, string branch, WorktreeCreationDialog dialog)
    {
        var created = entries.SingleOrDefault(entry => string.Equals(entry.Branch, branch, StringComparison.Ordinal));
        if (created is null) throw new InvalidOperationException($"Git did not report the created worktree for branch '{branch}'.");
        var workspaceFileCreated = false;
        string? workspaceFileError = null;
        if (dialog.CreateVsCodeWorkspace)
        {
            try { await CreateVsCodeWorkspaceAsync(created.Path); workspaceFileCreated = true; }
            catch (Exception exception) { workspaceFileError = exception.Message; }
        }
        ReconcileWorktrees(entries);
        SelectWorktree(created.Path, branch);
        if (workspaceFileError is not null)
            RefreshStatusText.Text = $"Worktree created, but its VS Code workspace file could not be created: {workspaceFileError}";
        else RefreshStatusText.Text = string.Empty;
        if (!dialog.OpenInVsCode) return;
        try
        {
            var target = workspaceFileCreated ? Path.Combine(created.Path, $"{Path.GetFileName(created.Path)}.code-workspace") : created.Path;
            OpenInVsCode(target);
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Worktree created, but VS Code could not be opened: {exception.Message}"; }
    }

    private async Task CreateVsCodeWorkspaceAsync(string worktreePath)
    {
        var file = Path.Combine(worktreePath, $"{Path.GetFileName(worktreePath)}.code-workspace");
        var json = JsonSerializer.Serialize(new { folders = new[] { new { path = "." } }, settings = new { } }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(file, json);
    }

    private void SelectWorktree(string path, string branch)
    {
        if (project is null) return;
        project = new ProjectContext(project.WorkspaceName, path, project.MainBranch);
        PersistSelectedWorktree(path);
        Title = project.WindowTitle;
        WorktreePathText.Text = path;
        WorktreePathText.ToolTip = path;
        CreateTerminalWorkspace();
        PopulateWorktrees();
    }

    private static string WorktreeActionRoot(string worktreePath)
    {
        var parent = Directory.GetParent(worktreePath)?.FullName ?? worktreePath;
        return Path.Combine(parent, "wt");
    }

    private void OpenVisualStudioCodeSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VisualStudioCodeSettingsDialog(visualStudioCodePreference.Resolve()) { Owner = this };
        if (dialog.ShowDialog() == true) visualStudioCodePreference.Write(dialog.ExecutablePath);
    }

    private void OpenWorktreeInCode_Click(object sender, RoutedEventArgs e)
    {
        var entry = ContextEntry(sender);
        if (entry is null) return;
        try { OpenInVsCode(entry.Path); }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not open worktree in VS Code: {exception.Message}"; }
    }

    private void OpenInVsCode(string target) => VisualStudioCodeLauncher.Open(visualStudioCodePreference.Resolve(), target);

    private void RevealWorktree_Click(object sender, RoutedEventArgs e)
    {
        var entry = ContextEntry(sender);
        if (entry is null) return;
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add(entry.Path);
            _ = Process.Start(info) ?? throw new InvalidOperationException("Explorer could not be started.");
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not reveal worktree in Explorer: {exception.Message}"; }
    }

    private async void DeleteWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is not { } entry || entry.IsPrimary || project is null) return;
        var dialog = new WorktreeDeletionDialog(entry, terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var activeTerminals = terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0;
        var hasTerminalPair = terminalRegistry?.Pairs.Any(pair => WorktreePath.Comparer.Equals(pair.Path, entry.Path)) is true;
        if (activeTerminals != 0 && exitConfirmation.Confirm(this, activeTerminals) == TerminalExitDecision.Cancel) return;
        try
        {
            await RunWorktreeActionAsync("Removing worktree…", async cancellationToken =>
            {
                var dirty = await worktreeDiscovery.HasUncommittedOrUntrackedChangesAsync(entry.Path, cancellationToken);
                if (activeTerminals != 0) await terminalRegistry!.CloseAndRemoveAsync(entry.Path);
                IReadOnlyList<GitWorktreeEntry> entries;
                try
                {
                    entries = await worktreeDiscovery.RemoveWorktreeAsync(project.ProjectFolder, entry.Path, force: false, deleteLocalBranch: false, forceBranchDelete: false, cancellationToken: cancellationToken);
                }
                catch (InvalidOperationException exception) when (dirty && exception.Message.Contains("modified or untracked", StringComparison.OrdinalIgnoreCase) && ConfirmForceRemoval(entry, exception.Message))
                {
                    entries = await worktreeDiscovery.RemoveWorktreeAsync(project.ProjectFolder, entry.Path, force: true, deleteLocalBranch: false, forceBranchDelete: false, cancellationToken: cancellationToken);
                }
                if (hasTerminalPair && activeTerminals == 0) await terminalRegistry!.CloseAndRemoveAsync(entry.Path);
                ReconcileWorktrees(entries);
                SelectRemainingWorktree(entries);
                if (!dialog.DeleteLocalBranch || string.IsNullOrWhiteSpace(entry.Branch)) return;
                try { await worktreeDiscovery.DeleteLocalBranchAsync(project.ProjectFolder, entry.Branch, force: false, cancellationToken); }
                catch (InvalidOperationException exception) when (ConfirmForceBranchDeletion(entry, exception.Message))
                {
                    await worktreeDiscovery.DeleteLocalBranchAsync(project.ProjectFolder, entry.Branch, force: true, cancellationToken);
                }
                catch (Exception exception) { RefreshStatusText.Text = $"Worktree removed, but local branch was retained: {exception.Message}"; }
            });
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not remove worktree: {exception.Message}"; }
    }

    private bool ConfirmForceRemoval(WorktreeNavigationEntry entry, string error) =>
        MessageBox.Show(this, $"Git could not safely remove '{entry.Branch}'.\n\n{error}\n\nForce delete this worktree may permanently lose uncommitted and untracked files.", "Force delete this worktree", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private bool ConfirmForceBranchDeletion(WorktreeNavigationEntry entry, string error) =>
        MessageBox.Show(this, $"The worktree was removed, but local branch '{entry.Branch}' is unmerged.\n\n{error}\n\nForce deletion permanently removes that local branch.", "Force delete local branch", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void SelectRemainingWorktree(IReadOnlyList<GitWorktreeEntry> entries)
    {
        if (project is null) return;
        var selected = entries.FirstOrDefault(item => string.Equals(item.Branch, project.MainBranch, StringComparison.Ordinal)) ?? entries.FirstOrDefault(item => item.Branch is not null);
        if (selected?.Branch is not null) SelectWorktree(selected.Path, selected.Branch);
    }

    private async void CloseOrphanTerminals_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorktreeNavigationEntry entry } || terminalRegistry is null) return;
        var activeCount = terminalRegistry.GetActiveTerminalCount(entry.Path);
        if (activeCount != 0 && exitConfirmation.Confirm(this, activeCount) == TerminalExitDecision.Cancel) return;
        try { await terminalRegistry.CloseAndRemoveAsync(entry.Path); worktreeState?.RemoveOrphan(entry.Path); PopulateWorktrees(); }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not close orphaned terminals: {exception.Message}"; }
    }

    private void WorktreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingWorktreeSelection || WorktreeList.SelectedItem is not ListBoxItem { Tag: WorktreeNavigationEntry entry }) return;
        if (entry.Availability != WorktreeAvailability.Available)
        {
            var pair = terminalRegistry?.GetOrCreate(entry.Path);
            if (pair is not null) { ObserveTerminalPair(pair); MarkTerminalPairOwned(pair); pair.Attach(MainTerminalRegion, BottomTerminalRegion); piTerminal = pair.First; commandsTerminal = pair.Second; terminalLayout = pair.Layout; }
            RefreshStatusText.Text = "This worktree is unavailable; worktree-scoped Git actions are unavailable.";
            return;
        }
        project = new ProjectContext(project!.WorkspaceName, entry.Path, project.MainBranch);
        PersistSelectedWorktree(entry.Path);
        Title = project.WindowTitle; WorktreePathText.Text = entry.Path; WorktreePathText.ToolTip = entry.Path;
        CreateTerminalWorkspace();
        UpdateSidebarIndicators();
    }

    private static string DescribeWorkspaceOpenFailure(Exception exception) => exception switch
    {
        FileNotFoundException => "The workspace file could not be found.",
        DirectoryNotFoundException => "A required workspace or project folder could not be found.",
        UnauthorizedAccessException => "gabCode does not have permission to read the workspace or project folder.",
        FormatException => exception.Message,
        _ when string.IsNullOrWhiteSpace(exception.Message) => "An unexpected error occurred while opening the workspace.",
        _ => exception.Message
    };

    private async void PersistSelectedWorktree(string path)
    {
        if (activeWorkspacePath is null) return;
        try { await selectionPreference.WriteAsync(activeWorkspacePath, path); }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not remember the selected worktree: {exception.Message}"; }
    }

    private void MoveSidebarRight_Click(object sender, RoutedEventArgs e) => ApplySidebarSide(SidebarSide.Right);
    private void MoveSidebarLeft_Click(object sender, RoutedEventArgs e) => ApplySidebarSide(SidebarSide.Left);
    private void ApplySidebarSide(SidebarSide side)
    {
        if (side == SidebarSide.Right) { Grid.SetColumn(WorktreeSidebar, 1); Grid.SetColumn(TerminalGrid, 0); SidebarColumn.Width = new GridLength(1, GridUnitType.Star); TerminalColumn.Width = new GridLength(250); }
        else { Grid.SetColumn(WorktreeSidebar, 0); Grid.SetColumn(TerminalGrid, 1); SidebarColumn.Width = new GridLength(250); TerminalColumn.Width = new GridLength(1, GridUnitType.Star); }
        sidebarPreference.Write(side);
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
            await (terminalRegistry?.CloseAllAsync() ?? Task.CompletedTask);
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
