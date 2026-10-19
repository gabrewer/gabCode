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
    internal event EventHandler<ViewerFailure>? Unavailable;
    internal event EventHandler<ViewerProgress>? ProgressChanged;

    // Snapshot failures carry their accepted selection through deferred native UI work.
    // Initialization failures have no selected snapshot and are scoped to this host.
    internal sealed record ViewerFailure(string Message, long? Generation = null, string? Identity = null);

    internal sealed record ViewerProgress(long Generation, string Identity, int Index, int Total,
        string Description, bool Deletion, string Mode);

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
            json.Length > 2800) return false;
        try
        {
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
            var name = kind.GetString();
            if (name == "ready") return root.EnumerateObject().Count() == 1;
            if (name == "progress")
            {
                if (root.EnumerateObject().Count() != 8 ||
                    !root.TryGetProperty("generation", out var progressGeneration) ||
                    !root.TryGetProperty("identity", out var progressIdentity) ||
                    !root.TryGetProperty("index", out var index) ||
                    !root.TryGetProperty("total", out var total) ||
                    !root.TryGetProperty("description", out var description) ||
                    !root.TryGetProperty("deletion", out var deletion) ||
                    !root.TryGetProperty("mode", out var mode)) return false;
                return progressGeneration.ValueKind == JsonValueKind.Number && progressGeneration.TryGetInt64(out var pg) &&
                    pg is >= 0 and <= 9007199254740991L &&
                    progressIdentity.ValueKind == JsonValueKind.String && progressIdentity.GetString() is { Length: > 0 and <= 512 } &&
                    total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) && count is >= 0 and <= 1000000 &&
                    index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var position) && position >= 0 && position <= count &&
                    description.ValueKind == JsonValueKind.String && description.GetString() is { Length: <= 256 } &&
                    deletion.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    mode.ValueKind == JsonValueKind.String && mode.GetString() is "side-by-side" or "inline" or "file";
            }
            if (name != "failure" || root.EnumerateObject().Count() != 4 ||
                !root.TryGetProperty("message", out var detail) || !root.TryGetProperty("generation", out var generation) ||
                !root.TryGetProperty("identity", out var identity)) return false;
            return detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: <= 2048 } &&
                generation.ValueKind == JsonValueKind.Number && generation.TryGetInt64(out var value) && value is >= 0 and <= 9007199254740991L &&
                identity.ValueKind == JsonValueKind.String && identity.GetString() is { Length: > 0 and <= 512 };
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
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        work.CancelAfter(TimeSpan.FromSeconds(15));
        work.Token.ThrowIfCancellationRequested();
        try
        {
            // Verify all bytes on a worker, never block WPF input while hashing the
            // packaged bundle. WebView2 cannot navigate before verification succeeds.
            var verification = await Task.Run(() =>
            {
                var valid = VerifyBundle(assets, out var reason);
                return (valid, reason);
            }, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!verification.valid) throw new InvalidOperationException(verification.reason);
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
                var kind = message.RootElement.GetProperty("kind").GetString();
                if (kind == "ready") { ready.TrySetResult(); return; }
                if (message.RootElement.GetProperty("generation").GetInt64() != selectedGeneration ||
                    message.RootElement.GetProperty("identity").GetString() != selectedIdentity) return;
                if (kind == "progress")
                {
                    ProgressChanged?.Invoke(this, new ViewerProgress(selectedGeneration, selectedIdentity,
                        message.RootElement.GetProperty("index").GetInt32(), message.RootElement.GetProperty("total").GetInt32(),
                        message.RootElement.GetProperty("description").GetString() ?? string.Empty,
                        message.RootElement.GetProperty("deletion").GetBoolean(), message.RootElement.GetProperty("mode").GetString()!));
                }
                else Unavailable?.Invoke(this, new ViewerFailure(message.RootElement.GetProperty("message").GetString() ?? "Viewer failed.",
                    message.RootElement.GetProperty("generation").GetInt64(), message.RootElement.GetProperty("identity").GetString()));
            };
            core.Navigate(Origin + "index.html");
            await ready.Task.WaitAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
        }
        catch (WebView2RuntimeNotFoundException runtimeError)
        {
            ready.TrySetException(runtimeError);
            Unavailable?.Invoke(this, new ViewerFailure("Diff unavailable: Microsoft Edge WebView2 Runtime is required."));
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
            Unavailable?.Invoke(this, new ViewerFailure("Diff unavailable: " + initializationError.Message));
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

    internal async Task SendControlAsync(long generation, string identity, string kind, string value = "", CancellationToken token = default)
    {
        if (kind is not ("mode" or "change" or "find" or "focus" or "deletion") ||
            kind == "mode" && value is not ("side-by-side" or "inline" or "file") ||
            kind == "change" && value is not ("next" or "previous") ||
            (kind is "find" or "focus" or "deletion") && value.Length != 0)
            throw new ArgumentException("Unsupported viewer control.");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await ready.Task.WaitAsync(work.Token);
        work.Token.ThrowIfCancellationRequested();
        if (disposed || generation != selectedGeneration || identity != selectedIdentity)
            throw new OperationCanceledException("Review selection changed.");
        if (kind == "focus") webView.Focus();
        webView.CoreWebView2.PostWebMessageAsJson(kind switch
        {
            "mode" => JsonSerializer.Serialize(new { kind, generation, identity, mode = value }),
            "change" => JsonSerializer.Serialize(new { kind, generation, identity, direction = value }),
            _ => JsonSerializer.Serialize(new { kind, generation, identity })
        });
    }

    internal async Task<int> EmbeddedNavigationButtonCountForTestAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var json = await webView.ExecuteScriptAsync("document.querySelectorAll('button').length");
        return JsonSerializer.Deserialize<int>(json);
    }

    internal async Task AttemptTypingForTestAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var focused = await webView.ExecuteScriptAsync("(() => { const input = [...document.querySelectorAll('.monaco-editor textarea')].at(-1); input?.focus(); return !!input; })()");
        if (focused != "true") throw new InvalidOperationException("File editor input could not be focused for the read-only test.");
        await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"tampered\"}");
    }

    internal async Task<string> WaitForChangeStatusForTestAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var last = string.Empty;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var json = await webView.ExecuteScriptAsync("document.querySelector('[role=status]')?.textContent ?? ''");
            last = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
            if (last.Contains("Change 1 of 1", StringComparison.Ordinal)) return last;
            await Task.Delay(50);
        }
        throw new TimeoutException("Monaco did not compute the shared change map: " + last);
    }

    internal async Task SetLineForTestAsync(int line)
    {
        if (line is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(line));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await webView.ExecuteScriptAsync($"window.gabCodeViewer?.line({line})");
    }

    internal async Task<int> WaitForLineForTestAsync(int line)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var json = await webView.ExecuteScriptAsync("window.gabCodeViewer?.line(null)");
            if (JsonSerializer.Deserialize<int?>(json) is { } actual && actual == line) return actual;
            await Task.Delay(50);
        }
        throw new TimeoutException("Review position did not survive the snapshot replacement.");
    }

    internal async Task<string> WaitForModeForTestAsync(string expectedMode)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var json = await webView.ExecuteScriptAsync("window.gabCodeViewer?.models?.()?.mode ?? null");
            var actual = JsonSerializer.Deserialize<string?>(json);
            if (actual == expectedMode) return actual;
            await Task.Delay(50);
        }
        throw new TimeoutException("Packaged viewer did not enter the requested mode.");
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
