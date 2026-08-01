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

    func testPresentationShowsCleanupFailureAndRetainsActiveOwnership() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let session = TerminalSession(
            workingDirectory: directory,
            environment: ["SHELL": "/bin/sh"],
            processGroupSignaler: { _, _ in }
        )
        let workspace = TerminalWorkspace(terminal1: session, terminal2: TerminalSession(workingDirectory: directory, environment: ["SHELL": "/bin/sh"]))
        let presentation = TerminalWorkspacePresentation(workspace: workspace, workingDirectory: directory)
        defer {
            if let processGroup = session.processGroupIdentifier {
                _ = kill(-processGroup, SIGKILL)
            }
            Task { await workspace.stop(gracePeriod: .milliseconds(10)) }
        }

        try await session.start()
        try session.send("trap '' HUP TERM; while :; do sleep 60; done\n")
        let shutdown = await session.stop(gracePeriod: .milliseconds(10))
        XCTAssertEqual(shutdown, .failed)

        XCTAssertEqual(presentation.state(for: .terminal1), .cleanupFailed)
        XCTAssertEqual(presentation.activeTerminalCount, 1)
    }

    func testMutationLockDisablesTerminalCommandsWithoutReplacingSessions() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let workspace = TerminalWorkspace(workingDirectory: directory, environment: ["SHELL": "/bin/sh"])
        let presentation = TerminalWorkspacePresentation(workspace: workspace, workingDirectory: directory)
        let router = TerminalCommandRouter.shared
        defer {
            router.disconnect(presentation)
            Task { await workspace.stop(gracePeriod: .milliseconds(250)) }
        }

        await presentation.start()
        let terminal1PID = try XCTUnwrap(workspace.terminal1.processIdentifier)
        router.connect(presentation)
        presentation.setMutationLocked(true)

        XCTAssertTrue(presentation.isMutationLocked)
        XCTAssertFalse(router.isAvailable)
        router.swapTerminals()
        XCTAssertEqual(presentation.mainTerminal, .terminal1)
        XCTAssertEqual(workspace.terminal1.processIdentifier, terminal1PID)

        presentation.setMutationLocked(false)
        XCTAssertTrue(router.isAvailable)
        router.swapTerminals()
        XCTAssertEqual(presentation.mainTerminal, .terminal2)
        XCTAssertEqual(workspace.terminal1.processIdentifier, terminal1PID)
    }

    func testExitedRootsWithLiveDescendantsRemainActiveUntilOwnedCleanupCompletes() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let workspace = TerminalWorkspace(workingDirectory: directory, environment: ["SHELL": "/bin/sh"])
        let presentation = TerminalWorkspacePresentation(workspace: workspace, workingDirectory: directory)
        var descendants: [pid_t] = []
        defer {
            descendants.forEach { _ = kill($0, SIGKILL) }
            Task { await workspace.stop(gracePeriod: .milliseconds(250)) }
        }

        await presentation.start()
        for (terminal, fileName) in [
            (workspace.terminal1, "terminal 1 surviving descendant.pid"),
            (workspace.terminal2, "terminal 2 surviving descendant.pid"),
        ] {
            let identifierFile = directory.appendingPathComponent(fileName)
            try terminal.send(
                "(trap '' HUP TERM; while :; do sleep 60; done) & printf '%s' $! > '\(fileName)'; exit\n"
            )
            try await waitForFile(identifierFile)
            descendants.append(
                try XCTUnwrap(
                    pid_t(String(contentsOf: identifierFile, encoding: .utf8)
                        .trimmingCharacters(in: .whitespacesAndNewlines))
                )
            )
        }

        try await waitForPresentationState(presentation, terminal: .terminal1, .exited)
        try await waitForPresentationState(presentation, terminal: .terminal2, .exited)
        descendants.forEach {
            XCTAssertEqual(kill($0, 0), 0, "Expected the controlled descendant to survive its root shell exit.")
        }
        XCTAssertEqual(
            presentation.activeTerminalCount,
            2,
            "Exited roots with live owned descendants must still block close/quit cleanup."
        )

        let results = await workspace.stopResults(gracePeriod: .milliseconds(250))
        XCTAssertFalse(results.contains(.failed))
        for descendant in descendants {
            try await waitForProcessExit(descendant)
        }
        XCTAssertEqual(workspace.terminal1.state, .closed)
        XCTAssertEqual(workspace.terminal2.state, .closed)
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

    private func waitForFile(_ file: URL) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while !FileManager.default.fileExists(atPath: file.path) && clock.now < deadline {
            try await clock.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path), "Expected controlled shell command to create \(file.lastPathComponent).")
    }

    private func waitForProcessExit(_ processIdentifier: pid_t) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while kill(processIdentifier, 0) == 0 && clock.now < deadline {
            try await clock.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(kill(processIdentifier, 0), -1, "Expected controlled descendant \(processIdentifier) to be gone after owned cleanup.")
        XCTAssertEqual(errno, ESRCH)
    }
}
