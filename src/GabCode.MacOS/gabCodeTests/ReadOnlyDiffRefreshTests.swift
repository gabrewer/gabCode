import AppKit
import Combine
import SwiftUI
@testable import gabCode
import XCTest

@MainActor
final class ReadOnlyDiffRefreshTests: XCTestCase {
    func testBackgroundRefreshRetainsNativeTextHostFocusSelectionAndScroll() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let nextText = (0..<180).map { "changed line \($0)\n" }.joined()
        try Data(nextText.utf8).write(to: root.appendingPathComponent("history.txt"))
        let service = WorktreeChangeReviewService()
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        let file = try XCTUnwrap(snapshot.working.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1_000, height: 720), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: TerminalWorkspaceView(presentation: presentation, isActive: true)
            .environmentObject(TerminalFontPreferenceStore()))
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        presentation.openReviewDocument(file)
        try await waitUntil { !presentation.reviewDocument.isLoading && self.diffText(in: window.contentView!) != nil }
        let original = try XCTUnwrap(diffText(in: window.contentView!))
        let selection = NSRange(location: 25, length: 10)
        original.setSelectedRange(selection)
        original.scrollRangeToVisible(NSRange(location: original.string.utf16.count - 80, length: 10))
        let scroll = try XCTUnwrap(original.enclosingScrollView)
        let scrollOrigin = scroll.contentView.bounds.origin
        XCTAssertTrue(window.makeFirstResponder(original))
        presentation.reviewDocument.didFocus()
        presentation.reviewDocument.reload()
        window.contentView?.layoutSubtreeIfNeeded()
        XCTAssertTrue(presentation.reviewDocument.isLoading)
        XCTAssertTrue(diffText(in: window.contentView!) === original, "Do not replace a visible diff with a loading spinner")
        try await waitUntil { !presentation.reviewDocument.isLoading && self.diffText(in: window.contentView!) != nil }
        XCTAssertTrue(diffText(in: window.contentView!) === original)
        XCTAssertEqual(original.selectedRange(), selection)
        XCTAssertTrue(window.firstResponder === original)
        XCTAssertEqual(scroll.contentView.bounds.origin, scrollOrigin)
    }

    func testChangedPatchUpdatesInPlaceAndClampsSelectionWithoutStealingFocus() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("history.txt")
        try Data(String(repeating: "first version\n", count: 80).utf8).write(to: path)
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        let file = try XCTUnwrap(snapshot.working.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1_000, height: 720), styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: TerminalWorkspaceView(presentation: presentation, isActive: true)
            .environmentObject(TerminalFontPreferenceStore()))
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        presentation.openReviewDocument(file)
        try await waitUntil { !presentation.reviewDocument.isLoading && self.diffText(in: window.contentView!) != nil }
        let original = try XCTUnwrap(diffText(in: window.contentView!))
        original.setSelectedRange(NSRange(location: original.string.utf16.count - 10, length: 5))
        XCTAssertTrue(window.makeFirstResponder(original))
        presentation.reviewDocument.didFocus()
        try Data("shorter\n".utf8).write(to: path)
        presentation.reviewDocument.reload()
        try await waitUntil { !presentation.reviewDocument.isLoading && self.diffText(in: window.contentView!)?.string.contains("+shorter") == true }
        XCTAssertTrue(diffText(in: window.contentView!) === original)
        let range = original.selectedRange()
        XCTAssertLessThanOrEqual(range.location + range.length, original.string.utf16.count)
        XCTAssertTrue(window.firstResponder === original)
    }

    func testEmptyLoadedPatchRetainsNativeHostDuringRefresh() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("temporary edit\n".utf8).write(to: root.appendingPathComponent("history.txt"))
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        let file = try XCTUnwrap(snapshot.working.first)
        try git(["restore", "--worktree", "history.txt"], in: root)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1000, height: 720), styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: TerminalWorkspaceView(presentation: presentation, isActive: true)
            .environmentObject(TerminalFontPreferenceStore()))
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        presentation.openReviewDocument(file)
        try await waitUntil { !presentation.reviewDocument.isLoading && self.diffText(in: window.contentView!) != nil }
        let original = try XCTUnwrap(diffText(in: window.contentView!))
        XCTAssertTrue(original.string.isEmpty)
        presentation.reviewDocument.reload()
        window.contentView?.layoutSubtreeIfNeeded()
        XCTAssertTrue(diffText(in: window.contentView!) === original, "Empty is valid loaded content, not an initial load")
        try await waitUntil { !presentation.reviewDocument.isLoading }
        XCTAssertTrue(diffText(in: window.contentView!) === original)
    }

    func testUnrelatedWorkingRefreshDoesNotReloadImmutableHistoricalDocument() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator()
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let commit = try XCTUnwrap(coordinator.snapshot?.commits.first)
        let loaded = expectation(description: "commit files")
        let token = coordinator.$commitFiles.sink { if $0[commit.sha] != nil { loaded.fulfill() } }
        coordinator.toggleCommit(commit.sha)
        await fulfillment(of: [loaded], timeout: 5)
        token.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[commit.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        presentation.reviewDocument.apply(ReviewDiff(state: .patch, text: "diff --git a/history.txt b/history.txt\n+initial\n"))
        let revision = presentation.reviewDocument.revision
        try Data("untracked\n".utf8).write(to: root.appendingPathComponent("new.txt"))
        await coordinator.refresh()
        XCTAssertEqual(presentation.reviewDocument.revision, revision, "Historical content is immutable across working-tree refresh")
        XCTAssertEqual(coordinator.selectedFile?.id, file.id)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
        presentation.reviewDocument.fail("Transient Git failure")
        let failedRevision = presentation.reviewDocument.revision
        await coordinator.refresh()
        XCTAssertGreaterThan(presentation.reviewDocument.revision, failedRevision, "Refresh must still retry an unavailable historical diff")
    }

    private func diffText(in view: NSView) -> NSTextView? {
        if let text = view as? NSTextView, text.accessibilityLabel() == "Read-only unified diff" { return text }
        return view.subviews.lazy.compactMap { self.diffText(in: $0) }.first
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while !condition() && ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(condition(), "Native diff failed to settle")
    }

    private func repository() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review refresh ünicode \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        do {
            try git(["init", "-b", "trunk"], in: root)
            try git(["-c", "user.name=Review", "-c", "user.email=review@example.invalid", "commit", "--allow-empty", "-m", "base"], in: root)
            try git(["switch", "-c", "feature"], in: root)
            try Data("initial\n".utf8).write(to: root.appendingPathComponent("history.txt"))
            try git(["add", "history.txt"], in: root)
            try git(["-c", "user.name=Review", "-c", "user.email=review@example.invalid", "commit", "-m", "history"], in: root)
            return root
        } catch {
            try? FileManager.default.removeItem(at: root)
            throw error
        }
    }

    private func git(_ arguments: [String], in root: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = arguments
        process.currentDirectoryURL = root
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw NSError(domain: "ReadOnlyDiffRefresh", code: Int(process.terminationStatus)) }
    }
}
