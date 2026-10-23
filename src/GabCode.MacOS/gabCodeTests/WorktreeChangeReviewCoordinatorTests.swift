import AppKit
import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorktreeChangeReviewCoordinatorTests: XCTestCase {
    func testBranchListingRejectsObsoleteHeadAndKeepsComparisonIdentity() async throws {
        let root = URL(fileURLWithPath: "/tmp/review branch coordinator \(UUID())", isDirectory: true)
        let oldHead = String(repeating: "a", count: 40)
        let main = String(repeating: "b", count: 40)
        let newHead = String(repeating: "c", count: 40)
        let loader = SnapshotSequence([snapshot(root, head: oldHead, main: main, working: [], commits: []), snapshot(root, head: newHead, main: main, working: [], commits: [])])
        let coordinator = WorktreeChangeReviewCoordinator(
            snapshotLoader: { _, _ in try await loader.next() },
            branchFileLoader: { _, _ in
                try await Task.sleep(for: .milliseconds(150))
                return [ReviewFile(worktree: root, group: .branch, status: .added, path: "change.txt", oldPath: nil,
                                   commitSHA: oldHead, comparison: "\(main) → \(oldHead)")]
            })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        coordinator.loadBranchFiles()
        await coordinator.refresh()
        try await Task.sleep(for: .milliseconds(250))
        XCTAssertTrue(coordinator.branchFiles.isEmpty, "A result captured for obsolete HEAD cannot appear")
        XCTAssertNil(coordinator.branchFiles.first?.commitSHA)
    }

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

    func testCopyCommitHashPreservesDiffAndRejectsStaleCommitAfterWorktreeSwitch() async throws {
        let root = URL(fileURLWithPath: "/tmp/review copy \(UUID())", isDirectory: true)
        let sha = String(repeating: "a", count: 40)
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "file.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let commit = ReviewCommit(sha: sha, subject: "feature", author: "Tester", timestamp: .now, summary: "1 file changed", parents: [])
        let loader = SnapshotSequence([snapshot(root, working: [file], commits: [commit])])
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { _, _ in try await loader.next() })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        defer { coordinator.deactivate() }
        await coordinator.refresh()
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        let revision = presentation.reviewDocument.revision
        coordinator.copyCommitHash(sha)
        XCTAssertEqual(NSPasteboard.general.string(forType: .string), sha)
        XCTAssertEqual(coordinator.selectedFile, file)
        XCTAssertEqual(presentation.reviewDocument.revision, revision)
        XCTAssertEqual(coordinator.notice, "Commit hash copied to clipboard.")
        coordinator.activate(worktree: root.appendingPathComponent("another"), mainBranch: "trunk")
        let changeCount = NSPasteboard.general.changeCount
        coordinator.copyCommitHash(sha)
        XCTAssertEqual(NSPasteboard.general.changeCount, changeCount, "A stale menu action must not touch the clipboard")
        XCTAssertNil(coordinator.notice)
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

    func testScopeSwitchDuringCoalescingCooldownReleasesCancelledSlot() async throws {
        let first = URL(fileURLWithPath: "/tmp/review cooldown old \(UUID())", isDirectory: true)
        let second = URL(fileURLWithPath: "/tmp/review cooldown new \(UUID())", isDirectory: true)
        let gate = SwitchSnapshotGate()
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { path, _ in
            await gate.load(self.snapshot(path, working: [], commits: []))
        })
        coordinator.activate(worktree: first, mainBranch: "trunk")
        coordinator.requestRefresh()
        await gate.waitForStart()
        coordinator.requestRefresh() // first completion enters its one-second cooldown
        await gate.release()
        let firstDeadline = ContinuousClock.now.advanced(by: .seconds(2))
        while coordinator.snapshot?.worktree != first && ContinuousClock.now < firstDeadline { await Task.yield() }
        XCTAssertEqual(coordinator.snapshot?.worktree, first)
        coordinator.activate(worktree: second, mainBranch: "trunk")
        coordinator.requestRefresh()
        let secondDeadline = ContinuousClock.now.advanced(by: .seconds(2))
        while coordinator.snapshot?.worktree != second && ContinuousClock.now < secondDeadline { await Task.yield() }
        XCTAssertEqual(coordinator.snapshot?.worktree, second, "Cancelling pacing sleep must not strand old refresh slot")
        coordinator.deactivate()
    }

    func testScopeSwitchWaitsForCancelledSnapshotCleanupBeforeLaunchingReplacement() async throws {
        let first = URL(fileURLWithPath: "/tmp/review old scope \(UUID())", isDirectory: true)
        let second = URL(fileURLWithPath: "/tmp/review new scope \(UUID())", isDirectory: true)
        let gate = SwitchSnapshotGate()
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { path, _ in
            await gate.load(self.snapshot(path, working: [], commits: []))
        })
        coordinator.activate(worktree: first, mainBranch: "trunk")
        coordinator.requestRefresh()
        await gate.waitForStart()
        coordinator.activate(worktree: second, mainBranch: "trunk")
        coordinator.requestRefresh()
        for _ in 0..<200 { await Task.yield() }
        let before = await gate.calls
        XCTAssertEqual(before, 1, "Switch cannot overlap old Git cleanup")
        await gate.release()
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while coordinator.snapshot?.worktree != second && ContinuousClock.now < deadline { await Task.yield() }
        let peak = await gate.peak
        XCTAssertEqual(peak, 1)
        XCTAssertEqual(coordinator.snapshot?.worktree, second)
        coordinator.deactivate()
    }

    func testFailedAutomaticRefreshBacksOffWhileExplicitRetryRuns() async throws {
        let root = URL(fileURLWithPath: "/tmp/review retry \(UUID())", isDirectory: true)
        let expected = snapshot(root, working: [], commits: [])
        let loader = RetrySnapshotLoader(expected: expected)
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { _, _ in try await loader.load() })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        defer { coordinator.deactivate() }
        coordinator.requestRefresh()
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while !coordinator.isStale && ContinuousClock.now < deadline { await Task.yield() }
        XCTAssertTrue(coordinator.isStale)
        for _ in 0..<100 { coordinator.requestRefresh() }
        let before = await loader.calls
        XCTAssertEqual(before, 1, "Watcher storm after failure must observe retry cooldown")
        coordinator.requestRefresh(explicit: true)
        let retryDeadline = ContinuousClock.now.advanced(by: .seconds(2))
        while coordinator.snapshot == nil && ContinuousClock.now < retryDeadline { await Task.yield() }
        let after = await loader.count()
        XCTAssertEqual(after, 2)
        XCTAssertFalse(coordinator.isStale)
        XCTAssertNotNil(coordinator.snapshot)
    }

    func testRefreshHintsDuringAnActiveRequestCoalesceIntoOneFollowUp() async throws {
        let root = URL(fileURLWithPath: "/tmp/review burst \(UUID())", isDirectory: true)
        let expected = snapshot(root, working: [], commits: [])
        let gate = BurstSnapshotGate()
        let coordinator = WorktreeChangeReviewCoordinator(snapshotLoader: { _, _ in await gate.next(expected) })
        coordinator.activate(worktree: root, mainBranch: "trunk")
        defer { coordinator.deactivate() }
        coordinator.requestRefresh()
        await gate.waitForStart()
        for _ in 0..<100 { coordinator.requestRefresh() }
        let before = await gate.calls
        XCTAssertEqual(before, 1, "Only one in-flight snapshot may own the selected scope")
        await gate.release()
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while await gate.calls < 2 && ContinuousClock.now < deadline { await Task.yield() }
        let count = await gate.calls
        let peak = await gate.peak
        XCTAssertEqual(count, 2, "Burst hints schedule exactly one fresh snapshot")
        XCTAssertEqual(peak, 1, "A replacement cannot overlap still-running cleanup")
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

    nonisolated private func snapshot(_ root: URL, head: String = String(repeating: "1", count: 40), main: String = String(repeating: "0", count: 40), working: [ReviewFile], commits: [ReviewCommit]) -> ReviewSnapshot {
        ReviewSnapshot(worktree: root, head: head, main: main, working: working, commits: commits, hasMoreCommits: false)
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

    private actor SwitchSnapshotGate {
        var calls = 0
        var active = 0
        var peak = 0
        private var waiting: CheckedContinuation<Void, Never>?
        private var starters: [CheckedContinuation<Void, Never>] = []
        func load(_ snapshot: ReviewSnapshot) async -> ReviewSnapshot {
            calls += 1; active += 1; peak = max(peak, active)
            if calls == 1 {
                starters.forEach { $0.resume() }; starters.removeAll()
                await withCheckedContinuation { waiting = $0 }
            }
            active -= 1
            return snapshot
        }
        func waitForStart() async {
            if waiting != nil { return }
            await withCheckedContinuation { starters.append($0) }
        }
        func release() { waiting?.resume(); waiting = nil }
    }

    private actor RetrySnapshotLoader {
        let expected: ReviewSnapshot
        var calls = 0
        init(expected: ReviewSnapshot) { self.expected = expected }
        func load() throws -> ReviewSnapshot {
            calls += 1
            if calls == 1 { throw ReviewGitError.timeout }
            return expected
        }
        func count() -> Int { calls }
    }

    private actor BurstSnapshotGate {
        var calls = 0
        var active = 0
        var peak = 0
        private var held: CheckedContinuation<Void, Never>?
        private var starters: [CheckedContinuation<Void, Never>] = []
        func next(_ snapshot: ReviewSnapshot) async -> ReviewSnapshot {
            calls += 1; active += 1; peak = max(peak, active)
            if calls == 1 {
                starters.forEach { $0.resume() }; starters.removeAll()
                await withCheckedContinuation { held = $0 }
            }
            active -= 1
            return snapshot
        }
        func waitForStart() async {
            if held != nil { return }
            await withCheckedContinuation { starters.append($0) }
        }
        func release() { held?.resume(); held = nil }
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
