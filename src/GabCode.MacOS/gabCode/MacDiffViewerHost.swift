import AppKit
import CryptoKit
import WebKit

/// Owns only the restricted, packaged presentation page. Git and filesystem authority never enter WebKit.
@MainActor
final class MacDiffViewerHost: NSObject, WKNavigationDelegate, WKScriptMessageHandler, WKUIDelegate {
    let webView: WKWebView
    let pageURL = URL(string: "gabcode-viewer://app/index.html")!
    private let resourceDirectory: URL
    private let resourceHandler: DiffViewerResourceHandler
    private var selectedIdentity = ""
    private var selectedGeneration = -1
    private(set) var isReady = false
    private var pageReady = false
    private var failure: String?
    private var closed = false
    var onEvent: (([String: Any]) -> Void)?
    var onFocusExit: (() -> Void)?

    func focusCode() async throws {
        guard !closed, isReady, selectedGeneration >= 0 else { throw HostError.unavailable("Viewer is not ready") }
        webView.window?.makeFirstResponder(webView)
        let payload: [String: Any] = ["kind": "focus", "generation": selectedGeneration, "identity": selectedIdentity]
        let data = try JSONSerialization.data(withJSONObject: payload)
        guard let json = String(data: data, encoding: .utf8) else { return }
        try await webView.evaluateJavaScript("window.gabCodeViewer.show(\(json))")
    }

    override init() {
        let resources = Bundle.main.resourceURL?.appendingPathComponent("NativeDiffViewer", isDirectory: true)
            ?? URL(fileURLWithPath: "/nonexistent/NativeDiffViewer", isDirectory: true)
        resourceDirectory = resources.standardizedFileURL
        resourceHandler = DiffViewerResourceHandler(root: resourceDirectory)
        let controller = WKUserContentController()
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.userContentController = controller
        configuration.setURLSchemeHandler(resourceHandler, forURLScheme: "gabcode-viewer")
        webView = MacReviewWebView(frame: .zero, configuration: configuration)
        super.init()
        controller.add(self, name: "gabCode")
        (webView as? MacReviewWebView)?.onFocusExit = { [weak self] in self?.onFocusExit?() }
        webView.navigationDelegate = self
        webView.uiDelegate = self
    }

    func select(identity: String, generation: Int) {
        guard !closed, !identity.isEmpty, identity.utf16.count <= 512, generation >= 0 else { return }
        selectedIdentity = identity
        selectedGeneration = generation
    }

    func load() async throws {
        guard !closed else { throw HostError.unavailable("Viewer closed") }
        do {
            try verifyBundle()
            webView.load(URLRequest(url: pageURL))
            let deadline = ContinuousClock.now.advanced(by: .seconds(15))
            while !pageReady && failure == nil && !closed && ContinuousClock.now < deadline {
                try Task.checkCancellation()
                try await Task.sleep(for: .milliseconds(50))
            }
            guard !closed, pageReady else {
                throw HostError.unavailable(failure ?? "Bundled viewer did not become ready")
            }
            // WKWebView fails Monaco's worker-side dynamic import on this app origin.
        // A static module import of the SAME packaged worker succeeds. Keep this
        // adaptation native-owned; no repository data or untrusted URL enters it.
        try await webView.evaluateJavaScript("""
        (() => {
          const scripts = Object.freeze({ editorWorkerService: 'editor', json: 'json', css: 'css', html: 'html', typescript: 'typescript', javascript: 'typescript' });
          const urls = new Map();
          window.MonacoEnvironment.getWorker = (_id, label) => {
            const name = scripts[label] || 'editor';
            let url = urls.get(name);
            if (!url) {
              const source = `import '${new URL('./workers/' + name + '.js', document.location.href)}'`;
              url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
              urls.set(name, url);
            }
            return new Worker(url, { type: 'module', name: label });
          };
          return true;
        })()
        """)
            guard !closed else { throw HostError.unavailable("Viewer closed") }
            isReady = true
        } catch {
            close()
            throw error
        }
    }

    func show(before: String, after: String, path: String, identity: String, generation: Int,
              mode: String = "side-by-side", beforeLabel: String = "Before", afterLabel: String = "After",
              beforeFacts: [String: Any] = ["bom": false, "eol": "LF", "finalNewline": true],
              afterFacts: [String: Any] = ["bom": false, "eol": "LF", "finalNewline": true]) async throws {
        guard !closed, isReady, identity == selectedIdentity, generation == selectedGeneration,
              !identity.isEmpty, identity.utf16.count <= 512, path.utf16.count <= 4096,
              before.utf16.count <= 1_000_000, after.utf16.count <= 1_000_000,
              beforeLabel.utf16.count <= 256, afterLabel.utf16.count <= 256,
              ["side-by-side", "inline", "file"].contains(mode) else {
            throw HostError.unavailable("Obsolete or oversized viewer snapshot")
        }
        let payload: [String: Any] = ["kind": "snapshot", "generation": generation,
            "identity": identity, "path": path, "before": before, "after": after,
            "beforeLabel": beforeLabel, "afterLabel": afterLabel, "mode": mode,
            "beforeFacts": beforeFacts, "afterFacts": afterFacts]
        let json = try JSONSerialization.data(withJSONObject: payload, options: [.fragmentsAllowed])
        guard json.count <= 2_100_000, let string = String(data: json, encoding: .utf8) else {
            throw HostError.unavailable("Viewer snapshot exceeds its safety limit")
        }
        // JSON is a JavaScript literal only after serialization, never interpolated repository text.
        try await webView.evaluateJavaScript("window.gabCodeViewer.show(\(string))")
        guard !closed, identity == selectedIdentity, generation == selectedGeneration else {
            throw HostError.unavailable("Viewer selection changed")
        }
    }

