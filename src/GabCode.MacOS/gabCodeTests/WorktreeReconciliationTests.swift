import Foundation
@testable import gabCode
import XCTest

final class WorktreeReconciliationTests: XCTestCase {
    func testFirstMissingMakesEntryUnavailableAndSecondMissingOrphansOnlyRetainedPath() {
        let primary = entry("/tmp/project/main", branch: "trunk", primary: true)
        let retained = entry("/tmp/project/wt/feature ünicode", branch: "feature/demo")
        let transient = entry("/tmp/project/wt/no terminals", branch: "feature/other")
        let initial = WorktreeReconciliation.reconcile(
            previous: [],
            discovered: [primary, retained, transient],
            retainedPaths: []
        )

        let firstMissing = WorktreeReconciliation.reconcile(
            previous: initial.worktrees,
            discovered: [primary],
            retainedPaths: [retained.path]
        )
        XCTAssertEqual(firstMissing.worktrees.map(\.availability), [.available, .unavailable, .unavailable])
        XCTAssertTrue(firstMissing.orphanedPaths.isEmpty)

        let secondMissing = WorktreeReconciliation.reconcile(
            previous: firstMissing.worktrees,
            discovered: [primary],
            retainedPaths: [retained.path]
        )
        XCTAssertEqual(secondMissing.worktrees.map(\.path), [primary.path])
        XCTAssertEqual(secondMissing.orphanedPaths, [retained.path])
    }

    func testReturningNormalizedPathRestoresAvailableEntryAndClearsOrphan() {
        let path = URL(fileURLWithPath: "/tmp/project/wt/feature", isDirectory: true).standardizedFileURL
        let orphan = WorktreeReconciliation.reconcile(
            previous: [WorktreeNavigationEntry(path: path, branch: "feature/demo", isPrimary: false, availability: .unavailable, consecutiveMissingRefreshes: 1)],
            discovered: [],
            retainedPaths: [path]
        )

        let restored = WorktreeReconciliation.reconcile(
            previous: orphan.worktrees,
            discovered: [entry(path.path, branch: "feature/demo")],
            retainedPaths: [path],
            existingOrphanedPaths: orphan.orphanedPaths
        )
        XCTAssertEqual(restored.worktrees.map(\.availability), [.available])
        XCTAssertTrue(restored.orphanedPaths.isEmpty)
    }

    func testOrdersPrimaryThenAvailableFolderNameThenUnavailable() {
        let primary = entry("/tmp/project/z-primary", branch: "trunk", primary: true)
        let alpha = entry("/tmp/project/wt/Alpha", branch: "feature/a")
        let unavailable = WorktreeNavigationEntry(
            path: URL(fileURLWithPath: "/tmp/project/wt/Aardvark", isDirectory: true),
            branch: "feature/missing",
            isPrimary: false,
            availability: .unavailable,
            consecutiveMissingRefreshes: 0
        )

        let result = WorktreeReconciliation.reconcile(
            previous: [unavailable],
            discovered: [alpha, primary],
            retainedPaths: []
        )
        XCTAssertEqual(result.worktrees.map(\.path), [primary.path, alpha.path, unavailable.path])
    }

    private func entry(_ path: String, branch: String, primary: Bool = false) -> GitWorktreeEntry {
        GitWorktreeEntry(
            path: URL(fileURLWithPath: path, isDirectory: true),
            branch: branch,
            isPrimary: primary
        )
    }
}
