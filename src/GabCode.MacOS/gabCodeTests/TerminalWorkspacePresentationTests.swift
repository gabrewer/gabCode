import AppKit
@testable import gabCode
import XCTest

@MainActor
final class TerminalWorkspacePresentationTests: XCTestCase {
    func testLaunchReadsOnlyTheExplicitTerminalDirectoryArgument() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        XCTAssertNil(TerminalWorkspaceLaunch.workingDirectory(arguments: ["gabCode"]))
        XCTAssertNil(TerminalWorkspaceLaunch.workingDirectory(arguments: ["gabCode", "--terminal-directory"]))
        XCTAssertEqual(
            TerminalWorkspaceLaunch.workingDirectory(
                arguments: ["gabCode", "--terminal-directory", directory.path]
            ),
            directory
        )
    }

    func testPresentationSwapsRetainedSessionsWithoutReplacingTheirIdentity() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let workspace = TerminalWorkspace(workingDirectory: directory, environment: ["SHELL": "/bin/sh"])
        let presentation = TerminalWorkspacePresentation(workspace: workspace, workingDirectory: directory)
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        await presentation.start()
        let terminal1PID = try XCTUnwrap(workspace.terminal1.processIdentifier)
        let terminal2PID = try XCTUnwrap(workspace.terminal2.processIdentifier)
        XCTAssertEqual(presentation.mainTerminal, .terminal1)
        XCTAssertEqual(presentation.bottomTerminal, .terminal2)

        presentation.swapTerminals()

        XCTAssertEqual(presentation.mainTerminal, .terminal2)
        XCTAssertEqual(presentation.bottomTerminal, .terminal1)
        XCTAssertEqual(workspace.terminal1.processIdentifier, terminal1PID)
        XCTAssertEqual(workspace.terminal2.processIdentifier, terminal2PID)
        XCTAssertEqual(workspace.terminal1.state, .ready)
        XCTAssertEqual(workspace.terminal2.state, .ready)
    }

    func testPresentationMakesFallbackAndNaturalExitTerminalLocal() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let invalidShell = directory.appendingPathComponent("not a shell", isDirectory: true)
        try FileManager.default.createDirectory(at: invalidShell, withIntermediateDirectories: false)

        let terminal1 = TerminalSession(workingDirectory: directory, environment: ["SHELL": invalidShell.path])
        let terminal2 = TerminalSession(workingDirectory: directory, environment: ["SHELL": "/bin/sh"])
        let workspace = TerminalWorkspace(terminal1: terminal1, terminal2: terminal2)
        let presentation = TerminalWorkspacePresentation(workspace: workspace, workingDirectory: directory)
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        await presentation.start()
        XCTAssertEqual(
            presentation.state(for: .terminal1),
            .ready(shellSelection: .fallback("/bin/zsh"))
        )
        XCTAssertEqual(
            presentation.state(for: .terminal2),
            .ready(shellSelection: .configured("/bin/sh"))
        )
        XCTAssertEqual(presentation.activeTerminalCount, 2)

        try terminal1.send("exit\n")
        try await waitForPresentationState(presentation, terminal: .terminal1, .exited)
        XCTAssertEqual(presentation.state(for: .terminal2), .ready(shellSelection: .configured("/bin/sh")))
        XCTAssertEqual(presentation.activeTerminalCount, 1)
    }

    private func makeTemporaryDirectory() throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode retained workspace ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func waitForPresentationState(
        _ presentation: TerminalWorkspacePresentation,
        terminal: TerminalWorkspaceTerminal,
        _ expectedState: TerminalWorkspacePresentationState
    ) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while presentation.state(for: terminal) != expectedState && clock.now < deadline {
            try await clock.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(presentation.state(for: terminal), expectedState)
    }
}
