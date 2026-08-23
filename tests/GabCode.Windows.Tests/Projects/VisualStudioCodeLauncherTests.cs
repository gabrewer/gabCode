using System.IO;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class VisualStudioCodeLauncherTests
{
    [Fact]
    public void Prefers_user_installed_code_executable_before_path_command()
    {
        var local = Path.Combine("C:", "Users", "gabre", "AppData", "Local");
        var result = VisualStudioCodeLauncher.FindExecutable(
            folder => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => local,
                Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
                _ => @"C:\Program Files (x86)",
            },
            path => path == Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));

        Assert.Equal(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), result);
    }

    [Fact]
    public void Falls_back_to_code_command_when_no_standard_installation_exists() =>
        Assert.Equal("code", VisualStudioCodeLauncher.FindExecutable(_ => string.Empty, _ => false));
}