    func command(_ kind: String, value: String? = nil) async throws {
        guard !closed, isReady, selectedGeneration >= 0, ["mode", "change", "find", "focus", "deletion"].contains(kind) else {
            throw HostError.unavailable("Viewer command is unavailable")
        }
        var payload: [String: Any] = ["kind": kind, "generation": selectedGeneration, "identity": selectedIdentity]
        if kind == "mode" {
            guard let value, ["side-by-side", "inline", "file"].contains(value) else { throw HostError.unavailable("Invalid mode") }
            payload["mode"] = value
        } else if kind == "change" {
            guard let value, ["next", "previous"].contains(value) else { throw HostError.unavailable("Invalid change direction") }
            payload["direction"] = value
        } else if value != nil { throw HostError.unavailable("Unexpected command value") }
        let bytes = try JSONSerialization.data(withJSONObject: payload)
        guard let json = String(data: bytes, encoding: .utf8) else { throw HostError.unavailable("Invalid viewer command") }
        try await webView.evaluateJavaScript("window.gabCodeViewer.show(\(json))")
    }

    func close() {
        guard !closed else { return }
        closed = true
        isReady = false
        pageReady = false
        selectedIdentity = ""
        selectedGeneration = -1
        onEvent = nil
        onFocusExit = nil
        webView.stopLoading()
        webView.navigationDelegate = nil
        webView.uiDelegate = nil
        webView.configuration.userContentController.removeScriptMessageHandler(forName: "gabCode")
        webView.loadHTMLString("", baseURL: nil)
    }

    func allowsNavigation(to url: URL, isMainFrame: Bool) -> Bool {
        guard !closed, url.scheme == pageURL.scheme, url.host == pageURL.host,
              !url.path.split(separator: "/").contains(".."),
              !url.path.contains("\\"), !url.path.contains("%") else { return false }
        return !isMainFrame || url == pageURL
    }

    func accepts(event: [String: Any], origin: URL?, isMainFrame: Bool) -> Bool {
        guard !closed, isMainFrame, let origin, origin.standardizedFileURL == pageURL else { return false }
        guard let kind = event["kind"] as? String else { return false }
        switch kind {
        case "ready":
            return Set(event.keys) == ["kind"]
        case "failure":
            guard Set(event.keys) == ["kind", "generation", "identity", "message"],
                  let message = event["message"] as? String, !message.isEmpty, message.utf16.count <= 2048 else { return false }
        case "progress":
            guard Set(event.keys) == ["kind", "generation", "identity", "index", "total", "description", "mode", "deletion"],
                  let index = event["index"] as? Int, let total = event["total"] as? Int,
                  index >= 0, total >= 0, total <= 100_000, index <= total,
                  let description = event["description"] as? String, description.utf16.count <= 256,
                  let mode = event["mode"] as? String, ["side-by-side", "inline", "file"].contains(mode),
                  event["deletion"] is Bool else { return false }
        default:
            return false
        }
        guard let generation = event["generation"] as? Int, generation == selectedGeneration,
              let identity = event["identity"] as? String, identity == selectedIdentity else { return false }
        return true
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "gabCode", let event = message.body as? [String: Any],
              accepts(event: event, origin: message.frameInfo.request.url, isMainFrame: message.frameInfo.isMainFrame) else { return }
        if event["kind"] as? String == "ready" { pageReady = true }
        else { onEvent?(event) }
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(allowsNavigation(to: navigationAction.request.url ?? URL(fileURLWithPath: "/nonexistent"),
                                         isMainFrame: navigationAction.targetFrame?.isMainFrame == true) ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        failure = error.localizedDescription
    }
    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        failure = error.localizedDescription
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? { nil }

    private func verifyBundle() throws {
        let manifestURL = resourceDirectory.appendingPathComponent("manifest.json")
        let data = try Data(contentsOf: manifestURL)
        guard data.count < 64_000,
              let manifest = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              manifest["monaco"] as? String == "0.57.0",
              let assets = manifest["assets"] as? [[String: Any]], !assets.isEmpty, assets.count <= 100 else {
            throw HostError.unavailable("Invalid bundled viewer inventory")
        }
        for asset in assets {
            guard let path = asset["path"] as? String, !path.isEmpty, !path.hasPrefix("/"),
                  !path.split(separator: "/").contains(".."), !path.contains("\\"),
                  let size = asset["bytes"] as? Int, size >= 0, size <= 20_000_000,
                  let hash = asset["sha256"] as? String, hash.count == 64 else {
                throw HostError.unavailable("Invalid bundled viewer asset")
            }
            let url = resourceDirectory.appendingPathComponent(path)
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true, values.fileSize == size,
                  url.resolvingSymlinksInPath().path.hasPrefix(resourceDirectory.path + "/") else {
                throw HostError.unavailable("Viewer asset is missing or unsafe")
            }
            let bytes = try Data(contentsOf: url, options: [.mappedIfSafe])
            guard bytes.count == size,
                  SHA256.hash(data: bytes).map({ String(format: "%02x", $0) }).joined() == hash else {
                throw HostError.unavailable("Viewer asset integrity check failed")
            }
        }
    }

    enum HostError: LocalizedError {
        case unavailable(String)
        var errorDescription: String? { if case .unavailable(let message) = self { return message }; return nil }
    }
}
