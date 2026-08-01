using System.Windows;

namespace GabCode.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var workingDirectory = e.Args.Length == 0 ? Environment.CurrentDirectory : e.Args[0];
        MainWindow = new MainWindow(workingDirectory);
        MainWindow.Show();
    }
}
