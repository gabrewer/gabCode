using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GabCode.Windows.Terminal.Settings;

internal sealed class TerminalFontSettingsDialog : Window
{
    private readonly TerminalFontPreferenceStore preferences;
    private readonly ComboBox facePicker = new() { MinWidth = 280 };
    private readonly TextBox sizeBox = new() { Width = 80 };
    private readonly TextBlock effective = new();
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono") };

    internal TerminalFontSettingsDialog(TerminalFontPreferenceStore preferences)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        Title = "Terminal Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, "Terminal font settings");

        foreach (var face in preferences.Catalog.SelectableFaces)
        {
            facePicker.Items.Add(face);
        }
        facePicker.DisplayMemberPath = nameof(TerminalFontFace.DisplayName);
        facePicker.SelectionChanged += (_, _) => SaveSelection();
        sizeBox.TextChanged += (_, _) => SaveSelection();

        var reset = new Button { Content = "Restore System Default", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(reset, "Restore system default terminal font");
        reset.Click += (_, _) => { preferences.RestoreSystemDefault(); Refresh(); };
        AutomationProperties.SetName(facePicker, "Terminal font face");
        AutomationProperties.SetName(sizeBox, "Terminal font point size");
        AutomationProperties.SetName(effective, "Effective terminal font");
        AutomationProperties.SetName(preview, "Terminal font preview: ordinary text, numbers, Unicode, and representative Powerline glyphs");

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Terminal font", FontSize = 18, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Font face", Margin = new Thickness(0, 14, 0, 4) },
                facePicker,
                new TextBlock { Text = "Point size (8–72)", Margin = new Thickness(0, 12, 0, 4) },
                sizeBox,
                effective,
                reset,
                new TextBlock { Text = "Preview", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) },
                preview,
            }
        };
        Refresh();
    }

    private void Refresh()
    {
        var selection = preferences.EffectiveSelection;
        facePicker.SelectedItem = preferences.Catalog.SelectableFaces.FirstOrDefault(face => string.Equals(face.Id, selection.FaceId, StringComparison.OrdinalIgnoreCase));
        sizeBox.Text = selection.PointSize.ToString("0.##");
        effective.Text = $"Effective: {selection.FaceId}, {selection.PointSize:0.##} pt";
        preview.FontFamily = new FontFamily(selection.FaceId!);
        preview.FontSize = selection.PointSize;
        preview.Text = "Aa Bb Cc 0123 你好 • Powerline:  \nTerminal font preview. Private-use glyphs are shown when supplied by the selected font.";
    }

    private void SaveSelection()
    {
        if (facePicker.SelectedItem is not TerminalFontFace face || !double.TryParse(sizeBox.Text, out var size) ||
            TerminalFontSelection.Named(face.Id, size) is not { } selection) return;
        preferences.Save(selection);
        Refresh();
    }
}
