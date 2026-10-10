import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorktreeChangeReviewCoordinatorTests: XCTestCase {
    func testRefreshRetainsReviewContextAndClosesInvalidatedDiff() async throws {
        let root = URL(fileURLWithPath: "/tmp/review coordinator \(UUID())", isDirectory: true)
        let sha = String(repeating: "a", count: 40)
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "file.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let commit = ReviewCommit(sha: sha, subject: "feature", author: "Tester", timestamp: .now, summary: "1 file changed", parents: [])
        let loader = SnapshotSequence([snapshot(root, working: [file], commits: [commit]), snapshot(root, working: [file], commits: [commit]), snapshot(root, working: [], commits: [])])
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { _, _ in try await loader.next() })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        XCTAssertEqual(coordinator.snapshot?.working, [file])
        coordinator.toggleCommit(sha)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
        let revision = presentation.reviewDocument.revision
        await coordinator.refresh()
        XCTAssertGreaterThan(presentation.reviewDocument.revision, revision, "Retained file refreshes the diff without losing selection")
        XCTAssertFalse(presentation.reviewDocument.wantsFocus, "Background refresh must not steal terminal focus")
        XCTAssertEqual(coordinator.expandedCommits, [sha])
        XCTAssertEqual(presentation.reviewDocument.selectedFile?.id, file.id)
        await coordinator.refresh()
        XCTAssertTrue(coordinator.expandedCommits.isEmpty)
        XCTAssertNil(coordinator.selectedFile)
        XCTAssertFalse(presentation.reviewDocument.isOpen)
        XCTAssertNotNil(coordinator.notice)
    }

    func testFailureKeepsLastGoodDataMarkedStaleAndManualRefreshRecovers() async throws {
        let root = URL(fileURLWithPath: "/tmp/review stale \(UUID())", isDirectory: true)
        let good = snapshot(root, working: [], commits: [])
        let loader = SnapshotSequence([good, nil, good])
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { _, _ in try await loader.next() })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        await coordinator.refresh()
        XCTAssertNotNil(coordinator.snapshot)
        XCTAssertTrue(coordinator.isStale)
        XCTAssertNotNil(coordinator.errorMessage)
        await coordinator.refresh()
        XCTAssertFalse(coordinator.isStale)
        XCTAssertNil(coordinator.errorMessage)
    }

    func testRecursiveMacOSFSEventsInvalidatesOnNestedFileChange() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review observer \(UUID())", isDirectory: true)
        let nested = root.appendingPathComponent("nested/deeper", isDirectory: true)
        try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let event = expectation(description: "nested FSEvents invalidation")
        event.assertForOverFulfill = false
        let observer = WorktreeReviewObserver(worktree: root) { _ in event.fulfill() }
        XCTAssertTrue(observer.start())
        defer { observer.stop() }
        try Data("changed".utf8).write(to: nested.appendingPathComponent("file.txt"))
        await fulfillment(of: [event], timeout: 5)
    }

    func testLateResultCannotCrossSelectedWorktreesOrRefreshGenerations() async throws {
        let first = URL(fileURLWithPath: "/tmp/review old \(UUID())", isDirectory: true)
        let second = URL(fileURLWithPath: "/tmp/review new \(UUID())", isDirectory: true)
        let gate = SnapshotGate()
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { path, _ in
            if path == first { return try await gate.wait() }
            return self.snapshot(second, working: [], commits: [])
        })
        coordinator.activate(worktree: first, mainBranch: "trunk")
        let old = Task { await coordinator.refresh() }
        await gate.waitForStart()
        coordinator.activate(worktree: second, mainBranch: "trunk")
        await coordinator.refresh()
        await gate.release(snapshot(first, working: [], commits: []))
        await old.value
        XCTAssertEqual(coordinator.snapshot?.worktree, second)
        XCTAssertEqual(coordinator.selectedWorktree, second)
    }

    nonisolated private func snapshot(_ root: URL, working: [ReviewFile], commits: [ReviewCommit]) -> ReviewSnapshot {
        ReviewSnapshot(worktree: root, head: String(repeating: "1", count: 40), main: String(repeating: "0", count: 40), working: working, commits: commits, hasMoreCommits: false)
    }

    private actor SnapshotSequence {
        var results: [ReviewSnapshot?]
        init(_ results: [ReviewSnapshot?]) { self.results = results }
        func next() throws -> ReviewSnapshot {
            guard !results.isEmpty else { throw ReviewGitError.failed("No test result") }
            guard let next = results.removeFirst() else { throw ReviewGitError.failed("Injected Git failure") }
            return next
        }
    }

    private actor SnapshotGate {
        private var continuation: CheckedContinuation<ReviewSnapshot, Error>?
        private var startWaiters: [CheckedContinuation<Void, Never>] = []
        func wait() async throws -> ReviewSnapshot {
            try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                let waiters = startWaiters
                startWaiters.removeAll()
                waiters.forEach { $0.resume() }
            }
        }
        func waitForStart() async {
            if continuation != nil { return }
            await withCheckedContinuation { startWaiters.append($0) }
        }
        func release(_ snapshot: ReviewSnapshot) { continuation?.resume(returning: snapshot); continuation = nil }
    }
}
