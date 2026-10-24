import AppKit
import SwiftUI
@testable import gabCode
import XCTest

@MainActor
final class ReadOnlyDiffPresentationTests: XCTestCase {
    func testOpenReplaceSwitchAndCloseRetainExactMainTerminalIdentity() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("review retained \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let presentation = TerminalWorkspacePresentation(workingDirectory: directory)
        await presentation.start()
        defer { Task { await presentation.workspace.stop(gracePeriod: .milliseconds(250)) } }
        let one = presentation.session(for: .terminal1)
        let two = presentation.session(for: .terminal2)
        let pid1 = try XCTUnwrap(one.processIdentifier), pid2 = try XCTUnwrap(two.processIdentifier)
        let group1 = try XCTUnwrap(one.processGroupIdentifier), group2 = try XCTUnwrap(two.processGroupIdentifier)
        let view1 = one.terminalView, view2 = two.terminalView
        presentation.swapTerminals()
        XCTAssertEqual(presentation.mainTerminal, .terminal2)
        let document = presentation.reviewDocument
        let first = ReviewFile(worktree: directory, group: .staged, status: .modified, path: "a.txt", oldPath: nil, commitSHA: nil, comparison: "HEAD → index")
        let second = ReviewFile(worktree: directory, group: .unstaged, status: .modified, path: "b.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        presentation.openReviewDocument(first)
        XCTAssertEqual(document.restoreTerminal, .terminal2)
        XCTAssertEqual(document.selectedBottomTerminal, .terminal2)
        XCTAssertTrue(document.wantsFocus)
        presentation.openReviewDocument(second)
        presentation.focus(.terminal1)
        XCTAssertEqual(document.selectedBottomTerminal, .terminal1, "Keyboard focus reveals its bottom tab")
        XCTAssertFalse(document.wantsFocus, "Switching to terminal during load must not steal focus back")
        XCTAssertEqual(document.restoreTerminal, .terminal2)
        presentation.closeReviewDocument()
        XCTAssertNil(document.selectedFile)
        XCTAssertEqual(presentation.mainTerminal, .terminal2)
        XCTAssertEqual(one.processIdentifier, pid1)
        XCTAssertEqual(two.processIdentifier, pid2)
        XCTAssertEqual(one.processGroupIdentifier, group1)
        XCTAssertEqual(two.processGroupIdentifier, group2)
        XCTAssertTrue(one.terminalView === view1)
        XCTAssertTrue(two.terminalView === view2)
    }

    func testNativeHostReparentsExistingTerminalViewsWhileDocumentOccupiesMain() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("review native host \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let presentation = TerminalWorkspacePresentation(workingDirectory: directory)
        await presentation.start()
        defer { Task { await presentation.workspace.stop(gracePeriod: .milliseconds(250)) } }
        let session1 = presentation.session(for: .terminal1)
        let session2 = presentation.session(for: .terminal2)
        let first = session1.terminalView
        let second = session2.terminalView
        let pid1 = session1.processIdentifier, pid2 = session2.processIdentifier
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1_000, height: 720), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: TerminalWorkspaceView(presentation: presentation, isActive: true)
            .environmentObject(TerminalFontPreferenceStore()))
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        func awaitHost(_ view: NSView) async throws {
            let deadline = ContinuousClock.now.advanced(by: .seconds(3))
            while view.superview == nil && ContinuousClock.now < deadline {
                window.contentView?.layoutSubtreeIfNeeded()
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertNotNil(view.superview)
        }
        try await awaitHost(first)
        try await awaitHost(second)
        let firstHost = first.superview
        let secondHost = second.superview
        let file = ReviewFile(worktree: directory, group: .unstaged, status: .conflicted, path: "conflicted.txt", oldPath: nil, commitSHA: nil, comparison: "unmerged stages")
        presentation.openReviewDocument(file)
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while second.superview === secondHost && ContinuousClock.now < deadline {
            window.contentView?.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(first === session1.terminalView)
        XCTAssertTrue(second === session2.terminalView)
        XCTAssertEqual(session1.processIdentifier, pid1)
        XCTAssertEqual(session2.processIdentifier, pid2)
        XCTAssertTrue(first.superview !== firstHost || second.superview !== secondHost,
                      "Opening a document must move existing terminal views into bottom tabs")
        let tabHost = first.superview
        let replacement = ReviewFile(worktree: directory, group: .unstaged, status: .conflicted, path: "replacement.txt", oldPath: nil, commitSHA: nil, comparison: "unmerged stages")
        presentation.openReviewDocument(replacement)
        window.contentView?.layoutSubtreeIfNeeded()
        XCTAssertTrue(first.superview === tabHost, "Replacing a document must not reparent terminal views again")
        presentation.reviewDocument.selectBottom(.terminal2)
        let switchDeadline = ContinuousClock.now.advanced(by: .seconds(3))
        while second.superview === secondHost && ContinuousClock.now < switchDeadline {
            window.contentView?.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(second === session2.terminalView)
        presentation.closeReviewDocument()
        XCTAssertEqual(session1.processIdentifier, pid1)
        XCTAssertEqual(session2.processIdentifier, pid2)
    }

    func testCurrentFileActionRejectsDeletedPathsAndSymlinkEscapes() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review external \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let current = root.appendingPathComponent("current.txt")
        try "current".write(to: current, atomically: true, encoding: .utf8)
        let deleted = ReviewFile(worktree: root, group: .commit, status: .deleted, path: "deleted.txt", oldPath: nil, commitSHA: String(repeating: "a", count: 40), comparison: "parent → commit")
        let currentHistory = ReviewFile(worktree: root, group: .commit, status: .modified, path: "current.txt", oldPath: nil, commitSHA: String(repeating: "b", count: 40), comparison: "parent → commit")
        XCTAssertNil(ReviewCurrentFileLocator.existingFile(for: deleted, selectedWorktree: root))
        XCTAssertEqual(ReviewCurrentFileLocator.existingFile(for: currentHistory, selectedWorktree: root), current)
        let outside = FileManager.default.temporaryDirectory.appendingPathComponent("review outside \(UUID()).txt")
        try "outside".write(to: outside, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: outside) }
        let link = root.appendingPathComponent("escape.txt")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        let escaped = ReviewFile(worktree: root, group: .unstaged, status: .untracked, path: "escape.txt", oldPath: nil, commitSHA: nil, comparison: "empty → worktree")
        XCTAssertNil(ReviewCurrentFileLocator.existingFile(for: escaped, selectedWorktree: root))
    }

