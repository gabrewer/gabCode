import Foundation
@testable import gabCode
import XCTest

@MainActor
final class GitRepositoryValidatorTests: XCTestCase {
    func testValidatesRealRepositoryWithoutMutation() async throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repository = root.appendingPathComponent("repo with spaces", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runGit(in: repository, arguments: ["init", "--quiet"])
        try Data("content".utf8).write(to: repository.appendingPathComponent("file.txt"))

        let result = await GitRepositoryValidator().validate(folder: repository)

        XCTAssertEqual(result, .valid(repository: repository.standardizedFileURL))
        XCTAssertTrue(FileManager.default.fileExists(atPath: repository.appendingPathComponent("file.txt").path))
    }

    func testRejectsNonGitFolderWithBoundedReadOnlyProcessError() async throws {
        let folder = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }

        let result = await GitRepositoryValidator().validate(folder: folder)

        guard case let .notRepository(_, stderr) = result else {
            return XCTFail("Expected not-repository result, got \(result)")
        }
        XCTAssertFalse(stderr.isEmpty)
    }

    func testReportsMissingGitExecutableWithoutLaunchingShell() async throws {
        let folder = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }

        let result = await GitRepositoryValidator(
            gitExecutable: URL(fileURLWithPath: "/does/not/exist/git")
        ).validate(folder: folder)

        XCTAssertEqual(result, .gitUnavailable(URL(fileURLWithPath: "/does/not/exist/git")))
    }

    func testCancellationStopsTheOwnedGitProcess() async throws {
        let folder = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let runner = GitRepositoryValidator(
            gitExecutable: URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", "sleep 30"]
        )
        let task = Task { await runner.validate(folder: folder) }
        try await Task.sleep(for: .milliseconds(50))
        task.cancel()

        let result = await task.value
        XCTAssertEqual(result, .cancelled)
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
            .appendingPathComponent("gabCode git validator ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
