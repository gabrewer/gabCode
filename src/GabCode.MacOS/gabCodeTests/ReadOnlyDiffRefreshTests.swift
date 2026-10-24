import AppKit
import Combine
import SwiftUI
import WebKit
@testable import gabCode
import XCTest

@MainActor
final class ReadOnlyDiffRefreshTests: XCTestCase {
    func testNativeViewerRefreshKeepsSameHostAndCapturedPair() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review WK refresh \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        func git(_ args: [String]) throws {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
            process.arguments = args
            process.currentDirectoryURL = root
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            try process.run(); process.waitUntilExit()
            XCTAssertEqual(process.terminationStatus, 0)
        }
        try git(["init", "-b", "trunk"])
        try Data("committed\n".utf8).write(to: root.appendingPathComponent("file.swift"))
        try git(["add", "file.swift"])
        try git(["-c", "user.name=Review", "-c", "user.email=review@example.invalid", "commit", "-m", "base"])
        try Data("first staged\nsecond staged\nthird staged\n".utf8).write(to: root.appendingPathComponent("file.swift"))
        try git(["add", "file.swift"])
        try Data("unstaged\n".utf8).write(to: root.appendingPathComponent("file.swift"))
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        let file = try XCTUnwrap(snapshot.working.first { $0.group == .staged })
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        presentation.reviewMainBranch = "trunk"
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1000, height: 720), styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: TerminalWorkspaceView(presentation: presentation, isActive: true)
            .environmentObject(TerminalFontPreferenceStore()))
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil); window.contentView = nil }
        presentation.openReviewDocument(file)
        let deadline = ContinuousClock.now.advanced(by: .seconds(8))
        while (!presentation.reviewDocument.hasLoadedContent || viewer(in: window.contentView!) == nil) && ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        var view = try XCTUnwrap(viewer(in: window.contentView!))
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.before, "committed\n")
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "first staged\nsecond staged\nthird staged\n")
        var host = try XCTUnwrap(presentation.reviewDocument.viewerHost)
        _ = try await host.webView.evaluateJavaScript("window.gabCodeViewer.line(2)")
        try await host.focusCode()
        presentation.reviewDocument.reload()
        XCTAssertTrue(presentation.reviewDocument.isLoading)
        XCTAssertTrue(viewer(in: window.contentView!) === view, "Retain the hosted view while refreshing")
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "first staged\nsecond staged\nthird staged\n")
        let refreshDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < refreshDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertFalse(presentation.reviewDocument.isLoading)
        XCTAssertTrue(viewer(in: window.contentView!) === view)
        let retainedLine = try await host.webView.evaluateJavaScript("window.gabCodeViewer.line()") as? Int
        XCTAssertEqual(retainedLine, 2)
        let retainedFocus = try await host.webView.evaluateJavaScript("document.activeElement?.tagName") as? String
        XCTAssertEqual(retainedFocus, "TEXTAREA",
                       "Refresh must return keyboard focus to code when code had focus")
        try Data("shorter".utf8).write(to: root.appendingPathComponent("file.swift"))
        try git(["add", "file.swift"])
        presentation.reviewDocument.reload()
        let changedDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < changedDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "shorter")
        XCTAssertTrue(viewer(in: window.contentView!) === view)
        let clampedLine = try await host.webView.evaluateJavaScript("window.gabCodeViewer.line()") as? Int
        XCTAssertEqual(clampedLine, 1,
                       "Changed shorter result must retain a surviving code line")

        host.webViewWebContentProcessDidTerminate(host.webView)
        XCTAssertNil(presentation.reviewDocument.errorMessage, "The first white/terminated viewer should retry silently")
        XCTAssertFalse(presentation.reviewDocument.viewerHost === host, "Automatically retry in a new WK host")
        let recoveryDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while (presentation.reviewDocument.isLoading || presentation.reviewDocument.errorMessage != nil) && ContinuousClock.now < recoveryDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertNil(presentation.reviewDocument.errorMessage)
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "shorter")
        host = try XCTUnwrap(presentation.reviewDocument.viewerHost)
        XCTAssertTrue(host.isReady)
        view = try XCTUnwrap(viewer(in: window.contentView!))
        host.webViewWebContentProcessDidTerminate(host.webView)
        XCTAssertNotNil(presentation.reviewDocument.errorMessage, "The second failure should offer manual Retry")
        XCTAssertFalse(presentation.reviewDocument.isLoading)
        presentation.reviewDocument.retry()
        let manualDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < manualDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertNil(presentation.reviewDocument.errorMessage)
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "shorter")
        host = try XCTUnwrap(presentation.reviewDocument.viewerHost)
        view = try XCTUnwrap(viewer(in: window.contentView!))

        try Data("other selected file\n".utf8).write(to: root.appendingPathComponent("other.swift"))
        try git(["add", "other.swift"])
        let nextSnapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        let other = try XCTUnwrap(nextSnapshot.working.first { $0.path == "other.swift" })
        presentation.openReviewDocument(other)
        let otherDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < otherDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "other selected file\n",
                       "A failed WK process must not poison subsequent file selections")
        presentation.openReviewDocument(file)
        let backDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < backDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "shorter")
        host = try XCTUnwrap(presentation.reviewDocument.viewerHost)
        view = try XCTUnwrap(viewer(in: window.contentView!))

        try FileManager.default.removeItem(at: root.appendingPathComponent(".git"))
        presentation.reviewDocument.reload()
        let failureDeadline = ContinuousClock.now.advanced(by: .seconds(8))
        while presentation.reviewDocument.isLoading && ContinuousClock.now < failureDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertNotNil(presentation.reviewDocument.errorMessage)
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "shorter")
        XCTAssertTrue(viewer(in: window.contentView!) === view, "Failed refresh must leave last-good viewer visibly stale")
        presentation.reviewDocument.retry()
        XCTAssertTrue(presentation.reviewDocument.isLoading)
        XCTAssertFalse(presentation.reviewDocument.viewerHost === host, "Retry creates a fresh host after an error")
        let retryDeadline = ContinuousClock.now.advanced(by: .seconds(3))
        while viewer(in: window.contentView!) === view && ContinuousClock.now < retryDeadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertTrue(viewer(in: window.contentView!) === presentation.reviewDocument.viewerHost?.webView,
                      "Retry must reparent the fresh WKWebView, not retain the disposed native view")
    }

    func testUnrelatedWorkingRefreshDoesNotReloadImmutableHistoricalDocument() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review history \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = ["init", "-b", "trunk"]
        process.currentDirectoryURL = root
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        func git(_ args: [String]) throws {
            let command = Process()
            command.executableURL = URL(fileURLWithPath: "/usr/bin/git")
            command.arguments = args
            command.currentDirectoryURL = root
            command.standardOutput = FileHandle.nullDevice; command.standardError = FileHandle.nullDevice
            try command.run(); command.waitUntilExit()
            XCTAssertEqual(command.terminationStatus, 0)
        }
        try git(["-c", "user.name=Review", "-c", "user.email=review@example.invalid", "commit", "--allow-empty", "-m", "base"])
        try git(["switch", "-c", "feature"])
        try Data("initial\n".utf8).write(to: root.appendingPathComponent("history.txt"))
        try git(["add", "history.txt"])
        try git(["-c", "user.name=Review", "-c", "user.email=review@example.invalid", "commit", "-m", "history"])
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
        let pair = try await WorktreeChangeReviewService().document(for: file, in: root, mainBranch: "trunk")
        presentation.reviewDocument.apply(pair)
        let revision = presentation.reviewDocument.revision
        try Data("untracked\n".utf8).write(to: root.appendingPathComponent("new.txt"))
        await coordinator.refresh()
        XCTAssertEqual(presentation.reviewDocument.revision, revision, "Unrelated working changes do not reload immutable history")
        XCTAssertEqual(coordinator.selectedFile?.id, file.id)
        presentation.reviewDocument.fail("Transient Git failure")
        let failedRevision = presentation.reviewDocument.revision
        await coordinator.refresh()
        XCTAssertGreaterThan(presentation.reviewDocument.revision, failedRevision, "Reconciliation retries failed history")
        XCTAssertEqual(presentation.reviewDocument.reviewPair?.after, "initial\n")
    }

    private func viewer(in view: NSView) -> MacReviewWebView? {
        if let web = view as? MacReviewWebView { return web }
        return view.subviews.lazy.compactMap { self.viewer(in: $0) }.first
    }
}
