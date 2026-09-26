using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GabCode.Windows.Projects;

internal sealed class GitHubIssueDialog : Window
{
    private readonly TextBox url = new() { MinWidth = 420 };
    private readonly TextBlock error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

    internal GitHubIssueDialog(GitHubIssueReference? current = null)
    {
        Title = current is null ? "Add GitHub issue" : "Replace GitHub issue";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var add = new Button { Content = current is null ? "Add" : "Replace", IsDefault = true, MinWidth = 88 };
        add.Click += Add_Click;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "GitHub issue URL" },
                url,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { add, cancel } },
            },
        };
        url.Text = current?.Url ?? string.Empty;
        AutomationProperties.SetName(url, "GitHub issue URL");
        AutomationProperties.SetName(error, "GitHub issue URL error");
        Loaded += (_, _) => url.Focus();
    }

    internal GitHubIssueReference? Issue { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Issue = GitHubIssueReference.Parse(url.Text);
            DialogResult = true;
        }
        catch (FormatException exception)
        {
            error.Text = exception.Message;
            error.Visibility = Visibility.Visible;
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            url.Focus();
        }
    }
}
