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
