using GabCode.Windows.Projects;
using System.Windows;

namespace GabCode.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _ = OpenInitialWorkspaceAsync(window, e.Args);
    }

    private static async Task OpenInitialWorkspaceAsync(MainWindow window, string[] arguments)
    {
        StartupWorkspaceSelection selection;
        try
        {
            selection = StartupWorkspaceSelection.Resolve(arguments);
        }
        catch (ArgumentException)
        {
            _ = await window.OpenWorkspaceAsync(string.Empty);
            return;
        }

        if (selection.IsExplicitEmpty) return;
        var workspacePath = selection.WorkspacePath ?? await new LastWorkspacePreference().ReadAsync();
        if (workspacePath is not null && !await window.OpenWorkspaceAsync(workspacePath) && selection.WorkspacePath is null)
        {
            new LastWorkspacePreference().Forget();
        }
    }
}
