using System.Windows;
using System.Windows.Controls;
using GabCode.Windows.Projects;
using GabCode.Windows.Terminal.Profiles;
using GabCode.Windows.Terminal.Views;
using GabCode.Windows.Terminal.Settings;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class WorktreeTerminalPair
{
    internal WorktreeTerminalPair(string path, Func<TerminalProfileResolution> resolveProfile, Func<TerminalFontSelection>? resolveFont = null)
    {
        Path = WorktreePath.Normalize(path);
        First = new TerminalSessionView(TerminalSessionKind.First, Path, resolveProfile, resolveFont);
        Second = new TerminalSessionView(TerminalSessionKind.Second, Path, resolveProfile, resolveFont);
        First.SessionChanged += TerminalSessionChanged;
        Second.SessionChanged += TerminalSessionChanged;
    }

    internal event EventHandler? SessionChanged;

    internal string Path { get; }
    internal TerminalSessionView First { get; }
    internal TerminalSessionView Second { get; }
    internal RetainedTerminalLayout? Layout { get; private set; }

    internal int ActiveTerminalCount => (First.IsActive ? 1 : 0) + (Second.IsActive ? 1 : 0);

    internal void Attach(ContentControl mainRegion, ContentControl bottomRegion)
    {
        Layout ??= new RetainedTerminalLayout(mainRegion, bottomRegion, First, Second);
        Layout.ShowPiInMain();
    }

    internal void ApplyFont(TerminalFontSelection selection)
    {
        First.ApplyFont(selection);
        Second.ApplyFont(selection);
    }

    internal async Task CloseAsync() => await Task.WhenAll(First.CloseAsync(), Second.CloseAsync());

    private void TerminalSessionChanged(object? sender, EventArgs e) => SessionChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class WorktreeTerminalRegistry
{
    private readonly Dictionary<string, WorktreeTerminalPair> pairs = new(WorktreePath.Comparer);
    private readonly Func<TerminalProfileResolution> resolveProfile;
    private readonly Func<TerminalFontSelection>? resolveFont;

    internal WorktreeTerminalRegistry(Func<TerminalProfileResolution> resolveProfile, Func<TerminalFontSelection>? resolveFont = null)
    {
        this.resolveProfile = resolveProfile ?? throw new ArgumentNullException(nameof(resolveProfile));
        this.resolveFont = resolveFont;
    }

    internal IEnumerable<WorktreeTerminalPair> Pairs => pairs.Values;

    internal WorktreeTerminalPair GetOrCreate(string worktreePath)
    {
        var path = WorktreePath.Normalize(worktreePath);
        if (!pairs.TryGetValue(path, out var pair))
        {
            pair = new WorktreeTerminalPair(path, resolveProfile, resolveFont);
            pairs.Add(path, pair);
        }
        return pair;
    }

    internal void ApplyFont(TerminalFontSelection selection)
    {
        foreach (var pair in pairs.Values) pair.ApplyFont(selection);
    }

    internal int ActiveTerminalCount => pairs.Values.Sum(pair => pair.ActiveTerminalCount);

    internal int GetActiveTerminalCount(string worktreePath) =>
        pairs.TryGetValue(WorktreePath.Normalize(worktreePath), out var pair) ? pair.ActiveTerminalCount : 0;

    internal async Task CloseAndRemoveAsync(string worktreePath)
    {
        var path = WorktreePath.Normalize(worktreePath);
        if (!pairs.TryGetValue(path, out var pair)) return;
        await pair.CloseAsync();
        pairs.Remove(path);
    }

    internal async Task CloseAllAsync() => await Task.WhenAll(pairs.Values.Select(pair => pair.CloseAsync()));
}
