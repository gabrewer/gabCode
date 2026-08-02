using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Terminal.Views;

internal sealed class TerminalMultilinePasteConfirmationDialog : Window
{
    internal TerminalMultilinePasteConfirmationDialog(TerminalPastePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        Title = "Paste multiple lines into the terminal?";
        Width = 580;
        MaxHeight = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetName(this, "Confirm multiline terminal paste");

        var warning = new TextBlock
        {
            Text = "This text may run multiple commands.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        };
        AutomationProperties.SetName(warning, "Multiline paste warning");

        var previewBox = new TextBox
        {
            Text = preview.Text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Cascadia Mono"),
            MinHeight = 96,
            MaxHeight = 260,
            Margin = new Thickness(0, 12, 0, 0),
        };
        AutomationProperties.SetName(previewBox, "Pasted text preview");
        AutomationProperties.SetHelpText(previewBox, preview.AccessibleDescription);

        var truncation = new TextBlock
        {
            Text = "Preview truncated",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brushes.Black,
            Visibility = preview.IsTruncated ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetName(truncation, "Preview truncated");

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
        };
        AutomationProperties.SetName(cancel, "Cancel multiline terminal paste");
        cancel.Click += (_, _) => DialogResult = false;

        var paste = new Button
        {
            Content = "Paste",
            MinWidth = 92,
            Padding = new Thickness(12, 6, 12, 6),
        };
        AutomationProperties.SetName(paste, "Paste the displayed multiline text into the terminal");
        paste.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        _ = buttons.Children.Add(cancel);
        _ = buttons.Children.Add(paste);

        var content = new StackPanel { Margin = new Thickness(24) };
        _ = content.Children.Add(new TextBlock
        {
            Text = "Paste multiple lines into the terminal?",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 0, 0, 12),
        });
        _ = content.Children.Add(warning);
        _ = content.Children.Add(previewBox);
        _ = content.Children.Add(truncation);
        _ = content.Children.Add(buttons);
        Content = content;

        Loaded += (_, _) => _ = cancel.Focus();
    }
}
