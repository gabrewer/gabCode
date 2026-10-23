import AppKit
import WebKit
@testable import gabCode
import XCTest

@MainActor
final class MacDiffViewerHostTests: XCTestCase {
    func testRejectsUntrustedViewerEventsAndObsoleteSelections() throws {
        let host = MacDiffViewerHost()
        host.select(identity: "worktree|staged|file.txt", generation: 3)
        let valid: [String: Any] = ["kind": "progress", "generation": 3, "identity": "worktree|staged|file.txt", "index": 1, "total": 2, "description": "Change 1", "mode": "side-by-side", "deletion": false]
        XCTAssertTrue(host.accepts(event: valid, origin: host.pageURL, isMainFrame: true))
        XCTAssertFalse(host.accepts(event: valid, origin: URL(string: "https://example.org")!, isMainFrame: true))
        XCTAssertFalse(host.accepts(event: valid, origin: host.pageURL, isMainFrame: false))
        XCTAssertFalse(host.accepts(event: valid.merging(["unexpected": "native access"]) { _, new in new }, origin: host.pageURL, isMainFrame: true))
        XCTAssertFalse(host.accepts(event: valid.merging(["generation": 2]) { _, new in new }, origin: host.pageURL, isMainFrame: true))
        XCTAssertFalse(host.accepts(event: valid.merging(["description": String(repeating: "a", count: 257)]) { _, new in new }, origin: host.pageURL, isMainFrame: true))
        host.select(identity: "another|file.txt", generation: 4)
        XCTAssertFalse(host.accepts(event: valid, origin: host.pageURL, isMainFrame: true))
        host.close()
        XCTAssertFalse(host.accepts(event: valid, origin: host.pageURL, isMainFrame: true))
    }

    func testClosingDuringLoadCannotPublishReadyOrAcceptMessages() async throws {
        let host = MacDiffViewerHost()
        let operation = Task { try await host.load() }
        host.close()
        do {
            try await operation.value
            XCTFail("Closed host must not finish initialization")
        } catch { /* expected closed/cancelled */ }
        XCTAssertFalse(host.isReady)
        XCTAssertFalse(host.accepts(event: ["kind": "ready"], origin: host.pageURL, isMainFrame: true))
    }

    func testF6LeavesViewerThroughNativeFocusOwner() throws {
        let host = MacDiffViewerHost()
        defer { host.close() }
        let view = try XCTUnwrap(host.webView as? MacReviewWebView)
        var exited = false
        host.onFocusExit = { exited = true }
        let f6 = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                                                 timestamp: 0, windowNumber: 0, context: nil,
                                                 characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 97))
        XCTAssertTrue(view.performKeyEquivalent(with: f6))
        XCTAssertTrue(exited)
    }

    func testBundledViewerLoadsInNativeWKWebView() async throws {
        let host = MacDiffViewerHost()
        defer { host.close() }
        try await host.load()
        XCTAssertTrue(host.isReady, "The actual packaged Monaco page must report ready")
        XCTAssertEqual(host.webView.url?.scheme, "gabcode-viewer")
        XCTAssertFalse(host.allowsNavigation(to: URL(string: "https://example.org")!, isMainFrame: true))
        XCTAssertFalse(host.allowsNavigation(to: URL(fileURLWithPath: "/etc/passwd"), isMainFrame: true))
        XCTAssertTrue(host.allowsNavigation(to: host.pageURL, isMainFrame: true))
        host.select(identity: "fixture|file", generation: 1)
        try await host.show(before: "old\n", after: "new\n", path: "fixture.swift", identity: "fixture|file", generation: 1)
        let probe = try await host.webView.evaluateJavaScript("JSON.stringify(window.gabCodeViewer.models())") as? String
        XCTAssertTrue(probe?.contains("new\\n") == true, "Monaco must receive the actual immutable pair")
        let deadline = ContinuousClock.now.advanced(by: .seconds(8))
        var computed = false
        var lastState = ""
        while !computed && ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(100))
            let state = try await host.webView.evaluateJavaScript("document.querySelector('.viewer-status').textContent") as? String ?? ""
            lastState = state
            computed = state.contains("Change 1 of")
        }
        XCTAssertTrue(computed, "Monaco's bundled worker must compute the change map, not only create models: \(lastState)")
    }
}
