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
    private string? retainedWorktreePath;
    private string? retainedWorktreeRepositoryPath;
    private WorktreeNavigationEntry? blockedWorktreeEntry;
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
            var anchor = worktreeState?.Entries.FirstOrDefault(entry => entry.IsPrimary)?.Path ?? project.ProjectFolder;
            var reconciliation = await worktreeDiscovery.ReconcileMissingSecondaryWorktreesAsync(anchor, discoveryCancellation.Token);
            ReconcileWorktrees(reconciliation.Entries, generation, confirmedRemoval: true);
            if (!reconciliation.Entries.Any(entry => WorktreePath.Comparer.Equals(entry.Path, project.ProjectFolder)))
            {
                var safe = reconciliation.Entries.FirstOrDefault(entry => string.Equals(entry.Branch, project.MainBranch, StringComparison.Ordinal))
                    ?? reconciliation.Entries.First(entry => entry.IsPrimary);
                SelectWorktree(safe.Path, safe.Branch!);
            }
            RefreshStatusText.Text = reconciliation.PrunedPaths.Count == 0 ? string.Empty : $"Removed missing worktree: {string.Join(", ", reconciliation.PrunedPaths)}.";
        }
        catch (OperationCanceledException) { RefreshStatusText.Text = "Worktree refresh cancelled."; }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not refresh worktrees: {exception.Message}"; }
        finally { discoveryCancellation.Dispose(); discoveryCancellation = null; RefreshWorktreesButton.IsEnabled = true; CancelRefreshButton.Visibility = Visibility.Collapsed; }
    }

    private void ReconcileWorktrees(IReadOnlyList<GitWorktreeEntry> entries, long generation = 0, bool confirmedRemoval = false)
    {
        var registered = entries.Where(entry => entry.Branch is not null).Select(entry => new RegisteredWorktree(entry.Path, entry.Branch!, entry.IsPrimary));
        worktreeState ??= new WorktreeNavigationState(registered);
        foreach (var pair in terminalRegistry?.Pairs ?? []) MarkTerminalPairOwned(pair);
        refreshCoordinator ??= new WorktreeRefreshCoordinator(worktreeState);
        if (generation == 0) generation = refreshCoordinator.BeginRefresh();
        if (!refreshCoordinator.TryReconcile(generation, registered)) return;
        if (confirmedRemoval)
        {
            var confirmedGeneration = refreshCoordinator.BeginRefresh();
            if (!refreshCoordinator.TryReconcile(confirmedGeneration, registered)) return;
        }
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

    private void WorktreeList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(WorktreeList, e.OriginalSource as DependencyObject) is not ListBoxItem item || item.ContextMenu is null) return;
        e.Handled = true;
        if (!ConfigureWorktreeContextMenu(item)) return;
        item.ContextMenu.PlacementTarget = item;
        item.ContextMenu.IsOpen = true;
    }

    private void WorktreeList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is ListBoxItem item && !ConfigureWorktreeContextMenu(item)) e.Handled = true;
    }

    private static bool ConfigureWorktreeContextMenu(ListBoxItem item)
    {
        if (item.Tag is not WorktreeNavigationEntry entry || item.ContextMenu is null || entry.Availability != WorktreeAvailability.Available) return false;
        var delete = item.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(menu => string.Equals(menu.Header?.ToString(), "Delete worktree", StringComparison.Ordinal));
        if (delete is not null)
        {
            delete.Visibility = entry.IsPrimary ? Visibility.Collapsed : Visibility.Visible;
            delete.IsEnabled = !entry.IsPrimary;
        }
        return true;
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
        FocusWorktreeListItem(path);
    }

    private void FocusWorktreeListItem(string path)
    {
        WorktreeList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is WorktreeNavigationEntry entry && WorktreePath.Comparer.Equals(entry.Path, path))?.Focus();
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

    private enum RetainedCleanupState { Removed, Retained, Registered, Cancelled }

    private void ShowWorktreeRecovery(string path, string message, WorktreeNavigationEntry? blockedEntry = null, string? repositoryPath = null)
    {
        retainedWorktreePath = path;
        retainedWorktreeRepositoryPath = repositoryPath ?? project?.ProjectFolder;
        blockedWorktreeEntry = blockedEntry;
        RefreshStatusText.Text = message;
        RetryCleanupButton.Visibility = Visibility.Visible;
        OpenRetainedFolderButton.Visibility = Visibility.Visible;
        RetryCleanupButton.Focus();
    }

    private void ClearWorktreeRecovery()
    {
        retainedWorktreePath = null;
        retainedWorktreeRepositoryPath = null;
        blockedWorktreeEntry = null;
        RetryCleanupButton.Visibility = Visibility.Collapsed;
        OpenRetainedFolderButton.Visibility = Visibility.Collapsed;
    }

    private async void RetryRetainedWorktreeCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (project is null) return;
        if (blockedWorktreeEntry is { } blocked) { await DeleteWorktreeAsync(blocked); return; }
        if (string.IsNullOrWhiteSpace(retainedWorktreePath)) return;
        var path = retainedWorktreePath;
        await RunWorktreeActionAsync("Retrying cleanup…", async cancellationToken =>
        {
            var result = await RetryRetainedWorktreeCleanup(path, retainedWorktreeRepositoryPath!, requireNonEmptyConfirmation: true, cancellationToken);
            if (result == RetainedCleanupState.Removed)
            {
                ClearWorktreeRecovery();
                RefreshStatusText.Text = "Retained worktree folder removed.";
            }
            else if (result == RetainedCleanupState.Registered)
                ShowWorktreeRecovery(path, $"Cleanup stopped because a worktree is registered at {path}.");
            else if (result == RetainedCleanupState.Retained)
                ShowWorktreeRecovery(path, $"Could not remove retained folder {path}. Close applications using it, then retry.");
            else if (result == RetainedCleanupState.Cancelled)
                ShowWorktreeRecovery(path, $"Cleanup cancelled; local folder remains at {path}.");
        });
    }

    private async Task<RetainedCleanupState> RetryRetainedWorktreeCleanup(string path, string repositoryPath, bool requireNonEmptyConfirmation, CancellationToken cancellationToken)
    {
        if (requireNonEmptyConfirmation)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(path).Any() &&
                    MessageBox.Show(this, $"The retained folder is not empty:\n{path}\n\nDelete its remaining contents?", "Delete retained folder contents", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return RetainedCleanupState.Cancelled;
            }
            catch (DirectoryNotFoundException) { return RetainedCleanupState.Removed; }
            catch (UnauthorizedAccessException) { return RetainedCleanupState.Retained; }
            catch (IOException) { return RetainedCleanupState.Retained; }
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await worktreeDiscovery.DiscoverEntriesAsync(repositoryPath, cancellationToken: cancellationToken);
            if (entries.Any(entry => WorktreePath.Comparer.Equals(entry.Path, path))) return RetainedCleanupState.Registered;
            try
            {
                Directory.Delete(path, recursive: true);
                return RetainedCleanupState.Removed;
            }
            catch (DirectoryNotFoundException) { return RetainedCleanupState.Removed; }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }
        return RetainedCleanupState.Retained;
    }

    private void OpenRetainedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(retainedWorktreePath)) return;
        try { _ = Process.Start(new ProcessStartInfo("explorer.exe", retainedWorktreePath) { UseShellExecute = true }) ?? throw new InvalidOperationException("Explorer could not be started."); }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not open retained folder: {exception.Message}"; }
    }

    private async void DeleteWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (ContextEntry(sender) is { } entry) await DeleteWorktreeAsync(entry);
    }

    private async Task DeleteWorktreeAsync(WorktreeNavigationEntry entry)
    {
        if (entry.IsPrimary || project is null) return;
        var dialog = new WorktreeDeletionDialog(entry, terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var activeTerminals = terminalRegistry?.GetActiveTerminalCount(entry.Path) ?? 0;
        var hasTerminalPair = terminalRegistry?.Pairs.Any(pair => WorktreePath.Comparer.Equals(pair.Path, entry.Path)) is true;
        if (activeTerminals != 0 && exitConfirmation.Confirm(this, activeTerminals) == TerminalExitDecision.Cancel) return;
        ClearWorktreeRecovery();
        try
        {
            await RunWorktreeActionAsync("Removing worktree…", async cancellationToken =>
            {
                var dirty = await worktreeDiscovery.HasUncommittedOrUntrackedChangesAsync(entry.Path, cancellationToken);
                if (activeTerminals != 0) await terminalRegistry!.CloseAndRemoveAsync(entry.Path);
                var outcome = await worktreeDiscovery.RemoveWorktreeWithOutcomeAsync(project.ProjectFolder, entry.Path, force: false, cancellationToken);
                if (outcome.State == GitWorktreeRemovalState.Blocked && dirty &&
                    (outcome.Diagnostic?.Contains("modified or untracked", StringComparison.OrdinalIgnoreCase) is true) && ConfirmForceRemoval(entry, outcome.Diagnostic))
                    outcome = await worktreeDiscovery.RemoveWorktreeWithOutcomeAsync(project.ProjectFolder, entry.Path, force: true, cancellationToken);

                if (outcome.State is GitWorktreeRemovalState.Blocked or GitWorktreeRemovalState.ReconciliationUnavailable)
                {
                    if (outcome.Attempt == GitWorktreeRemovalAttempt.Cancelled) throw new OperationCanceledException(cancellationToken);
                    throw new WorktreeRemovalBlockedException(outcome.Diagnostic ?? "Git reconciliation could not confirm removal.");
                }

                var retained = outcome.State == GitWorktreeRemovalState.RemovedWithRetainedPath;
                var primaryPath = outcome.Entries.First(entry => entry.IsPrimary).Path;
                var cleanup = RetainedCleanupState.Removed;
                if (retained && outcome.Attempt != GitWorktreeRemovalAttempt.Cancelled)
                    cleanup = await RetryRetainedWorktreeCleanup(outcome.Path, primaryPath, requireNonEmptyConfirmation: false, cancellationToken);
                if (cleanup == RetainedCleanupState.Registered)
                {
                    if (hasTerminalPair && activeTerminals == 0) await terminalRegistry!.CloseAndRemoveAsync(entry.Path);
                    var current = await worktreeDiscovery.DiscoverEntriesAsync(primaryPath, cancellationToken: cancellationToken);
                    ReconcileWorktrees(current);
                    var safe = current.FirstOrDefault(item => string.Equals(item.Branch, project.MainBranch, StringComparison.Ordinal) && !WorktreePath.Comparer.Equals(item.Path, outcome.Path))
                        ?? current.First(item => item.IsPrimary);
                    SelectWorktree(safe.Path, safe.Branch!);
                    ShowWorktreeRecovery(outcome.Path, $"Cleanup stopped because a worktree is registered at {outcome.Path}.", repositoryPath: primaryPath);
                    return;
                }
                retained = retained && cleanup != RetainedCleanupState.Removed;
                if (retained) ShowWorktreeRecovery(outcome.Path, $"Worktree removed; local folder remains at {outcome.Path}.", repositoryPath: primaryPath);

                if (hasTerminalPair && activeTerminals == 0) await terminalRegistry!.CloseAndRemoveAsync(entry.Path);
                ReconcileWorktrees(outcome.Entries, confirmedRemoval: true);
                SelectRemainingWorktree(outcome.Entries);
                if (!dialog.DeleteLocalBranch || string.IsNullOrWhiteSpace(entry.Branch))
                {
                    if (!retained) RefreshStatusText.Text = "Worktree removed.";
                    return;
                }
                try { await worktreeDiscovery.DeleteLocalBranchAsync(project.ProjectFolder, entry.Branch, force: false, cancellationToken); RefreshStatusText.Text = retained ? $"Worktree removed; local folder remains at {outcome.Path}. Local branch removed." : "Worktree and local branch removed."; }
                catch (InvalidOperationException exception) when (ConfirmForceBranchDeletion(entry, exception.Message))
                {
                    await worktreeDiscovery.DeleteLocalBranchAsync(project.ProjectFolder, entry.Branch, force: true, cancellationToken);
                    RefreshStatusText.Text = retained ? $"Worktree removed; local folder remains at {outcome.Path}. Local branch removed." : "Worktree and local branch removed.";
                }
                catch (Exception exception) { RefreshStatusText.Text = retained ? $"Worktree removed; local folder remains at {outcome.Path}. Local branch was retained: {exception.Message}" : $"Worktree removed, but local branch was retained: {exception.Message}"; }
            });
        }
        catch (WorktreeRemovalBlockedException exception)
        {
            ShowWorktreeRecovery(entry.Path, $"Could not remove worktree at {entry.Path}: {exception.Message}", entry);
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not remove worktree: {exception.Message}"; }
    }

    private sealed class WorktreeRemovalBlockedException(string message) : Exception(message);

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
        try
        {
            await terminalRegistry.CloseAndRemoveAsync(entry.Path);
            worktreeState?.RemoveOrphan(entry.Path);
            CreateTerminalWorkspace();
            PopulateWorktrees();
            FocusWorktreeListItem(project?.ProjectFolder ?? string.Empty);
        }
        catch (Exception exception) { RefreshStatusText.Text = $"Could not close orphaned terminals: {exception.Message}"; }
    }

    private void WorktreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (applyingWorktreeSelection || WorktreeList.SelectedItem is not ListBoxItem { Tag: WorktreeNavigationEntry entry }) return;
        var availability = GitWorktreeDiscovery.GetPathAvailability(entry.Path);
        if (entry.Availability != WorktreeAvailability.Available || availability != WorktreePathAvailability.Present)
        {
            if (entry.MissingRefreshes >= 2 && terminalRegistry?.Pairs.FirstOrDefault(pair => WorktreePath.Comparer.Equals(pair.Path, entry.Path)) is { } orphan)
            {
                orphan.Attach(MainTerminalRegion, BottomTerminalRegion);
                piTerminal = orphan.First;
                commandsTerminal = orphan.Second;
                terminalLayout = orphan.Layout;
                RefreshStatusText.Text = "Orphaned terminals are attached; worktree-scoped Git actions are unavailable.";
                return;
            }
            if (availability == WorktreePathAvailability.Missing)
            {
                applyingWorktreeSelection = true;
                WorktreeList.SelectedItem = WorktreeList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is WorktreeNavigationEntry current && WorktreePath.Comparer.Equals(current.Path, project?.ProjectFolder));
                applyingWorktreeSelection = false;
                FocusWorktreeListItem(project?.ProjectFolder ?? string.Empty);
                RefreshStatusText.Text = $"Worktree path is missing; reconciling {entry.Path}.";
                _ = RefreshWorktreesAsync();
                return;
            }
            RefreshStatusText.Text = $"Cannot activate worktree because its path is unavailable: {entry.Path}.";
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
