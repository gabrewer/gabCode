import Foundation
@testable import gabCode
import XCTest

final class GitWorktreeDiscoveryTests: XCTestCase {
    func testDiscoversBranchesAndResolvesWorktreeFromNonGitProjectRoot() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let feature = project.appendingPathComponent("wt/feature ünicode", isDirectory: true)
        try FileManager.default.createDirectory(at: main, withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try Data("content".utf8).write(to: main.appendingPathComponent("README.md"))
        try runGit(in: main, arguments: ["add", "."])
        try runGit(in: main, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"])
        try runGit(in: main, arguments: ["branch", "feature/demo"])
        try FileManager.default.createDirectory(at: feature.deletingLastPathComponent(), withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["worktree", "add", "--quiet", feature.path, "feature/demo"])

        let discovery = GitWorktreeDiscovery()
        let branches = try await discovery.branches(in: project)
        let resolved = try await discovery.resolve(branch: "feature/demo", in: project)

        XCTAssertEqual(branches, ["feature/demo", "trunk"])
        XCTAssertEqual(resolved, feature.standardizedFileURL)
    }

    func testPreservesPrimaryAndBranchBearingEntriesWithoutCollapsingPaths() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let primary = project.appendingPathComponent("checkout", isDirectory: true)
        let feature = project.appendingPathComponent("wt/feature ünicode", isDirectory: true)
        let detached = project.appendingPathComponent("wt/detached", isDirectory: true)
        try FileManager.default.createDirectory(at: primary, withIntermediateDirectories: true)
        try runGit(in: primary, arguments: ["-c", "init.defaultBranch=trunk", "init", "--quiet"])
        try Data("content".utf8).write(to: primary.appendingPathComponent("README.md"))
        try runGit(in: primary, arguments: ["add", "."])
        try runGit(in: primary, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"])
        try runGit(in: primary, arguments: ["branch", "feature/demo"])
        try FileManager.default.createDirectory(at: feature.deletingLastPathComponent(), withIntermediateDirectories: true)
        try runGit(in: primary, arguments: ["worktree", "add", "--quiet", feature.path, "feature/demo"])
        try runGit(in: primary, arguments: ["worktree", "add", "--quiet", "--detach", detached.path])

        let entries = try await GitWorktreeDiscovery().worktrees(in: project)

        XCTAssertEqual(entries.map(\.branch), ["trunk", "feature/demo"])
        XCTAssertEqual(entries.map(\.path), [primary.standardizedFileURL, feature.standardizedFileURL])
        XCTAssertEqual(entries.map(\.isPrimary), [true, false])
    }

    func testDrainsLargeGitOutputWithoutDeadlockingOnPipeCapacity() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repository", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["init", "--quiet"])

        let fakeGit = root.appendingPathComponent("fake git")
        let output = "#!/bin/sh\nprintf 'worktree %s\\nbranch refs/heads/main\\n\\n' '\(repository.path)'\nhead -c 200000 /dev/zero\n"
        try Data(output.utf8).write(to: fakeGit)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)

        let branches = try await GitWorktreeDiscovery(
            gitExecutable: fakeGit,
            timeout: .seconds(1)
        ).branches(in: repository)

