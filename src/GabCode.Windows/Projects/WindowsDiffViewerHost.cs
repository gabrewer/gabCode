using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GabCode.Windows.Projects;

// WPF owns the page origin, message admission and WebView2 lifetime. The HTML is presentation only.
internal sealed class WindowsDiffViewerHost : UserControl, IDisposable
{
    internal const string Origin = "https://gabcode-diff.invalid/";
    private readonly WebView2 webView = new();
    private readonly string assets;
    private readonly string profile;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private long selectedGeneration = -1;
    private string selectedIdentity = string.Empty;
    private bool disposed;
    internal event EventHandler<string>? Unavailable;

    internal sealed record Snapshot(long Generation, string Identity, string Path, string Before, string After,
        string BeforeLabel, string AfterLabel, string Mode,
        bool BeforeBom = false, bool AfterBom = false, string BeforeEol = "LF", string AfterEol = "LF",
        bool BeforeFinalNewline = true, bool AfterFinalNewline = true);

    internal WindowsDiffViewerHost(string assets, string profile)
    {
        this.assets = assets;
        this.profile = profile;
        Content = webView;
        System.Windows.Automation.AutomationProperties.SetName(webView, "Read-only code comparison");
        webView.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape)
            {
                args.Handled = true;
                MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Previous));
            }
        };
    }

    internal void Select(long generation, string identity)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsDiffViewerHost));
        if (generation < 0 || identity.Length is < 1 or > 512) throw new ArgumentException("Invalid selection identity.");
        selectedGeneration = generation;
        selectedIdentity = identity;
    }

    internal static bool IsValidSnapshot(Snapshot? value, long expectedGeneration, string expectedIdentity) =>
        value is not null && value.Generation is >= 0 and <= 9007199254740991L && value.Generation == expectedGeneration &&
        value.Identity == expectedIdentity && value.Identity.Length is > 0 and <= 512 &&
        value.Path is { Length: <= 4096 } && value.Before is { Length: <= 1048576 } &&
        value.After is { Length: <= 1048576 } && value.BeforeLabel is { Length: <= 256 } &&
        value.AfterLabel is { Length: <= 256 } &&
        value.BeforeEol is "LF" or "CRLF" or "CR" or "mixed" or "none" &&
        value.AfterEol is "LF" or "CRLF" or "CR" or "mixed" or "none" &&
        value.Mode is "side-by-side" or "inline" or "file";

    internal static bool IsAllowedViewerMessage(string source, string json, string expectedOrigin)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !Uri.TryCreate(expectedOrigin, UriKind.Absolute, out var expected) ||
            uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port ||
            json.Length > 2200) return false;
        try
        {
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
            var name = kind.GetString();
            if (name == "ready") return root.EnumerateObject().Count() == 1;
            if (name != "failure" || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("message", out var detail)) return false;
            return detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: <= 2048 };
        }
        catch (JsonException) { return false; }
    }

    internal static bool VerifyBundle(string directory, out string error)
    {
        error = "Diff viewer assets are unavailable.";
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
            if (manifest.RootElement.GetProperty("monaco").GetString() != "0.57.0") return false;
            var required = new HashSet<string>(StringComparer.Ordinal) { "index.html", "viewer.js", "viewer.css", "workers/editor.js", "workers/json.js", "workers/css.js", "workers/html.js", "workers/typescript.js", "MONACO-LICENSE.txt", "MONACO-THIRD-PARTY-NOTICES.txt" };
            var entries = manifest.RootElement.GetProperty("assets").EnumerateArray().ToArray();
            if (entries.Length is < 10 or > 80) return false;
            foreach (var entry in entries)
            {
                var name = entry.GetProperty("path").GetString();
                if (string.IsNullOrEmpty(name) || name.Split('/').Any(segment => segment is "" or "." or "..") || name.Contains('\\') || Path.IsPathRooted(name)) return false;
                required.Remove(name);
                var file = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length != entry.GetProperty("bytes").GetInt64() ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (required.Count != 0) return false;
            error = string.Empty;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsDiffViewerHost));
        if (!VerifyBundle(assets, out var error)) { Unavailable?.Invoke(this, error); throw new InvalidOperationException(error); }
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        work.CancelAfter(TimeSpan.FromSeconds(15));
        work.Token.ThrowIfCancellationRequested();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
            await webView.EnsureCoreWebView2Async(environment).WaitAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
            var core = webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("gabcode-diff.invalid", assets, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => { if (args.Uri != Origin + "index.html") args.Cancel = true; };
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += (_, args) =>
            {
                if (!IsAllowedViewerMessage(args.Source, args.WebMessageAsJson, Origin)) return;
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (message.RootElement.GetProperty("kind").GetString() == "ready") ready.TrySetResult();
                else Unavailable?.Invoke(this, message.RootElement.GetProperty("message").GetString() ?? "Viewer failed.");
            };
            core.Navigate(Origin + "index.html");
            await ready.Task.WaitAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
        }
        catch (WebView2RuntimeNotFoundException runtimeError)
        {
            ready.TrySetException(runtimeError);
            Unavailable?.Invoke(this, "Diff unavailable: Microsoft Edge WebView2 Runtime is required.");
            throw;
        }
        catch (OperationCanceledException)
        {
            ready.TrySetCanceled();
            throw;
        }
        catch (Exception initializationError)
        {
            ready.TrySetException(initializationError);
            Unavailable?.Invoke(this, "Diff unavailable: " + initializationError.Message);
            throw;
        }
    }

    internal async Task ShowAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsDiffViewerHost));
        if (!IsValidSnapshot(snapshot, selectedGeneration, selectedIdentity)) throw new ArgumentException("Invalid viewer snapshot.", nameof(snapshot));
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await ready.Task.WaitAsync(work.Token);
        work.Token.ThrowIfCancellationRequested();
        if (disposed || !IsValidSnapshot(snapshot, selectedGeneration, selectedIdentity)) throw new OperationCanceledException("Review selection changed.");
        // No script string interpolation; WebView2 posts a JSON value to the packaged page.
        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            kind = "snapshot", generation = snapshot.Generation, identity = snapshot.Identity,
            path = snapshot.Path, before = snapshot.Before, after = snapshot.After,
            beforeLabel = snapshot.BeforeLabel, afterLabel = snapshot.AfterLabel, mode = snapshot.Mode,
            beforeFacts = new { bom = snapshot.BeforeBom, eol = snapshot.BeforeEol, finalNewline = snapshot.BeforeFinalNewline },
            afterFacts = new { bom = snapshot.AfterBom, eol = snapshot.AfterEol, finalNewline = snapshot.AfterFinalNewline }
        }));
    }

    // Narrow test probe for the actual renderer: evaluate only a fixed model-content read,
    // never an untrusted script or a native file path.
    internal async Task<(string Before, string After)> ReadDocumentForTestAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var json = await webView.ExecuteScriptAsync("(() => { const pair = window.gabCodeViewer?.models?.(); return pair ? JSON.stringify(pair) : null; })()");
            var value = JsonSerializer.Deserialize<string?>(json);
            if (value is not null)
            {
                using var pair = JsonDocument.Parse(value);
                return (pair.RootElement.GetProperty("before").GetString()!, pair.RootElement.GetProperty("after").GetString()!);
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Packaged viewer did not publish paired Monaco models.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        ready.TrySetCanceled();
        webView.Dispose();
        lifetime.Dispose();
    }
}
