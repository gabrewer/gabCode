using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Terminal.Views;

internal sealed class TerminalExitConfirmationDialog : Window
{
    internal TerminalExitConfirmationDialog(int activeTerminalCount)
    {
        Title = "Close gabCode?";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetName(this, "Confirm stopping active terminals");

        var message = new TextBlock
        {
            Text = activeTerminalCount == 1
                ? "1 active terminal process will be stopped. Running shell or Pi work will be interrupted."
                : $"{activeTerminalCount} active terminal processes will be stopped. Running shell or Pi work will be interrupted.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Foreground = Brushes.Black,
        };
        AutomationProperties.SetName(message, "Active terminal shutdown warning");

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
        };
        AutomationProperties.SetName(cancel, "Cancel closing gabCode");
        cancel.Click += (_, _) =>
        {
            Decision = TerminalExitDecision.Cancel;
            DialogResult = false;
        };

        var close = new Button
        {
            Content = "Close and Stop Terminals",
            MinWidth = 180,
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = true,
        };
        AutomationProperties.SetName(close, "Close gabCode and stop active terminals");
        close.Click += (_, _) =>
        {
            Decision = TerminalExitDecision.CloseAndStopTerminals;
            DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        _ = buttons.Children.Add(cancel);
        _ = buttons.Children.Add(close);

        var content = new StackPanel { Margin = new Thickness(24) };
        _ = content.Children.Add(new TextBlock
        {
            Text = "Active terminals are still running",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 0, 0, 12),
        });
        _ = content.Children.Add(message);
        _ = content.Children.Add(buttons);
        Content = content;
    }

    internal TerminalExitDecision Decision { get; private set; } = TerminalExitDecision.Cancel;
}
