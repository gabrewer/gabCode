using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

[Collection(GabCode.Windows.Tests.Terminal.WpfTestCollection.Name)]
public sealed class WorktreeCreationDialogTests
{
    [Fact]
    public async Task Dialog_shows_editable_branch_and_location_defaults_without_optional_actions()
    {
        await RunOnStaAsync(() =>
        {
            var dialog = new WorktreeCreationDialog("trunk", "billing-fix", @"C:\repo\wt\wt-billing-fix");
            Assert.Equal("trunk", dialog.BaseBranch);
            Assert.Equal("billing-fix", dialog.WorktreeName);
            Assert.Equal("feature/billing-fix", dialog.BranchName);
            Assert.Equal(@"C:\repo\wt\wt-billing-fix", dialog.WorktreePath);
            Assert.False(dialog.FetchLatest);
            Assert.False(dialog.CreateVsCodeWorkspace);
            Assert.False(dialog.OpenInVsCode);
        });
    }

    [Fact]
    public async Task Existing_branch_dialog_preview_uses_the_selected_local_branch()
    {
        await RunOnStaAsync(() =>
        {
            var dialog = new WorktreeCreationDialog("origin/feature/remote", "feature-remote", @"C:\repo\wt\wt-feature-remote", branchEditable: false, latestRemoteAvailable: false, suggestedBranch: "feature/remote");
            Assert.Equal("feature/remote", dialog.BranchName);
        });
    }

    [Fact]
    public async Task Name_changes_keep_both_unedited_previews_live()
    {
        await RunOnStaAsync(() =>
        {
            var dialog = new WorktreeCreationDialog("trunk", "first", @"C:\repo\wt\wt-first");
            var name = FindTextBox(dialog, "Worktree name");

            name.Text = "second";
            Assert.Equal("feature/second", dialog.BranchName);
            Assert.Equal(@"C:\repo\wt\wt-second", dialog.WorktreePath);

            name.Text = "third";
            Assert.Equal("feature/third", dialog.BranchName);
            Assert.Equal(@"C:\repo\wt\wt-third", dialog.WorktreePath);
        });
    }

    [Theory]
    [InlineData("feature/already", "feature/already")]
    [InlineData("bugfix/already", "bugfix/already")]
    [InlineData("hotfix/already", "hotfix/already")]
    [InlineData("plain-name", "feature/plain-name")]
    public void Naming_preserves_supported_prefixes_or_suggests_feature(string input, string expected) =>
        Assert.Equal(expected, WorktreeActionNaming.SuggestBranch(input));

    private static TextBox FindTextBox(DependencyObject root, string automationName)
    {
        if (root is TextBox textBox && AutomationProperties.GetName(textBox) == automationName) return textBox;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            try { return FindTextBox(child, automationName); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Text box '{automationName}' was not found.");
    }

    private static async Task RunOnStaAsync(Action action)
    {
        Exception? failure = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); completed.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