        XCTAssertEqual(branches, ["main"])
    }

    func testCreationFormKeepsEditableBranchAndLocationPreviews() {
        let root = URL(fileURLWithPath: "/tmp/project/wt", isDirectory: true)
        var form = WorktreeCreationFormState(name: "billing fix", under: root, base: .workspaceSelectedBranch)

        XCTAssertEqual(form.branch, "feature/billing-fix")
        XCTAssertEqual(form.location, root.appendingPathComponent("wt-billing-fix", isDirectory: true).standardizedFileURL)
        form.name = "bugfix/urgent"
        form.refreshDefaults()

        XCTAssertEqual(form.branch, "bugfix/urgent")
        XCTAssertEqual(form.location, root.appendingPathComponent("wt-bugfix-urgent", isDirectory: true).standardizedFileURL)
        XCTAssertEqual(form.base, .workspaceSelectedBranch)
    }

    func testPreviewsEditableBranchAndSanitizedLocationDefaults() {
        let root = URL(fileURLWithPath: "/tmp/project/wt", isDirectory: true)

        let preview = WorktreeActionPreview.make(name: "billing fix", under: root)

        XCTAssertEqual(preview.branch, "feature/billing-fix")
        XCTAssertEqual(preview.location, root.appendingPathComponent("wt-billing-fix", isDirectory: true).standardizedFileURL)
        XCTAssertTrue(preview.isValidBranch)
        XCTAssertTrue(preview.isValidLocation)
    }

    func testCreatesFromWorkspaceBranchAndReconcilesGitWorktrees() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let created = project.appendingPathComponent("wt/billing fix", isDirectory: true)
        try makeRepository(at: main, defaultBranch: "trunk")
        try FileManager.default.createDirectory(at: created.deletingLastPathComponent(), withIntermediateDirectories: true)

        let request = WorktreeCreationRequest(
            name: "billing fix",
            branch: "feature/billing-fix",
            location: created,
            base: .workspaceSelectedBranch
        )
        let entries = try await GitWorktreeActionService().create(
            request: request,
            projectRoot: project,
            workspaceSelectedBranch: "trunk"
        )

        XCTAssertEqual(entries.map(\.branch), ["trunk", "feature/billing-fix"])
        XCTAssertEqual(entries.last?.path, created.standardizedFileURL)
    }

    func testRejectsPrimaryRemovalAndRemovesSecondaryWithoutDeletingBranch() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let feature = project.appendingPathComponent("wt/feature", isDirectory: true)
        try makeRepository(at: main, defaultBranch: "trunk")
        try FileManager.default.createDirectory(at: feature.deletingLastPathComponent(), withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["worktree", "add", "--quiet", "-b", "feature/demo", feature.path, "trunk"])
        let service = GitWorktreeActionService()

        do {
            try await service.remove(path: main, projectRoot: project, force: false, deleteLocalBranch: false)
            XCTFail("Primary worktree removal must be rejected.")
        } catch let error as WorktreeActionError {
            XCTAssertEqual(error, .primaryWorktreeProtected(main.standardizedFileURL))
        }

        try await service.remove(path: feature, projectRoot: project, force: false, deleteLocalBranch: false)
        XCTAssertFalse(FileManager.default.fileExists(atPath: feature.path))
        XCTAssertEqual(try runGitOutput(in: main, arguments: ["branch", "--list", "feature/demo"]).trimmingCharacters(in: .whitespacesAndNewlines), "feature/demo")
    }

    func testLatestRemoteRequiresConfiguredWorkspaceUpstream() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let created = project.appendingPathComponent("wt/latest", isDirectory: true)
        try makeRepository(at: main, defaultBranch: "trunk")
        try FileManager.default.createDirectory(at: created.deletingLastPathComponent(), withIntermediateDirectories: true)
        let request = WorktreeCreationRequest(
            name: "latest",
            branch: "feature/latest",
            location: created,
            base: .workspaceSelectedBranch,
            useLatestRemote: true
        )

        do {
            _ = try await GitWorktreeActionService().create(request: request, projectRoot: project, workspaceSelectedBranch: "trunk")
            XCTFail("Latest remote creation must require a configured upstream.")
        } catch let error as WorktreeActionError {
            guard case .gitFailed = error else { return XCTFail("Unexpected error: \(error)") }
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: created.path))
    }

    func testTimesOutAStuckGitAction() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repository", isDirectory: true)
        try makeRepository(at: repository, defaultBranch: "trunk")
        let fakeGit = root.appendingPathComponent("fake git")
        let script = "#!/bin/sh\ncase \"$*\" in *'worktree list'*) printf 'worktree \(repository.path)\\nbranch refs/heads/trunk\\n\\n' ;; *) sleep 5 ;; esac\n"
        try Data(script.utf8).write(to: fakeGit)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let request = WorktreeCreationRequest(
            name: "stuck",
            branch: "feature/stuck",
            location: root.appendingPathComponent("stuck", isDirectory: true),
            base: .workspaceSelectedBranch
        )

        do {
            _ = try await GitWorktreeActionService(gitExecutable: fakeGit, timeout: .milliseconds(50))
                .create(request: request, projectRoot: repository, workspaceSelectedBranch: "trunk")
            XCTFail("A stuck action must time out.")
        } catch let error as WorktreeActionError {
            guard case let .gitFailed(_, reason) = error else { return XCTFail("Unexpected error: \(error)") }
            XCTAssertTrue(reason.localizedCaseInsensitiveContains("timed out"))
        }
    }

    func testRejectsDirtyRemovalUnlessForceIsExplicit() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("project", isDirectory: true)
        let main = project.appendingPathComponent("main", isDirectory: true)
        let feature = project.appendingPathComponent("wt/feature", isDirectory: true)
        try makeRepository(at: main, defaultBranch: "trunk")
        try FileManager.default.createDirectory(at: feature.deletingLastPathComponent(), withIntermediateDirectories: true)
        try runGit(in: main, arguments: ["worktree", "add", "--quiet", "-b", "feature/dirty", feature.path, "trunk"])
        try Data("uncommitted".utf8).write(to: feature.appendingPathComponent("untracked.txt"))

        do {
            try await GitWorktreeActionService().remove(path: feature, projectRoot: project, force: false, deleteLocalBranch: false)
            XCTFail("Dirty safe removal must fail without force.")
        } catch let error as WorktreeActionError {
            guard case .gitFailed = error else { return XCTFail("Unexpected error: \(error)") }
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: feature.path))
    }

    func testRejectsProjectRootWithNoRepository() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }

        do {
            _ = try await GitWorktreeDiscovery().branches(in: root)
            XCTFail("Expected repository discovery to reject a project with no Git repository.")
        } catch let error as GitWorktreeDiscoveryError {
            XCTAssertEqual(error, .repositoryNotFound(root.standardizedFileURL))
        }
    }

    private func makeRepository(at directory: URL, defaultBranch: String) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try runGit(in: directory, arguments: ["-c", "init.defaultBranch=\(defaultBranch)", "init", "--quiet"])
        try Data("content".utf8).write(to: directory.appendingPathComponent("README.md"))
        try runGit(in: directory, arguments: ["add", "."])
        try runGit(in: directory, arguments: ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"])
    }

    private func runGitOutput(in directory: URL, arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = ["git"] + arguments
        process.currentDirectoryURL = directory
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        return String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
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
            .appendingPathComponent("gabCode worktree discovery ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