    func testReplacingPendingLoadDoesNotReuseHostOwnedByCancelledTask() async throws {
        let root = URL(fileURLWithPath: "/tmp/review WK pending")
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "pending.swift", oldPath: nil,
                              commitSHA: nil, comparison: "index → worktree")
        let document = ReadOnlyDiffDocumentState()
        document.open(file, mainTerminal: .terminal1)
        let old = try XCTUnwrap(document.viewerHost)
        document.open(file, mainTerminal: .terminal1)
        let replacement = try XCTUnwrap(document.viewerHost)
        XCTAssertFalse(old === replacement, "The canceled load owns its old WKWebView, not the next selection")
        old.close()
        try await replacement.load()
        XCTAssertTrue(replacement.isReady)
        document.reload()
        XCTAssertTrue(document.viewerHost === replacement, "Completed viewer should retain position on refresh")
        document.close()
    }

    func testFailedWKHostCanReopenSameFileWithoutSwitchingWorktrees() async throws {
        let root = URL(fileURLWithPath: "/tmp/review WK recovery")
        let file = ReviewFile(worktree: root, group: .staged, status: .modified, path: "first.swift", oldPath: nil,
                              commitSHA: nil, comparison: "HEAD → index")
        let document = ReadOnlyDiffDocumentState()
        document.open(file, mainTerminal: .terminal1)
        let failedHost = try XCTUnwrap(document.viewerHost)
        failedHost.close() // Mirrors load failure or terminated WebKit content process.
        document.fail("The native viewer stopped unexpectedly")
        document.open(file, mainTerminal: .terminal1)
        let recovered = try XCTUnwrap(document.viewerHost)
        XCTAssertFalse(recovered === failedHost, "Opening the same file must not reuse a closed WKWebView")
        try await recovered.load()
        XCTAssertTrue(recovered.isReady)
        document.fail("Change computation timed out")
        document.open(file, mainTerminal: .terminal1)
        XCTAssertFalse(document.viewerHost === recovered,
                       "Clicking a failed file must recreate even a ready WK host with a broken worker")
        document.close()
    }

    func testDocumentStateRetainsExactPairAndStaleSnapshotForRetry() throws {
        let root = URL(fileURLWithPath: "/tmp/review state")
        let file = ReviewFile(worktree: root, group: .staged, status: .modified, path: "file.swift", oldPath: nil,
                              commitSHA: nil, comparison: "HEAD → index")
        let document = ReadOnlyDiffDocumentState()
        document.open(file, mainTerminal: .terminal1)
        let facts = ReviewTextFacts(bom: false, eol: "LF", finalNewline: true)
        let pair = ReviewDocument(before: "committed\n", after: "staged\n", beforeFacts: facts, afterFacts: facts,
                                  beforeExists: true, afterExists: true, identity: "selected", comparison: "HEAD → index", rawFilter: false)
        document.apply(pair)
        XCTAssertEqual(document.reviewPair?.before, "committed\n")
        XCTAssertEqual(document.reviewPair?.after, "staged\n")
        document.reload()
        XCTAssertEqual(document.reviewPair?.identity, "selected", "Refresh must keep last-good pair while visibly stale")
        XCTAssertTrue(document.isLoading)
        document.fail("Git unavailable")
        XCTAssertEqual(document.reviewPair?.identity, "selected")
        XCTAssertNotNil(document.errorMessage)
        document.close()
        XCTAssertNil(document.reviewPair)
    }

    func testReviewModeIsLocalValidatedPresentationPreference() throws {
        let name = "gabcode.review.mode.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let preference = ReviewModePreference(defaults: defaults)
        XCTAssertEqual(preference.mode, "side-by-side")
        preference.mode = "file"
        XCTAssertEqual(ReviewModePreference(defaults: defaults).mode, "file")
        preference.mode = "editable"
        XCTAssertEqual(preference.mode, "file", "Unknown or editable modes must not persist")
        defaults.set("malformed", forKey: ReviewModePreference.key)
        XCTAssertEqual(preference.mode, "side-by-side")
    }

    func testReadOnlyDiffViewIsNotEditableAndHasNativeAccessibleDescription() throws {
        let view = ReadOnlyDiffTextView.make(text: "diff --git a/file b/file\n+added\n")
        XCTAssertFalse(view.isEditable)
        XCTAssertFalse(view.isRichText)
        XCTAssertEqual(view.string, "diff --git a/file b/file\n+added\n")
        XCTAssertEqual(view.accessibilityRole(), .textArea)
        XCTAssertNotNil(view.accessibilityLabel())
    }
}
