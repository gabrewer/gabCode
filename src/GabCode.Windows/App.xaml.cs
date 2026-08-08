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
        var workspacePath = arguments.Length == 1
            ? arguments[0]
            : await new LastWorkspacePreference().ReadAsync();
        if (workspacePath is not null) await window.OpenWorkspaceAsync(workspacePath);
    }
}
