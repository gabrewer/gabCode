import Combine
import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorktreeChangeReviewAdversarialTests: XCTestCase {
    func testUnrelatedRefreshPreservesLoadedHistoryAndOpenOlderDiff() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let page = expectation(description: "second history page")
        let pageSubscription = coordinator.$snapshot.sink { snapshot in
            if snapshot?.commits.count == 2 { page.fulfill() }
        }
        coordinator.loadMore()
        await fulfillment(of: [page], timeout: 5)
        pageSubscription.cancel()
        let loaded = try XCTUnwrap(coordinator.snapshot).commits
        XCTAssertEqual(loaded.count, 2)
        let older = try XCTUnwrap(loaded.last)
        let filesLoaded = expectation(description: "older commit files")
        let filesSubscription = coordinator.$commitFiles.sink { files in
            if files[older.sha] != nil { filesLoaded.fulfill() }
        }
        coordinator.toggleCommit(older.sha)
        await fulfillment(of: [filesLoaded], timeout: 5)
        filesSubscription.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[older.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
        await coordinator.refresh() // Same HEAD/main, like focus or periodic reconciliation.
        XCTAssertEqual(coordinator.snapshot?.commits.map(\.sha), loaded.map(\.sha), "Unchanged refs must not discard loaded pages")
        XCTAssertTrue(coordinator.expandedCommits.contains(older.sha), "Keep valid expansion")
        XCTAssertEqual(coordinator.selectedFile?.id, file.id, "Keep valid older-file selection")
        XCTAssertTrue(presentation.reviewDocument.isOpen, "Periodic refresh must not close a valid diff")
    }

    func testSwitchingWorktreesDuringPagingDoesNotPermanentlyDisableLoadMore() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let second = root.appendingPathComponent("second ünicode", isDirectory: true)
        try git(["worktree", "add", "--detach", second.path, "HEAD"], in: root)
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        coordinator.loadMore()
        XCTAssertTrue(coordinator.isLoadingMore)
        // Switch synchronously before the queued page operation can finish.
        coordinator.activate(worktree: second, mainBranch: "trunk")
        await coordinator.refresh()
        XCTAssertFalse(coordinator.isLoadingMore, "Cancelled page must release the loading latch in the new worktree")
        let page = expectation(description: "new worktree can page")
        let subscription = coordinator.$snapshot.sink { snapshot in
            if snapshot?.worktree == second && snapshot?.commits.count == 2 { page.fulfill() }
        }
        coordinator.loadMore()
        await fulfillment(of: [page], timeout: 5)
        subscription.cancel()
    }

    func testRefreshDuringQueuedPageReleasesLatchAndAllowsRetry() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        coordinator.loadMore()
        XCTAssertTrue(coordinator.isLoadingMore)
        await coordinator.refresh()
        XCTAssertFalse(coordinator.isLoadingMore)
        let page = expectation(description: "retry page after refresh")
        let subscription = coordinator.$snapshot.sink { snapshot in
            if snapshot?.commits.count == 2 { page.fulfill() }
        }
        coordinator.loadMore()
        await fulfillment(of: [page], timeout: 5)
        subscription.cancel()
        XCTAssertEqual(coordinator.snapshot?.commits.count, 2)
    }

    func testMainAdvancingClosesCommitNoLongerInUniqueRange() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let commit = try XCTUnwrap(coordinator.snapshot?.commits.first)
        let loaded = expectation(description: "commit file loaded")
        let subscription = coordinator.$commitFiles.sink { files in
            if files[commit.sha] != nil { loaded.fulfill() }
        }
        coordinator.toggleCommit(commit.sha)
        await fulfillment(of: [loaded], timeout: 5)
        subscription.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[commit.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
        try git(["update-ref", "refs/heads/trunk", "HEAD"], in: root)
        await coordinator.refresh()
        XCTAssertEqual(coordinator.snapshot?.commits.count, 0)
        XCTAssertTrue(coordinator.expandedCommits.isEmpty)
        XCTAssertNil(coordinator.selectedFile)
        XCTAssertFalse(presentation.reviewDocument.isOpen)
        XCTAssertNotNil(coordinator.notice)
    }

    func testPendingDebounceCannotRepopulateDeactivatedCoordinator() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator()
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        coordinator.invalidate(overflow: true)
        coordinator.deactivate()
        let resurrected = expectation(description: "no late snapshot after deactivation")
        resurrected.isInverted = true
        let subscription = coordinator.$snapshot.sink { snapshot in
            if snapshot != nil { resurrected.fulfill() }
        }
        await fulfillment(of: [resurrected], timeout: 0.6)
        subscription.cancel()
        XCTAssertNil(coordinator.selectedWorktree)
        XCTAssertFalse(coordinator.isLoading)
        XCTAssertFalse(coordinator.isLoadingMore)
    }

    func testOverflowBurstReconcilesOnceWithoutChangingRepository() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator()
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let before = try Data(contentsOf: root.appendingPathComponent(".git/index"))
        let oldHead = coordinator.snapshot?.head
        let recovered = expectation(description: "overflow recovered")
        var snapshots = 0
        let subscription = coordinator.$snapshot.dropFirst().sink { snapshot in
            guard snapshot != nil else { return }
            snapshots += 1
            recovered.fulfill()
        }
        for _ in 0..<50 { coordinator.invalidate(overflow: true) }
        XCTAssertTrue(coordinator.isStale)
        await fulfillment(of: [recovered], timeout: 5)
        subscription.cancel()
        XCTAssertFalse(coordinator.isStale)
        XCTAssertNil(coordinator.errorMessage)
        XCTAssertEqual(snapshots, 1)
        XCTAssertEqual(coordinator.snapshot?.head, oldHead)
        XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent(".git/index")), before)
    }

    func testLinkedWorktreeObservesCommonMainRefOutsideItsRoot() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let linked = root.appendingPathComponent("linked ünicode", isDirectory: true)
        try git(["worktree", "add", "--detach", linked.path, "HEAD"], in: root)
        let coordinator = WorktreeChangeReviewCoordinator()
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: linked, mainBranch: "trunk")
        await coordinator.refresh()
        let oldMain = try XCTUnwrap(coordinator.snapshot?.main)
        coordinator.observeSelectedWorktree()
        XCTAssertNil(coordinator.observerWarning)
        let updated = expectation(description: "common ref event reconciled before periodic poll")
        updated.assertForOverFulfill = false
        let subscription = coordinator.$snapshot.sink { snapshot in
            if let snapshot, snapshot.main != oldMain { updated.fulfill() }
        }
        try git(["update-ref", "refs/heads/trunk", "HEAD~1"], in: root)
        await fulfillment(of: [updated], timeout: 6) // Poll interval is 15 seconds.
        subscription.cancel()
        XCTAssertNotEqual(coordinator.snapshot?.main, oldMain)
        XCTAssertEqual(coordinator.selectedWorktree, linked)
    }

    func testObserverAndCoordinatorReleaseAfterRepeatedStop() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        for _ in 0..<10 {
            var observer: WorktreeReviewObserver? = WorktreeReviewObserver(worktree: root) { _ in }
            weak var weakObserver = observer
            XCTAssertTrue(observer!.start())
            observer?.stop()
            observer?.stop()
            observer = nil
            let deadline = ContinuousClock.now.advanced(by: .seconds(2))
            while weakObserver != nil && ContinuousClock.now < deadline { await Task.yield() }
            XCTAssertNil(weakObserver)
        }
        var coordinator: WorktreeChangeReviewCoordinator? = WorktreeChangeReviewCoordinator()
        weak var weakCoordinator = coordinator
        coordinator?.activate(worktree: root, mainBranch: "trunk")
        coordinator?.observeSelectedWorktree()
        coordinator?.deactivate()
        coordinator = nil
        await Task.yield()
        XCTAssertNil(weakCoordinator)
    }

    private func repository() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review adversarial ünicode \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        do {
            try git(["init", "-b", "trunk"], in: root)
            try git(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "--allow-empty", "-m", "base"], in: root)
            try git(["switch", "-c", "feature"], in: root)
            for index in 1...3 {
                try Data("version \(index)\n".utf8).write(to: root.appendingPathComponent("history.txt"))
                try git(["add", "history.txt"], in: root)
                try git(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "-m", "change \(index)"], in: root)
            }
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
        guard process.terminationStatus == 0 else {
            throw NSError(domain: "AdversarialGitFixture", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: arguments.joined(separator: " ")])
        }
    }
}
