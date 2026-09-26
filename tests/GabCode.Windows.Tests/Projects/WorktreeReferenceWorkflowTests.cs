using System.IO;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeReferenceWorkflowTests
{
    [Fact]
    public void Main_window_declares_a_keyboard_accessible_references_bar_and_context_actions()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GabCode.Windows");
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"WorktreeReferencesBar\"", xaml);
        Assert.Contains("Choose Markdown", xaml);
        Assert.Contains("Add issue", xaml);
        Assert.Contains("Open Markdown", xaml);
        Assert.Contains("Open issue", xaml);
        Assert.Contains("AssignMarkdown", code);
        Assert.Contains("AssignIssue", code);
        Assert.Contains("OpenMarkdown", code);
        Assert.Contains("OpenIssue", code);
        Assert.Contains("RemoveWorktreeAsync", code);
    }
}
