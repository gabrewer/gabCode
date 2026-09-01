import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceProjectTests: XCTestCase {
    func testTitleUsesWorkspaceNameAndResolvedFolderName() {
        XCTAssertEqual(
            WorkspaceProjectTitle.value(name: "GabCode", folder: URL(fileURLWithPath: "/tmp/repo")),
            "GabCode — repo — gabCode"
        )
    }

    func testOpenProjectRootResolvesSelectedBranchWorktreeBeforeActivation() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project with spaces", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let feature = project.appendingPathComponent("wt/feature demo", isDirectory: true)
        try FileManager.default.createDirectory(at: main, withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try Data("initial".utf8).write(to: main.appendingPathComponent("README.md"))
        try runGit(in: main, arguments: ["add", "."])
        try runGit(in: main, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"])
        try runGit(in: main, arguments: ["branch", "feature/demo"])
        try FileManager.default.createDirectory(at: feature.deletingLastPathComponent(), withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["worktree", "add", "--quiet", feature.path, "feature/demo"])
        let descriptorURL = project.appendingPathComponent("project.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Project", projectRoot: project, mainBranch: "trunk", to: descriptorURL)
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let result = await controller.openWorkspace(at: descriptorURL)

        XCTAssertTrue(result)
        XCTAssertEqual(controller.activeDescriptor?.name, "Project")
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, main.standardizedFileURL)
        XCTAssertEqual(controller.projectRoot, project.standardizedFileURL)
        XCTAssertEqual(controller.mainBranch, "trunk")
        XCTAssertEqual(controller.worktrees.map(\.path), [main.standardizedFileURL, feature.standardizedFileURL])
        XCTAssertEqual(controller.windowTitle, "Project — main — gabCode")
        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.preference.lastWorkspaceURL, descriptorURL.standardizedFileURL)

        controller.preference.setSelectedWorktreeURL(
            feature,
            for: descriptorURL,
            availableWorktrees: [main, feature]
        )
        try runGit(in: main, arguments: ["worktree", "remove", "--force", feature.path])

        let reopened = await controller.openWorkspace(at: descriptorURL)
        XCTAssertTrue(reopened)
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, main.standardizedFileURL)
        XCTAssertEqual(
            controller.fallbackNotice,
            "The previously selected worktree is no longer available. Opened main instead."
        )
    }

    func testCreateWorkspaceWritesDescriptorInNonGitProjectRootAndActivatesBranch() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("created project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        try FileManager.default.createDirectory(at: main, withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try Data("initial".utf8).write(to: main.appendingPathComponent("README.md"))
        try runGit(in: main, arguments: ["add", "."])
        try runGit(in: main, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"])
        let descriptorURL = project.appendingPathComponent("Created Project.gabcode-workspace")
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let created = await controller.createWorkspace(
            name: "Created Project",
            projectRoot: project,
            branch: "trunk",
            descriptorURL: descriptorURL
        )

        XCTAssertTrue(created)
        XCTAssertTrue(FileManager.default.fileExists(atPath: descriptorURL.path))
        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.activeDescriptor?.name, "Created Project")
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, main.standardizedFileURL)
    }

    func testInvalidOpenPreservesCurrentActiveProjectAndProducesRecoveryState() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try runGit(in: repository, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "--quiet", "-m", "initial"])
        let validURL = root.appendingPathComponent("valid.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Valid", projectRoot: repository, mainBranch: "trunk", to: validURL)
        let invalidURL = root.appendingPathComponent("invalid.gabcode-workspace")
        try Data("{\"version\":1,\"name\":\"Broken\",\"project\":{\"path\":\"repo\",\"mainBranch\":\"trunk\"},\"unknown\":true}".utf8).write(to: invalidURL)
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let opened = await controller.openWorkspace(at: validURL)
        let reopened = await controller.openWorkspace(at: invalidURL)
        XCTAssertTrue(opened)
        XCTAssertFalse(reopened)

        XCTAssertEqual(controller.activeDescriptor?.name, "Valid")
        XCTAssertEqual(controller.windowTitle, "Valid — repo — gabCode")
        guard case let .recovery(error) = controller.state else {
            return XCTFail("Expected actionable recovery state")
        }
        XCTAssertEqual(
            error.message,
            "The workspace file contains unknown property \"unknown\". Workspace file: \(invalidURL.path)"
        )

        let missingMainURL = root.appendingPathComponent("missing-main.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Missing Main", projectRoot: repository, mainBranch: "missing", to: missingMainURL)
        let openedMissingMain = await controller.openWorkspace(at: missingMainURL)
        XCTAssertFalse(openedMissingMain)
        guard case let .recovery(.branchNotFound(_, errorURL)) = controller.state else {
            return XCTFail("A missing local main branch must produce workspace-file recovery.")
        }
        XCTAssertEqual(errorURL, missingMainURL.standardizedFileURL)
        XCTAssertEqual(controller.recoveryDescriptorURL, missingMainURL.standardizedFileURL)
    }

    func testSupersededOpenCannotPublishStaleFallbackNotice() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repository", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try runGit(in: repository, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "--quiet", "-m", "initial"])
        let oldURL = root.appendingPathComponent("old.gabcode-workspace")
        let newURL = root.appendingPathComponent("new.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Old", projectRoot: repository, mainBranch: "old", to: oldURL)
        try WorkspaceDescriptor.write(name: "New", projectRoot: repository, mainBranch: "new", to: newURL)
        let missing = root.appendingPathComponent("removed-worktree", isDirectory: true)
        let gate = BranchValidationGate()
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let controller = WorkspaceProjectController(
            defaults: defaults,
            worktreeLoader: { _ in [
                GitWorktreeEntry(path: repository, branch: "trunk", isPrimary: true),
                GitWorktreeEntry(path: missing, branch: "feature", isPrimary: false)
            ] },
            localBranchValidator: { branch, _ in await gate.validate(branch: branch) }
        )
        controller.preference.setSelectedWorktreeURL(missing, for: oldURL, availableWorktrees: [repository, missing])

        let oldOpen = Task { await controller.openWorkspace(at: oldURL) }
        for _ in 0..<100 {
            if await gate.hasBlockedOldValidation { break }
            await Task.yield()
        }
        XCTAssertTrue(await gate.hasBlockedOldValidation)

        XCTAssertTrue(await controller.openWorkspace(at: newURL))
        await gate.releaseOldValidation()
        XCTAssertFalse(await oldOpen.value)
        XCTAssertEqual(controller.activeDescriptor?.name, "New")
        XCTAssertNil(controller.fallbackNotice)
    }

    func testFailedRefreshPreservesReadyProjectAndPublishesActionableError() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repository", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try runGit(in: repository, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "--quiet", "-m", "initial"])
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Project", projectRoot: repository, mainBranch: "trunk", to: descriptorURL)
        let entry = GitWorktreeEntry(path: repository, branch: "trunk", isPrimary: true)
        let loader = RefreshLoader(entries: [entry])
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let controller = WorkspaceProjectController(defaults: defaults, worktreeLoader: { _ in
            try loader.load()
        })

        let opened = await controller.openWorkspace(at: descriptorURL)
        XCTAssertTrue(opened)
        let activeDescriptor = controller.activeDescriptor
        let worktrees = controller.worktrees
        loader.fail = true
        await controller.refreshWorktrees()

        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.activeDescriptor, activeDescriptor)
        XCTAssertEqual(controller.worktrees, worktrees)
        XCTAssertNotNil(controller.refreshError)
        XCTAssertFalse(controller.isRefreshing)
    }

    func testRefreshUsesInjectedDiscoverySeam() async throws {
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let expected = GitWorktreeEntry(
            path: URL(fileURLWithPath: "/tmp/refresh worktree", isDirectory: true),
            branch: "trunk",
            isPrimary: true
        )
        let controller = WorkspaceProjectController(
            defaults: defaults,
            worktreeLoader: { _ in [expected] }
        )

        XCTAssertEqual(controller.worktrees, [])
    }

    private actor BranchValidationGate {
        private var oldContinuation: CheckedContinuation<Void, Never>?
        private(set) var hasBlockedOldValidation = false

        func validate(branch: String) async {
            guard branch == "old" else { return }
            hasBlockedOldValidation = true
            await withCheckedContinuation { oldContinuation = $0 }
        }

        func releaseOldValidation() {
            oldContinuation?.resume()
            oldContinuation = nil
        }
    }

    private final class RefreshLoader: @unchecked Sendable {
        private let lock = NSLock()
        private let entries: [GitWorktreeEntry]
        private var shouldFail = false

        init(entries: [GitWorktreeEntry]) { self.entries = entries }

        var fail: Bool {
            get { lock.withLock { shouldFail } }
            set { lock.withLock { shouldFail = newValue } }
        }

        func load() throws -> [GitWorktreeEntry] {
            try lock.withLock {
                if shouldFail { throw GitWorktreeDiscoveryError.gitFailed(URL(fileURLWithPath: "/tmp"), reason: "controlled refresh failure") }
                return entries
            }
        }
    }

    private func runGit(in directory: URL, arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = ["git"] + arguments
        process.currentDirectoryURL = directory
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
    }

    private func temporaryDirectory() throws -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode project tests", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
