using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using GabCode.Windows.Projects;

namespace GabCode.Windows.Terminal.Settings;

internal sealed class TerminalFontSettingsDialog : Window
{
    private readonly TerminalFontPreferenceStore preferences;
    private readonly VisualStudioCodePreference visualStudioCodePreference;
    private readonly ComboBox facePicker = new() { MinWidth = 280 };
    private readonly TextBox sizeBox = new() { Width = 80 };
    private readonly TextBlock effective = new();
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono") };
    private readonly TextBox visualStudioCodePath = new() { MinWidth = 360 };
    private bool refreshing;

    internal TerminalFontSettingsDialog(TerminalFontPreferenceStore preferences, VisualStudioCodePreference? visualStudioCodePreference = null)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        this.visualStudioCodePreference = visualStudioCodePreference ?? new VisualStudioCodePreference();
        Title = "Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(this, "Terminal font settings");

        foreach (var face in preferences.Catalog.SelectableFaces)
        {
            facePicker.Items.Add(face);
        }
        facePicker.DisplayMemberPath = nameof(TerminalFontFace.DisplayName);
        facePicker.SelectionChanged += (_, _) => SaveFaceSelection();
        sizeBox.TextChanged += (_, _) => SaveSizeSelection();
        sizeBox.LostKeyboardFocus += (_, _) => Refresh();
        sizeBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            Refresh();
            args.Handled = true;
        };

        var reset = new Button { Content = "Restore System Default", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(reset, "Restore system default terminal font");
        reset.Click += (_, _) => RestoreSystemDefault();
        AutomationProperties.SetName(facePicker, "Terminal font face");
        AutomationProperties.SetName(sizeBox, "Terminal font point size");
        AutomationProperties.SetName(effective, "Effective terminal font");
        AutomationProperties.SetName(preview, "Terminal font preview: ordinary text, numbers, Unicode, and representative Powerline glyphs");
        visualStudioCodePath.Text = this.visualStudioCodePreference.Resolve();
        AutomationProperties.SetName(visualStudioCodePath, "VS Code executable path");
        var browseCode = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browseCode.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "VS Code executable (Code.exe)|Code.exe|Executable files|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) visualStudioCodePath.Text = dialog.FileName;
        };
        var saveCode = new Button { Content = "Save VS Code Path", Margin = new Thickness(0, 8, 0, 0) };
        saveCode.Click += (_, _) =>
        {
            if (File.Exists(visualStudioCodePath.Text.Trim())) this.visualStudioCodePreference.Write(visualStudioCodePath.Text.Trim());
            else MessageBox.Show(this, "Choose an existing Code.exe path.", "Invalid VS Code path", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

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
                new TextBlock { Text = "VS Code", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) },
                new StackPanel { Orientation = Orientation.Horizontal, Children = { visualStudioCodePath, browseCode } },
                saveCode,
            }
        };
        Refresh();
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            var selection = preferences.EffectiveSelection;
            facePicker.SelectedItem = preferences.Catalog.SelectableFaces.FirstOrDefault(face => string.Equals(face.Id, selection.FaceId, StringComparison.OrdinalIgnoreCase));
            sizeBox.Text = selection.PointSize.ToString("0.##");
            effective.Text = $"Effective: {selection.FaceId}, {selection.PointSize:0.##} pt";
            preview.FontFamily = new FontFamily(selection.FaceId!);
            preview.FontSize = PointSizeToDeviceIndependentPixels(selection.PointSize);
            preview.Text = "Aa Bb Cc 0123 你好 • Powerline:  \nTerminal font preview. Private-use glyphs are shown when supplied by the selected font.";
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SaveFaceSelection()
    {
        if (refreshing || facePicker.SelectedItem is not TerminalFontFace face) return;
        preferences.Save(TerminalFontSelection.Named(face.Id, preferences.EffectiveSelection.PointSize)!);
        Refresh();
    }

    private void SaveSizeSelection()
    {
        if (refreshing || facePicker.SelectedItem is not TerminalFontFace face || !double.TryParse(sizeBox.Text, out var size) ||
            TerminalFontSelection.Named(face.Id, size) is not { } selection) return;
        preferences.Save(selection);
        Refresh();
    }

    internal void RestoreSystemDefault()
    {
        preferences.RestoreSystemDefault();
        Refresh();
    }

    internal static double PointSizeToDeviceIndependentPixels(double pointSize) => pointSize * 96d / 72d;
}
