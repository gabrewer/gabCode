import AppKit
import WebKit

/// F6 leaves the embedded document; the native owner decides the next focus target.
@MainActor
final class MacReviewWebView: WKWebView {
    var onFocusExit: (() -> Void)?

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if event.type == .keyDown && event.keyCode == 97 { // macOS F6
            onFocusExit?()
            return true
        }
        return super.performKeyEquivalent(with: event)
    }
}
