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

    func testNewCommitRetainsStillUniqueOlderSelectedFileAndRebasesLoadedPage() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let page = expectation(description: "older commit loaded")
        let pageToken = coordinator.$snapshot.sink { if $0?.commits.count == 2 { page.fulfill() } }
        coordinator.loadMore()
        await fulfillment(of: [page], timeout: 5)
        pageToken.cancel()
        let older = try XCTUnwrap(coordinator.snapshot?.commits.last)
        let loaded = expectation(description: "older files loaded")
        let fileToken = coordinator.$commitFiles.sink { if $0[older.sha] != nil { loaded.fulfill() } }
        coordinator.toggleCommit(older.sha)
        await fulfillment(of: [loaded], timeout: 5)
        fileToken.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[older.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        presentation.reviewDocument.apply(ReviewDiff(state: .patch, text: "historical patch"))
        let revision = presentation.reviewDocument.revision
        try git(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "--allow-empty", "-m", "unrelated tip"], in: root)
        try git(["merge-base", "--is-ancestor", older.sha, "HEAD"], in: root)
        await coordinator.refresh()
        let shas = try XCTUnwrap(coordinator.snapshot?.commits.map(\.sha))
        XCTAssertEqual(shas.count, 3, "Include the new tip and previously loaded older commit")
        XCTAssertEqual(shas.last, older.sha, "A new tip should shift, not discard, a still-unique older commit")
        XCTAssertTrue(coordinator.expandedCommits.contains(older.sha))
        XCTAssertEqual(coordinator.selectedFile?.id, file.id)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
        XCTAssertEqual(presentation.reviewDocument.revision, revision, "An immutable selected patch must not restart")
        XCTAssertTrue(coordinator.snapshot?.hasMoreCommits == true)
        // The preserved page count is a new cursor: paging must continue without duplicates.
        let nextPage = expectation(description: "load next older commit")
        let nextToken = coordinator.$snapshot.sink { if $0?.commits.count == 4 { nextPage.fulfill() } }
        coordinator.loadMore()
        await fulfillment(of: [nextPage], timeout: 5)
        nextToken.cancel()
        XCTAssertEqual(Set(coordinator.snapshot?.commits.map(\.sha) ?? []).count, 4)
        XCTAssertEqual(coordinator.selectedFile?.id, file.id)
    }

    func testMultipleNewTipsBeyondOriginalPageStillKeepOlderReview() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let older = try XCTUnwrap(coordinator.snapshot?.commits.first)
        let loaded = expectation(description: "selected commit files")
        let token = coordinator.$commitFiles.sink { if $0[older.sha] != nil { loaded.fulfill() } }
        coordinator.toggleCommit(older.sha)
        await fulfillment(of: [loaded], timeout: 5)
        token.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[older.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        for index in 0..<4 {
            try git(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "--allow-empty", "-m", "new tip \(index)"], in: root)
        }
        await coordinator.refresh()
        XCTAssertEqual(coordinator.snapshot?.commits.count, 5)
        XCTAssertEqual(coordinator.snapshot?.commits.last?.sha, older.sha)
        XCTAssertEqual(coordinator.selectedFile?.id, file.id)
        XCTAssertTrue(presentation.reviewDocument.isOpen)
    }

    func testRebasedAwaySelectionClosesEvenWhenNewRangeHasMorePages() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let coordinator = WorktreeChangeReviewCoordinator(service: WorktreeChangeReviewService(pageSize: 1))
        defer { coordinator.deactivate() }
        coordinator.activate(worktree: root, mainBranch: "trunk")
        await coordinator.refresh()
        let page = expectation(description: "older page")
        let pageToken = coordinator.$snapshot.sink { if $0?.commits.count == 2 { page.fulfill() } }
        coordinator.loadMore()
        await fulfillment(of: [page], timeout: 5)
        pageToken.cancel()
        let old = try XCTUnwrap(coordinator.snapshot?.commits.last)
        let loaded = expectation(description: "old commit files")
        let fileToken = coordinator.$commitFiles.sink { if $0[old.sha] != nil { loaded.fulfill() } }
        coordinator.toggleCommit(old.sha)
        await fulfillment(of: [loaded], timeout: 5)
        fileToken.cancel()
        let file = try XCTUnwrap(coordinator.commitFiles[old.sha]?.first)
        let presentation = TerminalWorkspacePresentation(workingDirectory: root)
        coordinator.open(file: file, in: presentation)
        try git(["reset", "--hard", "trunk"], in: root) // isolated throwaway fixture only
        for index in 0..<3 {
            try git(["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "--allow-empty", "-m", "replacement \(index)"], in: root)
        }
        await coordinator.refresh()
        XCTAssertNil(coordinator.errorMessage)
        XCTAssertFalse(coordinator.isStale)
        XCTAssertFalse(coordinator.snapshot?.commits.contains(where: { $0.sha == old.sha }) ?? true)
        XCTAssertNil(coordinator.selectedFile)
        XCTAssertFalse(presentation.reviewDocument.isOpen)
        XCTAssertNotNil(coordinator.notice)
    }

    func testMergeReorderingRetainsEveryPreviouslyLoadedCommitNotJustOldTail() async throws {
        let root = try repository()
        defer { try? FileManager.default.removeItem(at: root) }
        let tree = try readGit(["rev-parse", "HEAD^{tree}"], in: root)
        // Fixed-date DAG: the old prefix is [16,14,11]; merging 40 changes its
        // order to [41,40,16,11,26,19,14,...]. Finding old tail 11 is not enough.
        let graph: [(Int, [Int])] = [(0, []), (1, [0]), (2, [0, 1]), (7, [0]), (8, [2]),
                                    (11, [7]), (14, [7]), (16, [11, 14]), (19, [8, 14]),
                                    (26, [19]), (40, [26, 16]), (41, [16, 40])]
        var shas: [Int: String] = [:]
        for (node, parents) in graph {
            let arguments = ["-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit-tree", tree, "-m", "\(node)"]
                + parents.flatMap { ["-p", shas[$0]!] }
            shas[node] = try readGit(arguments, in: root)
        }
        try git(["update-ref", "refs/heads/trunk", shas[0]!], in: root)
        try git(["update-ref", "refs/heads/feature", shas[16]!], in: root)
        let service = WorktreeChangeReviewService(pageSize: 3)
        let previous = try await service.snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertEqual(previous.commits.map(\.subject), ["16", "14", "11"])
        try git(["update-ref", "refs/heads/feature", shas[41]!], in: root)
        let next = try await service.snapshot(worktree: root, mainBranch: "trunk")
        let reconciled = try await service.reconciledHistory(next, retaining: previous, mainBranch: "trunk")
        for commit in previous.commits {
            XCTAssertTrue(reconciled.commits.contains(where: { $0.sha == commit.sha }), "Still-reachable loaded commit \(commit.subject) must survive merge reordering")
        }
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

    private func readGit(_ arguments: [String], in root: URL) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = arguments
        process.currentDirectoryURL = root
        process.environment = ProcessInfo.processInfo.environment.merging([
            "GIT_AUTHOR_DATE": "2000-01-01T00:00:00Z", "GIT_COMMITTER_DATE": "2000-01-01T00:00:00Z"
        ]) { _, value in value }
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw NSError(domain: "AdversarialDAG", code: Int(process.terminationStatus)) }
        return String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
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
