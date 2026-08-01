using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GabCode.Windows.Tests.Terminal;

[Collection(WpfTestCollection.Name)]
public sealed class WindowsTerminalAssetTests
{
    private const string Upstream = "https://github.com/microsoft/terminal";
    private const string Version = "v1.24.11911.0";
    private const string Commit = "5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc";
    private const string LicenseHash = "5D177F23ECFEB0EA8E050B6A5A16355E1AE9A0B286436CA8F83ED08B3795BE6B";
    private const string NoticeHash = "E7FBAADEE6AB20C28B87730A510EE5F5815D8FB4BD88D1D54D282DC2A74C0726";
    private const string WpfAssemblyHash = "5B74201D3D8EEBB0D2FC3ABC35A1AB08EACFCA4203FBCFD4D1F5727F43EB386B";
    private const string NativeAssemblyHash = "1F56A0A3B903BEAB561E7BFBC22CA66221668D801215A969B1A76094ACC30CB5";

    [Fact]
    public void Pinned_terminal_assets_and_notices_are_present_and_match_the_approved_manifest()
    {
        var assetRoot = GetAssetRoot();
        var manifestPath = Path.Combine(assetRoot, "manifest.json");
        var licensePath = Path.Combine(assetRoot, "LICENSE");
        var noticePath = Path.Combine(assetRoot, "NOTICE.md");
        var wpfAssemblyPath = Path.Combine(assetRoot, "win-x64", "Microsoft.Terminal.Wpf.dll");
        var nativeAssemblyPath = Path.Combine(assetRoot, "win-x64", "Microsoft.Terminal.Control.dll");

        Assert.True(File.Exists(manifestPath), $"Missing approved terminal asset manifest: {manifestPath}");
        Assert.True(File.Exists(licensePath), $"Missing upstream MIT license: {licensePath}");
        Assert.True(File.Exists(noticePath), $"Missing upstream third-party notice: {noticePath}");
        Assert.True(File.Exists(wpfAssemblyPath), $"Missing managed terminal wrapper: {wpfAssemblyPath}");
        Assert.True(File.Exists(nativeAssemblyPath), $"Missing native terminal control: {nativeAssemblyPath}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal(Upstream, root.GetProperty("upstream").GetString());
        Assert.Equal(Version, root.GetProperty("tag").GetString());
        Assert.Equal(Commit, root.GetProperty("commit").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        Assert.Equal("Release", root.GetProperty("configuration").GetString());
        Assert.Equal("x64", root.GetProperty("platform").GetString());
        Assert.Equal("10.0.22621.0", root.GetProperty("windowsSdk").GetString());
        Assert.Equal("v143", root.GetProperty("platformToolset").GetString());
        Assert.Equal(LicenseHash, GetManifestFileHash(root, "LICENSE"));
        Assert.Equal(NoticeHash, GetManifestFileHash(root, "NOTICE.md"));
        Assert.Equal(1116L, GetManifestFileBytes(root, "LICENSE"));
        Assert.Equal(23176L, GetManifestFileBytes(root, "NOTICE.md"));
        Assert.Equal(WpfAssemblyHash, GetManifestHash(root, "Microsoft.Terminal.Wpf.dll", "win-x64/Microsoft.Terminal.Wpf.dll"));
        Assert.Equal(NativeAssemblyHash, GetManifestHash(root, "Microsoft.Terminal.Control.dll", "win-x64/Microsoft.Terminal.Control.dll"));
        Assert.Equal(23552L, GetManifestAssetBytes(root, "Microsoft.Terminal.Wpf.dll"));
        Assert.Equal(1653760L, GetManifestAssetBytes(root, "Microsoft.Terminal.Control.dll"));
        Assert.Equal(LicenseHash, ComputeSha256(licensePath));
        Assert.Equal(NoticeHash, ComputeSha256(noticePath));
        Assert.Equal(WpfAssemblyHash, ComputeSha256(wpfAssemblyPath));
        Assert.Equal(NativeAssemblyHash, ComputeSha256(nativeAssemblyPath));
        Assert.Equal(1116L, new FileInfo(licensePath).Length);
        Assert.Equal(23176L, new FileInfo(noticePath).Length);
        Assert.Equal(23552L, new FileInfo(wpfAssemblyPath).Length);
        Assert.Equal(1653760L, new FileInfo(nativeAssemblyPath).Length);
    }

    [Fact]
    public async Task Terminal_runtime_assets_are_deployed_and_the_managed_control_constructs_on_an_sta_thread()
    {
        var wpfAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Microsoft.Terminal.Wpf.dll");
        var nativeAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Microsoft.Terminal.Control.dll");
        Assert.True(File.Exists(wpfAssemblyPath), $"Managed terminal wrapper was not deployed to test output: {wpfAssemblyPath}");
        Assert.True(File.Exists(nativeAssemblyPath), $"Native terminal control was not deployed to test output: {nativeAssemblyPath}");

        Exception? failure = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = Assembly.LoadFrom(wpfAssemblyPath);
                var terminalControlType = assembly.GetType("Microsoft.Terminal.Wpf.TerminalControl", throwOnError: true)!;
                var control = Activator.CreateInstance(terminalControlType);
                Assert.NotNull(control);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completion.TrySetResult();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(completion.Task, completed);
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)), "Terminal-control STA thread did not terminate.");

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"Terminal control did not construct: {failure}");
        }
    }

    private static string GetAssetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GabCode.slnx")))
            {
                return Path.Combine(directory.FullName, "third_party", "microsoft-terminal", Version);
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate the repository root from the test output directory.");
    }

    private static string GetManifestHash(JsonElement root, string fileName, string relativePath)
    {
        var asset = GetManifestEntry(root.GetProperty("assets"), fileName);
        Assert.Equal(relativePath, asset.GetProperty("path").GetString());
        Assert.Equal(fileName, asset.GetProperty("file").GetString());
        return asset.GetProperty("sha256").GetString() ?? string.Empty;
    }

    private static long GetManifestAssetBytes(JsonElement root, string fileName) =>
        GetManifestEntry(root.GetProperty("assets"), fileName).GetProperty("bytes").GetInt64();

    private static string GetManifestFileHash(JsonElement root, string fileName) =>
        GetManifestEntry(root.GetProperty("provenanceFiles"), fileName).GetProperty("sha256").GetString() ?? string.Empty;

    private static long GetManifestFileBytes(JsonElement root, string fileName) =>
        GetManifestEntry(root.GetProperty("provenanceFiles"), fileName).GetProperty("bytes").GetInt64();

    private static JsonElement GetManifestEntry(JsonElement entries, string fileName)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("file").GetString(), fileName, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        throw new Xunit.Sdk.XunitException($"Approved manifest does not contain {fileName}.");
    }

    private static string ComputeSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
