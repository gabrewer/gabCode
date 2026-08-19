import AppKit
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceTerminalRegistryTests: XCTestCase {
    func testSelectionIsLazyAndEnsureStartedIsIdempotentPerNormalizedPath() async throws {
        let directory = try makeTemporaryDirectory(name: "worktree A")
        let equivalentPath = directory.appendingPathComponent(".")
        defer { try? FileManager.default.removeItem(at: directory) }

        let registry = WorkspaceTerminalRegistry(environment: ["SHELL": "/bin/sh"])
        let presentation = registry.presentation(for: equivalentPath)

        XCTAssertEqual(registry.retainedPresentations.count, 1)
        XCTAssertEqual(presentation.activeTerminalCount, 0)
        XCTAssertEqual(presentation.workspace.terminal1.state, .idle)
        XCTAssertEqual(presentation.workspace.terminal2.state, .idle)

        let started = await registry.ensureStarted(for: directory)
        let firstPresentation = try XCTUnwrap(started)
        let firstPID = try XCTUnwrap(firstPresentation.workspace.terminal1.processIdentifier)
        let secondPID = try XCTUnwrap(firstPresentation.workspace.terminal2.processIdentifier)
        let firstView = firstPresentation.workspace.terminal1.terminalView
        let secondView = firstPresentation.workspace.terminal2.terminalView

        let repeated = await registry.ensureStarted(for: equivalentPath)
        XCTAssertTrue(repeated === firstPresentation)
        XCTAssertEqual(firstPresentation.workspace.terminal1.processIdentifier, firstPID)
        XCTAssertEqual(firstPresentation.workspace.terminal2.processIdentifier, secondPID)
        XCTAssertTrue(firstPresentation.workspace.terminal1.terminalView === firstView)
        XCTAssertTrue(firstPresentation.workspace.terminal2.terminalView === secondView)
        XCTAssertEqual(registry.retainedPresentations.count, 1)

        await registry.stopAll(gracePeriod: .milliseconds(250))
    }

    func testSwitchingBetweenPathsRetainsExactlyOnePairPerPath() async throws {
        let firstDirectory = try makeTemporaryDirectory(name: "first")
        let secondDirectory = try makeTemporaryDirectory(name: "second")
        defer {
            try? FileManager.default.removeItem(at: firstDirectory)
            try? FileManager.default.removeItem(at: secondDirectory)
        }

        let registry = WorkspaceTerminalRegistry(environment: ["SHELL": "/bin/sh"])
        let firstStarted = await registry.ensureStarted(for: firstDirectory)
        let first = try XCTUnwrap(firstStarted)
        let firstPID = try XCTUnwrap(first.workspace.terminal1.processIdentifier)
        let secondStarted = await registry.ensureStarted(for: secondDirectory)
        let second = try XCTUnwrap(secondStarted)
        let secondPID = try XCTUnwrap(second.workspace.terminal1.processIdentifier)
        let returnedStarted = await registry.ensureStarted(for: firstDirectory)
        let returned = try XCTUnwrap(returnedStarted)

        XCTAssertFalse(first === second)
        XCTAssertTrue(returned === first)
        XCTAssertNotEqual(firstPID, secondPID)
        XCTAssertEqual(registry.retainedPresentations.count, 2)
        XCTAssertEqual(first.workspace.terminal1.processIdentifier, firstPID)
        XCTAssertEqual(first.workspace.terminal2.state, .ready)
        XCTAssertEqual(second.workspace.terminal1.processIdentifier, secondPID)
        XCTAssertEqual(second.workspace.terminal2.state, .ready)

        await registry.stopAll(gracePeriod: .milliseconds(250))
    }

    private func makeTemporaryDirectory(name: String) throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode registry \(name) \(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
}
