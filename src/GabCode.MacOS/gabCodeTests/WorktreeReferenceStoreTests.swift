import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorktreeReferenceStoreTests: XCTestCase {
    func testSlotsRoundTripIndependentlyForStandardizedWorkspaceAndWorktreeURLs() throws {
        let defaults = makeIsolatedDefaults()
        let store = WorktreeReferenceStore(defaults: defaults)
        let workspace = URL(fileURLWithPath: "/tmp/references/../references/Project.gabcode-workspace")
        let worktree = URL(fileURLWithPath: "/tmp/references/wt/../wt/feature", isDirectory: true)
        let markdown = try makeMarkdownFile(named: "Notes ünicode.markdown")
        let issue = try XCTUnwrap(GitHubIssueReference(urlString: "https://github.com/octo-org/repository/issues/42?plain=1#comment"))

        XCTAssertTrue(store.setMarkdownURL(markdown, for: workspace, worktreeURL: worktree))
        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree).markdownURL, markdown.standardizedFileURL)
        XCTAssertNil(store.references(for: workspace, worktreeURL: worktree).issue)

        store.setIssue(issue, for: workspace, worktreeURL: worktree)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree).markdownURL, markdown.standardizedFileURL)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree).issue, issue)

        store.removeMarkdown(for: workspace, worktreeURL: worktree)
        XCTAssertNil(store.references(for: workspace, worktreeURL: worktree).markdownURL)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree).issue, issue)
    }

    func testMarkdownAssignmentRejectsMissingDirectoriesAndNonMarkdownFilesWithoutOverwritingExistingValue() throws {
        let defaults = makeIsolatedDefaults()
        let store = WorktreeReferenceStore(defaults: defaults)
        let workspace = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let worktree = URL(fileURLWithPath: "/tmp/project/wt/feature", isDirectory: true)
        let markdown = try makeMarkdownFile(named: "valid.md")
        XCTAssertTrue(store.setMarkdownURL(markdown, for: workspace, worktreeURL: worktree))

        XCTAssertFalse(store.setMarkdownURL(URL(fileURLWithPath: "/tmp/missing.md"), for: workspace, worktreeURL: worktree))
        XCTAssertFalse(store.setMarkdownURL(markdown.deletingLastPathComponent(), for: workspace, worktreeURL: worktree))
        let text = markdown.deletingLastPathComponent().appendingPathComponent("not-markdown.txt")
        try Data("text".utf8).write(to: text)
        XCTAssertFalse(store.setMarkdownURL(text, for: workspace, worktreeURL: worktree))
        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree).markdownURL, markdown.standardizedFileURL)
    }

    func testIssueReferenceCanonicalizesOnlyHTTPSGitHubIssueURLs() throws {
        let reference = try XCTUnwrap(GitHubIssueReference(urlString: "https://github.com/owner/repository/issues/42?plain=1#comment"))
        XCTAssertEqual(reference.url, URL(string: "https://github.com/owner/repository/issues/42"))
        XCTAssertEqual(reference.displayIdentity, "owner/repository#42")

        for invalid in [
            "http://github.com/owner/repository/issues/42",
            "https://github.com/owner/repository/pull/42",
            "https://github.com/owner/repository/issues/0",
            "https://github.com/owner/repository/issues/not-a-number",
            "https://example.com/owner/repository/issues/42",
            "https://github.com/owner/issues/42",
            "not a URL"
        ] {
            XCTAssertNil(GitHubIssueReference(urlString: invalid), invalid)
        }
    }

    func testSeparateWindowStoresPreserveEachOthersLatestWorktreeUpdates() throws {
        let defaults = makeIsolatedDefaults()
        let firstWindowStore = WorktreeReferenceStore(defaults: defaults)
        let secondWindowStore = WorktreeReferenceStore(defaults: defaults)
        let workspace = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let firstWorktree = URL(fileURLWithPath: "/tmp/project/wt/first", isDirectory: true)
        let secondWorktree = URL(fileURLWithPath: "/tmp/project/wt/second", isDirectory: true)
        let firstIssue = try XCTUnwrap(GitHubIssueReference(urlString: "https://github.com/owner/repository/issues/1"))
        let secondIssue = try XCTUnwrap(GitHubIssueReference(urlString: "https://github.com/owner/repository/issues/2"))

        firstWindowStore.setIssue(firstIssue, for: workspace, worktreeURL: firstWorktree)
        secondWindowStore.setIssue(secondIssue, for: workspace, worktreeURL: secondWorktree)

        XCTAssertEqual(firstWindowStore.references(for: workspace, worktreeURL: firstWorktree).issue, firstIssue)
        XCTAssertEqual(firstWindowStore.references(for: workspace, worktreeURL: secondWorktree).issue, secondIssue)
    }

    func testMalformedStoredDataRecoversToEmptyAndRepairsItsPreferenceKey() throws {
        let defaults = makeIsolatedDefaults()
        let workspace = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let worktree = URL(fileURLWithPath: "/tmp/project/wt/feature", isDirectory: true)
        defaults.set("not an association dictionary", forKey: WorktreeReferenceStore.storageKey)

        let store = WorktreeReferenceStore(defaults: defaults)

        XCTAssertEqual(store.references(for: workspace, worktreeURL: worktree), .empty)
        XCTAssertNil(defaults.object(forKey: WorktreeReferenceStore.storageKey))
    }

    func testMalformedPartialAssociationDoesNotBecomeAFabricatedFileReference() throws {
        let defaults = makeIsolatedDefaults()
        let workspace = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let worktree = URL(fileURLWithPath: "/tmp/project/wt/feature", isDirectory: true)
        let stored = """
        [{"workspacePath":"/tmp/project.gabcode-workspace","worktreePath":"/tmp/project/wt/feature","markdownPath":"https://example.com/not-a-file.md","issueURL":"https://github.com/owner/repository/issues/7"}]
        """
        defaults.set(Data(stored.utf8), forKey: WorktreeReferenceStore.storageKey)

        let references = WorktreeReferenceStore(defaults: defaults).references(for: workspace, worktreeURL: worktree)

        XCTAssertNil(references.markdownURL)
        XCTAssertEqual(references.issue?.displayIdentity, "owner/repository#7")
    }

    func testConfirmedRemovalDeletesOnlyTheRemovedWorktreeWhileTransientUnavailabilityRetainsIt() throws {
        let defaults = makeIsolatedDefaults()
        let store = WorktreeReferenceStore(defaults: defaults)
        let workspace = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let retainedWorktree = URL(fileURLWithPath: "/tmp/project/wt/retained", isDirectory: true)
        let removedWorktree = URL(fileURLWithPath: "/tmp/project/wt/removed", isDirectory: true)
        let retainedMarkdown = try makeMarkdownFile(named: "retained.md")
        let removedMarkdown = try makeMarkdownFile(named: "removed.md")
        XCTAssertTrue(store.setMarkdownURL(retainedMarkdown, for: workspace, worktreeURL: retainedWorktree))
        XCTAssertTrue(store.setMarkdownURL(removedMarkdown, for: workspace, worktreeURL: removedWorktree))

        store.removeConfirmedWorktrees([], for: workspace)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: removedWorktree).markdownURL, removedMarkdown.standardizedFileURL)

        store.removeConfirmedWorktrees([removedWorktree], for: workspace)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: removedWorktree), .empty)
        XCTAssertEqual(store.references(for: workspace, worktreeURL: retainedWorktree).markdownURL, retainedMarkdown.standardizedFileURL)
    }

    private func makeIsolatedDefaults() -> UserDefaults {
        let suiteName = "gabCode.WorktreeReferenceStoreTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        addTeardownBlock { defaults.removePersistentDomain(forName: suiteName) }
        return defaults
    }

    private func makeMarkdownFile(named name: String) throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode reference tests \(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent(name)
        try Data("# Reference".utf8).write(to: file)
        return file.standardizedFileURL
    }
}
