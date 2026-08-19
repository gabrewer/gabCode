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
        try WorkspaceDescriptor.write(name: "Project", projectRoot: project, branch: "feature/demo", to: descriptorURL)
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let result = await controller.openWorkspace(at: descriptorURL)

        XCTAssertTrue(result)
        XCTAssertEqual(controller.activeDescriptor?.name, "Project")
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, feature.standardizedFileURL)
        XCTAssertEqual(controller.projectRoot, project.standardizedFileURL)
        XCTAssertEqual(controller.descriptorBranch, "feature/demo")
        XCTAssertEqual(controller.worktrees.map(\.path), [main.standardizedFileURL, feature.standardizedFileURL])
        XCTAssertEqual(controller.windowTitle, "Project — feature demo — gabCode")
        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.preference.lastWorkspaceURL, descriptorURL.standardizedFileURL)
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
        let validURL = root.appendingPathComponent("valid.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Valid", projectRoot: repository, branch: "trunk", to: validURL)
        let invalidURL = root.appendingPathComponent("invalid.gabcode-workspace")
        try Data("{\"version\":1,\"name\":\"Broken\",\"project\":{\"path\":\"repo\",\"branch\":\"trunk\"},\"unknown\":true}".utf8).write(to: invalidURL)
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
        guard case .recovery = controller.state else {
            return XCTFail("Expected actionable recovery state")
        }
    }

    func testFailedRefreshPreservesReadyProjectAndPublishesActionableError() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repository", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Project", projectRoot: repository, branch: "trunk", to: descriptorURL)
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
