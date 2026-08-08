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

    func testOpenExistingDescriptorActivatesOnlyAfterReadOnlyGitValidation() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repo with spaces", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["init", "--quiet"])
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Project", folder: repository, to: descriptorURL)
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let result = await controller.openWorkspace(at: descriptorURL)

        XCTAssertTrue(result)
        XCTAssertEqual(controller.activeDescriptor?.name, "Project")
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, repository.standardizedFileURL)
        XCTAssertEqual(controller.windowTitle, "Project — repo with spaces — gabCode")
        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.preference.lastWorkspaceURL, descriptorURL.standardizedFileURL)
    }

    func testCreateWorkspaceWritesDescriptorAndActivatesIt() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("created repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["init", "--quiet"])
        let descriptorURL = root.appendingPathComponent("saved/project.gabcode-workspace")
        let suite = "gabCode.project.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let controller = WorkspaceProjectController(defaults: defaults)
        let created = await controller.createWorkspace(
            name: "Created Project",
            folder: repository,
            descriptorURL: descriptorURL
        )

        XCTAssertTrue(created)
        XCTAssertTrue(FileManager.default.fileExists(atPath: descriptorURL.path))
        XCTAssertEqual(controller.state, .ready)
        XCTAssertEqual(controller.activeDescriptor?.name, "Created Project")
        XCTAssertEqual(controller.activeDescriptor?.resolvedFolder, repository.standardizedFileURL)
    }

    func testInvalidOpenPreservesCurrentActiveProjectAndProducesRecoveryState() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["init", "--quiet"])
        let validURL = root.appendingPathComponent("valid.gabcode-workspace")
        try WorkspaceDescriptor.write(name: "Valid", folder: repository, to: validURL)
        let invalidURL = root.appendingPathComponent("invalid.gabcode-workspace")
        try Data("{\"version\":1,\"name\":\"Broken\",\"folders\":[]}".utf8).write(to: invalidURL)
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
