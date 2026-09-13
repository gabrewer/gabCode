using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GabCode.Windows.Terminal.Settings;

internal sealed record TerminalFontSelection(string? FaceId, double PointSize)
{
    internal const double MinimumPointSize = 8;
    internal const double MaximumPointSize = 72;
    internal static TerminalFontSelection Default { get; } = new("Cascadia Mono", 12);
    internal bool IsSystemDefault => FaceId is null || string.Equals(FaceId, Default.FaceId, StringComparison.OrdinalIgnoreCase) && PointSize == Default.PointSize;

    internal static TerminalFontSelection? SystemDefault(double pointSize) => Create(Default.FaceId, pointSize);
    internal static TerminalFontSelection? Named(string? faceId, double pointSize) => Create(faceId, pointSize);

    private static TerminalFontSelection? Create(string? faceId, double pointSize) =>
        string.IsNullOrWhiteSpace(faceId) || !double.IsFinite(pointSize) || pointSize < MinimumPointSize || pointSize > MaximumPointSize
            ? null
            : new TerminalFontSelection(faceId, pointSize);
}

internal sealed record TerminalFontFace(string Id, string DisplayName, bool IsFixedPitch);

internal sealed class TerminalFontCatalog
{
    private readonly Dictionary<string, TerminalFontFace> faces;

    internal TerminalFontCatalog(IEnumerable<TerminalFontFace> faces)
    {
        this.faces = faces
            .GroupBy(face => face.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(face => face.Id, StringComparer.OrdinalIgnoreCase);
        SelectableFaces = this.faces.Values
            .Where(face => face.IsFixedPitch)
            .OrderBy(face => face.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(face => face.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlyList<TerminalFontFace> SelectableFaces { get; }
    internal bool IsSelectable(string? id) => id is not null && faces.TryGetValue(id, out var face) && face.IsFixedPitch;

    internal static TerminalFontCatalog Installed() => new(Fonts.SystemFontFamilies
        .Select(family =>
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var fixedPitch = typeface.TryGetGlyphTypeface(out var glyph) &&
                glyph.AdvanceWidths.Count != 0 && glyph.AdvanceWidths.Values.Distinct().Take(2).Count() == 1;
            return new TerminalFontFace(family.Source, family.Source, fixedPitch);
        }));
}

internal sealed class TerminalFontPreferenceStore
{
    private readonly string path;
    private readonly TerminalFontCatalog catalog;

    internal TerminalFontPreferenceStore(string? path = null, TerminalFontCatalog? catalog = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gabCode", "terminal-font.json");
        this.catalog = catalog ?? TerminalFontCatalog.Installed();
        EffectiveSelection = Restore();
    }

    internal event EventHandler<TerminalFontSelection>? Changed;
    internal TerminalFontSelection EffectiveSelection { get; private set; }

    internal void Save(TerminalFontSelection selection)
    {
        var effective = Validate(selection) ?? ResolveDefault();
        Write(effective);
        SetEffective(effective);
    }

    internal void RestoreSystemDefault() => Save(ResolveDefault());

    private TerminalFontSelection Restore()
    {
        try
        {
            if (File.Exists(path))
            {
                var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path));
                var selection = persisted is null ? null : TerminalFontSelection.Named(persisted.Face, persisted.PointSize);
                if (selection is not null && Validate(selection) is { } valid)
                {
                    return valid;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        var fallback = ResolveDefault();
        Write(fallback);
        return fallback;
    }

    private TerminalFontSelection ResolveDefault() =>
        catalog.IsSelectable(TerminalFontSelection.Default.FaceId)
            ? TerminalFontSelection.Default
            : catalog.SelectableFaces.FirstOrDefault() is { } fallback
                ? TerminalFontSelection.Named(fallback.Id, TerminalFontSelection.Default.PointSize)!
                : TerminalFontSelection.Default;

    private TerminalFontSelection? Validate(TerminalFontSelection selection) =>
        TerminalFontSelection.Named(selection.FaceId, selection.PointSize) is { } valid && catalog.IsSelectable(valid.FaceId) ? valid : null;

    private void Write(TerminalFontSelection selection)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Persisted(selection.FaceId!, selection.PointSize)));
        File.Move(temporary, path, overwrite: true);
    }

    private void SetEffective(TerminalFontSelection selection)
    {
        if (EffectiveSelection == selection) return;
        EffectiveSelection = selection;
        Changed?.Invoke(this, selection);
    }

    private sealed record Persisted(string Face, double PointSize);
}
