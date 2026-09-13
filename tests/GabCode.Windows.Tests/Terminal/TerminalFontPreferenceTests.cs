using System.IO;
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
    public void Reset_restores_cascadia_mono_at_twelve_points()
    {
        var store = new TerminalFontPreferenceStore(TemporaryPath(), Catalog());
        store.Save(TerminalFontSelection.Named("patched", 18)!);
        store.RestoreSystemDefault();
        Assert.Equal(TerminalFontSelection.Default, store.EffectiveSelection);
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
}
