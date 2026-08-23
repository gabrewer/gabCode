using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GabCode.Windows.Projects;

internal sealed class WorktreeCreationDialog : Window
{
    private readonly TextBox nameTextBox;
    private readonly TextBox branchTextBox;
    private readonly TextBox locationTextBox;
    private readonly CheckBox fetchLatestCheckBox;
    private readonly CheckBox createCodeWorkspaceCheckBox;
    private readonly CheckBox openInCodeCheckBox;
    private readonly Button createButton;
    private readonly TextBlock validationText;
    private readonly Func<string, string, CancellationToken, Task<string?>>? validateAsync;
    private CancellationTokenSource? validationCancellation;
    private int validationGeneration;
    private bool branchEdited;
    private bool locationEdited;
    private bool updatingBranchPreview;
    private bool updatingLocationPreview;

    internal WorktreeCreationDialog(string baseBranch, string suggestedName, string suggestedPath, bool branchEditable = true, bool latestRemoteAvailable = true, Func<string, string, CancellationToken, Task<string?>>? validateAsync = null, string? suggestedBranch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedPath);
        BaseBranch = baseBranch;
        this.validateAsync = validateAsync;
        Title = "Create Worktree";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        nameTextBox = Field("Name", suggestedName, "Worktree name");
        branchTextBox = Field("Branch preview", suggestedBranch ?? WorktreeActionNaming.SuggestBranch(suggestedName), "Branch preview");
        locationTextBox = Field("Location preview", suggestedPath, "Location preview");
        branchTextBox.IsReadOnly = !branchEditable;
        branchTextBox.TextChanged += (_, _) => { if (!updatingBranchPreview) branchEdited = true; QueueValidation(); };
        locationTextBox.TextChanged += (_, _) => { if (!updatingLocationPreview) locationEdited = true; QueueValidation(); };
        nameTextBox.TextChanged += (_, _) =>
        {
            if (!branchEdited)
            {
                updatingBranchPreview = true;
                try { branchTextBox.Text = WorktreeActionNaming.SuggestBranch(WorktreeName); }
                finally { updatingBranchPreview = false; }
            }
            if (!locationEdited)
            {
                updatingLocationPreview = true;
                try { locationTextBox.Text = WorktreeActionNaming.SuggestPath(WorktreeName, Path.GetDirectoryName(suggestedPath)!); }
                finally { updatingLocationPreview = false; }
            }
            QueueValidation();
        };

        fetchLatestCheckBox = Option("Use the latest remote version of the workspace branch", "Use latest remote version");
        fetchLatestCheckBox.Visibility = latestRemoteAvailable ? Visibility.Visible : Visibility.Collapsed;
        createCodeWorkspaceCheckBox = Option("Create a VS Code workspace file", "Create a VS Code workspace file");
        openInCodeCheckBox = Option("Open in VS Code after creation", "Open in VS Code after creation");

        var browse = new Button { Content = "Browse", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(browse, "Browse for worktree location");
        browse.Click += Browse_Click;
        var locationPanel = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        locationPanel.Children.Add(browse);
        locationPanel.Children.Add(locationTextBox);

        createButton = new Button { Content = "Create", IsDefault = true, MinWidth = 80, IsEnabled = false };
        createButton.Click += (_, _) => { if (IsValid && !validationPending) DialogResult = true; };
        validationText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(validationText, AutomationLiveSetting.Polite);
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = $"Create a worktree from {baseBranch}", FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Enter a name. Branch and location previews can be edited before creation.", Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap },
                Labelled("Name", nameTextBox),
                Labelled("Branch preview", branchTextBox),
                Labelled("Location preview", locationPanel),
                fetchLatestCheckBox,
                createCodeWorkspaceCheckBox,
                openInCodeCheckBox,
                validationText,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { createButton, cancel } },
            },
        };
        Loaded += (_, _) => { nameTextBox.Focus(); nameTextBox.SelectAll(); QueueValidation(); };
        Closed += (_, _) => validationCancellation?.Cancel();
    }

    internal string BaseBranch { get; }
    internal string WorktreeName => nameTextBox.Text.Trim();
    internal string BranchName => branchTextBox.Text.Trim();
    internal string WorktreePath => locationTextBox.Text.Trim();
    internal bool FetchLatest => fetchLatestCheckBox.IsChecked is true;
    internal bool CreateVsCodeWorkspace => createCodeWorkspaceCheckBox.IsChecked is true;
    internal bool OpenInVsCode => openInCodeCheckBox.IsChecked is true;
    internal bool IsValid => !string.IsNullOrWhiteSpace(WorktreeName) && !string.IsNullOrWhiteSpace(BranchName) && !string.IsNullOrWhiteSpace(WorktreePath);
    private bool validationPending;

    private void QueueValidation()
    {
        if (!IsLoaded) return;
        var generation = ++validationGeneration;
        validationCancellation?.Cancel();
        validationCancellation?.Dispose();
        validationCancellation = new CancellationTokenSource();
        _ = ValidateAsync(generation, validationCancellation.Token);
    }

    private async Task ValidateAsync(int generation, CancellationToken cancellationToken)
    {
        validationPending = true;
        createButton.IsEnabled = false;
        validationText.Text = "Validating branch and location…";
        try
        {
            var error = !IsValid ? "Enter a name, branch, and worktree location." :
                validateAsync is null ? null : await validateAsync(BranchName, WorktreePath, cancellationToken);
            if (generation != validationGeneration || cancellationToken.IsCancellationRequested) return;
            validationText.Text = error ?? string.Empty;
            createButton.IsEnabled = error is null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (generation == validationGeneration)
        {
            validationText.Text = $"Could not validate creation: {exception.Message}";
            createButton.IsEnabled = false;
        }
        finally
        {
            if (generation == validationGeneration) validationPending = false;
        }
    }

    private static TextBox Field(string label, string value, string automationName)
    {
        var field = new TextBox { Text = value, MinWidth = 420, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetName(field, automationName);
        return field;
    }

    private static CheckBox Option(string text, string automationName)
    {
        var option = new CheckBox { Content = text, Margin = new Thickness(0, 3, 0, 3) };
        AutomationProperties.SetName(option, automationName);
        return option;
    }

    private static StackPanel Labelled(string label, UIElement control) => new()
    {
        Children = { new TextBlock { Text = label }, control },
    };

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose Worktree Location" };
        if (dialog.ShowDialog(this) == true)
        {
            locationEdited = true;
            locationTextBox.Text = dialog.FolderName;
        }
    }
}

internal static class WorktreeActionNaming
{
    internal static string SuggestBranch(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("feature/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("bugfix/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hotfix/", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return $"feature/{trimmed}";
    }

    internal static string SuggestPath(string name, string worktreeRoot)
    {
        var safe = name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '-');
        safe = safe.Replace('/', '-').Replace('\\', '-');
        return Path.Combine(worktreeRoot, $"wt-{safe}");
    }
}
