import Foundation
import WebKit

/// A fixed, offline app origin for Monaco modules and workers; never resolves repository paths.
final class DiffViewerResourceHandler: NSObject, WKURLSchemeHandler {
    private let root: URL
    init(root: URL) { self.root = root; super.init() }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url = task.request.url, url.scheme == "gabcode-viewer", url.host == "app",
              !url.path.contains("%"), !url.path.contains("\\"),
              !url.path.split(separator: "/").contains(".."),
              url.query == nil, url.fragment == nil else {
            task.didFailWithError(URLError(.badURL)); return
        }
        let path = String(url.path.dropFirst())
        let file = root.appendingPathComponent(path)
        guard !path.isEmpty, file.resolvingSymlinksInPath().path.hasPrefix(root.path + "/"),
              let values = try? file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey]),
              values.isRegularFile == true, values.isSymbolicLink != true,
              let data = try? Data(contentsOf: file), data.count <= 20_000_000 else {
            task.didFailWithError(URLError(.fileDoesNotExist)); return
        }
        let mime: String
        switch file.pathExtension {
        case "html": mime = "text/html"
        case "js": mime = "text/javascript"
        case "css": mime = "text/css"
        case "woff": mime = "font/woff"
        case "woff2": mime = "font/woff2"
        case "ttf": mime = "font/ttf"
        default: task.didFailWithError(URLError(.unsupportedURL)); return
        }
        guard let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
                                             headerFields: ["Content-Type": mime, "Content-Length": "\(data.count)",
                                                            "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"]) else {
            task.didFailWithError(URLError(.badServerResponse)); return
        }
        task.didReceive(response)
        task.didReceive(data)
        task.didFinish()
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {}
}
