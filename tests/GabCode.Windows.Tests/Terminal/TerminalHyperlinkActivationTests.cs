using System.IO;
using GabCode.Windows.Terminal.Hosting;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class TerminalHyperlinkActivationTests
{
    [Fact]
    public void Resolves_supported_web_urls_without_treating_port_as_line_suffix()
    {
        var service = new TerminalHyperlinkActivationService(Environment.CurrentDirectory);

        Assert.True(service.TryResolveTarget("https://example.test:8443/path", out var target));
        Assert.Equal("https://example.test:8443/path", target);
    }

    [Fact]
    public void Resolves_existing_relative_file_and_removes_line_column_suffix()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "probe.txt");
        File.WriteAllText(file, "probe");
        var service = new TerminalHyperlinkActivationService(directory.Path);

        Assert.True(service.TryResolveTarget("probe.txt:12:4", out var target));
        Assert.Equal(Path.GetFullPath(file), target);
    }

    [Fact]
    public void Resolves_existing_relative_file_with_spaces()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "space probe.txt");
        File.WriteAllText(file, "probe");
        var service = new TerminalHyperlinkActivationService(directory.Path);

        Assert.True(service.TryResolveTarget("space probe.txt", out var target));
        Assert.Equal(Path.GetFullPath(file), target);
    }

    [Fact]
    public void Resolves_existing_absolute_windows_style_path()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "absolute.txt");
        File.WriteAllText(file, "probe");
        var service = new TerminalHyperlinkActivationService(Environment.CurrentDirectory);

        Assert.True(service.TryResolveTarget(file, out var target));
        Assert.Equal(Path.GetFullPath(file), target);
    }

    [Fact]
    public void Resolves_file_uri_to_existing_target()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "space probe.txt");
        File.WriteAllText(file, "probe");
        var service = new TerminalHyperlinkActivationService(directory.Path);

        Assert.True(service.TryResolveTarget(new Uri(file).AbsoluteUri, out var target));
        Assert.Equal(Path.GetFullPath(file), target);
    }

    [Fact]
    public void Rejects_unc_path()
    {
        var service = new TerminalHyperlinkActivationService(Environment.CurrentDirectory);

        Assert.False(service.TryResolveTarget("\\\\remote.example.test\\share\\probe.txt", out _));
    }

    [Fact]
    public void Rejects_remote_file_uri()
    {
        var service = new TerminalHyperlinkActivationService(Environment.CurrentDirectory);

        Assert.False(service.TryResolveTarget("file://remote.example.test/share/probe.txt", out _));
    }

    [Theory]
    [InlineData("ftp://example.test/file")]
    [InlineData("mailto:user@example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("cmd.exe /c whoami")]
    [InlineData("missing.txt")]
    public void Rejects_unsafe_or_missing_targets(string value)
    {
        var service = new TerminalHyperlinkActivationService(Environment.CurrentDirectory);

        Assert.False(service.TryResolveTarget(value, out _));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gabcode-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
