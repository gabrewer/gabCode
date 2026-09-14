using System.IO;
using System.Windows.Threading;
using GabCode.Windows.Terminal.Settings;

namespace GabCode.Windows.Tests.Terminal;

public sealed class TerminalFontPreferenceTests
{
    [Fact]
    public void Selection_validates_face_and_point_size_bounds()
    {
        Assert.Equal(8, TerminalFontSelection.MinimumPointSize);
        Assert.Equal(72, TerminalFontSelection.MaximumPointSize);
        Assert.NotNull(TerminalFontSelection.Named("MesloLGMNerdFontMono-Regular", 8));
        Assert.NotNull(TerminalFontSelection.SystemDefault(72));
        Assert.Null(TerminalFontSelection.Named("", 12));
        Assert.Null(TerminalFontSelection.Named("face", 7.9));
        Assert.Null(TerminalFontSelection.Named("face", 72.1));
        Assert.Null(TerminalFontSelection.Named("face", double.NaN));
    }

    [Fact]
    public void Catalog_selects_only_fixed_pitch_faces_in_stable_order_without_name_heuristics()
    {
        var catalog = new TerminalFontCatalog(
        [
            new TerminalFontFace("patched", "Patched face", true),
            new TerminalFontFace("proportional", "A proportional face", false),
            new TerminalFontFace("z", "Z face", true),
            new TerminalFontFace("patched", "Duplicate", true),
        ]);

        Assert.Equal(["patched", "z"], catalog.SelectableFaces.Select(face => face.Id));
        Assert.True(catalog.IsSelectable("patched"));
        Assert.False(catalog.IsSelectable("proportional"));
    }

    [Fact]
    public void Store_round_trips_named_selection_and_repairs_invalid_saved_state()
    {
        var path = TemporaryPath();
        var catalog = Catalog();
        var expected = TerminalFontSelection.Named("patched", 14)!;
        new TerminalFontPreferenceStore(path, catalog).Save(expected);
        Assert.Equal(expected, new TerminalFontPreferenceStore(path, catalog).EffectiveSelection);

        File.WriteAllText(path, "{ \"face\": \"proportional\", \"pointSize\": 200 }");
        var repaired = new TerminalFontPreferenceStore(path, catalog);
        Assert.Equal(TerminalFontSelection.Default, repaired.EffectiveSelection);
        Assert.Equal(TerminalFontSelection.Default, new TerminalFontPreferenceStore(path, catalog).EffectiveSelection);
    }

    [Fact]
    public async Task Concurrent_process_style_writes_leave_one_complete_valid_preference()
    {
        var path = TemporaryPath();
        var stores = Enumerable.Range(0, 12).Select(_ => new TerminalFontPreferenceStore(path, Catalog())).ToArray();

        await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
            store.Save(TerminalFontSelection.Named(index % 2 == 0 ? "Cascadia Mono" : "patched", 12 + index)!))));

        var restored = new TerminalFontPreferenceStore(path, Catalog()).EffectiveSelection;
        Assert.True(Catalog().IsSelectable(restored.FaceId));
        Assert.InRange(restored.PointSize, 12, 23);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Reset_restores_cascadia_mono_at_twelve_points_with_one_change_notification()
    {
        var store = new TerminalFontPreferenceStore(TemporaryPath(), Catalog());
        store.Save(TerminalFontSelection.Named("patched", 18)!);
        var notifications = 0;
        store.Changed += _ => notifications++;

        store.RestoreSystemDefault();
        store.RestoreSystemDefault();

        Assert.Equal(TerminalFontSelection.Default, store.EffectiveSelection);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Installed_catalog_contains_the_current_windows_default_terminal_face()
    {
        var catalog = TerminalFontCatalog.Installed();

        Assert.NotEmpty(catalog.SelectableFaces);
        Assert.True(catalog.IsSelectable("Cascadia Mono"), "Cascadia Mono must be selectable on the target Windows development machine.");
    }

    [Fact]
    public void Settings_refresh_and_reset_do_not_resave_stale_control_values()
    {
        RunOnSta(() =>
        {
            var store = new TerminalFontPreferenceStore(TemporaryPath(), Catalog());
            var patched = TerminalFontSelection.Named("patched", 18)!;
            store.Save(patched);

            var dialog = new TerminalFontSettingsDialog(store);
            Assert.Equal(patched, store.EffectiveSelection);

            dialog.RestoreSystemDefault();
            Assert.Equal(TerminalFontSelection.Default, store.EffectiveSelection);
            dialog.Close();
        });
    }

    [Fact]
    public void Preview_converts_points_to_wpf_device_independent_pixels()
    {
        Assert.Equal(16, TerminalFontSettingsDialog.PointSizeToDeviceIndependentPixels(12));
    }

    [Fact]
    public void Missing_cascadia_default_uses_an_installed_fixed_pitch_fallback()
    {
        var path = TemporaryPath();
        var catalog = new TerminalFontCatalog(
        [
            new TerminalFontFace("proportional", "A proportional face", false),
            new TerminalFontFace("patched", "Patched face", true),
        ]);

        var store = new TerminalFontPreferenceStore(path, catalog);

        Assert.Equal(TerminalFontSelection.Named("patched", 12), store.EffectiveSelection);
        Assert.Equal(store.EffectiveSelection, new TerminalFontPreferenceStore(path, catalog).EffectiveSelection);
    }

    private static TerminalFontCatalog Catalog() => new(
    [
        new TerminalFontFace("Cascadia Mono", "Cascadia Mono", true),
        new TerminalFontFace("patched", "Patched face", true),
        new TerminalFontFace("proportional", "Proportional", false),
    ]);

    private static string TemporaryPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gabcode-font-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "terminal-font.json");
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Settings-dialog STA thread did not terminate.");
        if (failure is not null) throw failure;
    }
}
