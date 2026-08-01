import AppKit
import Combine
@testable import gabCode
import XCTest

@MainActor
final class TerminalWorkspaceTests: XCTestCase {
    func testWorkspaceStartsIndependentSessionsInTheSameControlledDirectory() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }

        let workspace = TerminalWorkspace(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        try await workspace.start()

        let terminal1 = workspace.terminal1
        let terminal2 = workspace.terminal2
        XCTAssertEqual(terminal1.state, .ready)
        XCTAssertEqual(terminal2.state, .ready)
        XCTAssertNotEqual(terminal1.processIdentifier, terminal2.processIdentifier)
        XCTAssertNotEqual(terminal1.processGroupIdentifier, terminal2.processGroupIdentifier)
        XCTAssertNotEqual(terminal1.pseudoTerminalDescriptor, terminal2.pseudoTerminalDescriptor)
        XCTAssertTrue(terminal1.terminalView !== terminal2.terminalView)

        let firstMarker = workspaceDirectory.appendingPathComponent("terminal 1 marker.txt")
        let secondMarker = workspaceDirectory.appendingPathComponent("terminal 2 marker.txt")
        try terminal1.send("printf first > 'terminal 1 marker.txt'\n")
        try terminal2.send("printf second > 'terminal 2 marker.txt'\n")
        try await waitForFile(firstMarker)
        try await waitForFile(secondMarker)
        XCTAssertEqual(try String(contentsOf: firstMarker, encoding: .utf8), "first")
        XCTAssertEqual(try String(contentsOf: secondMarker, encoding: .utf8), "second")
    }

    func testWorkspaceRetriesOneFailedSessionWithoutReplacingTheHealthySession() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        let delayedDirectory = workspaceDirectory.appendingPathComponent("becomes available later", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }

        let failedTerminal = TerminalSession(
            workingDirectory: delayedDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        let healthyTerminal = TerminalSession(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        let workspace = TerminalWorkspace(terminal1: failedTerminal, terminal2: healthyTerminal)
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        try await workspace.start()
        XCTAssertEqual(workspace.terminal1.state, .failed)
        let healthyProcessIdentifier = try XCTUnwrap(workspace.terminal2.processIdentifier)
        XCTAssertEqual(workspace.terminal2.state, .ready)

        try FileManager.default.createDirectory(at: delayedDirectory, withIntermediateDirectories: true)
        try await workspace.retry(.terminal1)

        XCTAssertEqual(workspace.terminal1.state, .ready)
        XCTAssertEqual(workspace.terminal2.processIdentifier, healthyProcessIdentifier)
        XCTAssertEqual(workspace.terminal2.state, .ready)
    }

    func testSessionReportsFallbackShellAfterRejectingMalformedConfiguredShell() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }
        let invalidShell = workspaceDirectory.appendingPathComponent("not a shell", isDirectory: true)
        try FileManager.default.createDirectory(at: invalidShell, withIntermediateDirectories: false)

        let session = TerminalSession(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": invalidShell.path]
        )
        defer { Task { await session.stop(gracePeriod: .milliseconds(250)) } }

        try await session.start()
        XCTAssertEqual(session.shellSelection, .fallback("/bin/zsh"))
    }

    func testWorkspacePublishesFallbackAndNaturalExitWithoutInterruptingItsPeer() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }
        let invalidShell = workspaceDirectory.appendingPathComponent("not a shell", isDirectory: true)
        try FileManager.default.createDirectory(at: invalidShell, withIntermediateDirectories: false)

        let first = TerminalSession(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": invalidShell.path]
        )
        let second = TerminalSession(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        let workspace = TerminalWorkspace(terminal1: first, terminal2: second)
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        let fallbackPublished = expectation(description: "fallback shell selection publishes")
        let naturalExitPublished = expectation(description: "natural exit publishes")
        var cancellables = Set<AnyCancellable>()
        first.$shellSelection
            .compactMap { $0 }
            .sink { selection in
                if selection == .fallback("/bin/zsh") {
                    fallbackPublished.fulfill()
                }
            }
            .store(in: &cancellables)
        first.$state
            .sink { state in
                if state == .exited {
                    naturalExitPublished.fulfill()
                }
            }
            .store(in: &cancellables)

        try await workspace.start()
        await fulfillment(of: [fallbackPublished], timeout: 1)
        XCTAssertEqual(second.state, .ready)

        try first.send("exit\n")
        await fulfillment(of: [naturalExitPublished], timeout: 5)
        XCTAssertEqual(first.state, .exited)
        XCTAssertEqual(second.state, .ready)
    }

    func testNaturalExitOfOneSessionDoesNotStopTheOther() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }

        let workspace = TerminalWorkspace(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        defer { Task { await workspace.stop(gracePeriod: .milliseconds(250)) } }

        try await workspace.start()
        try workspace.terminal1.send("exit\n")
        try await waitForSessionState(workspace.terminal1, .exited)

        XCTAssertEqual(workspace.terminal2.state, .ready)
        let secondMarker = workspaceDirectory.appendingPathComponent("remaining terminal marker.txt")
        try workspace.terminal2.send("printf alive > 'remaining terminal marker.txt'\n")
        try await waitForFile(secondMarker)
        XCTAssertEqual(try String(contentsOf: secondMarker, encoding: .utf8), "alive")
    }

    func testStopRacingStartupCannotLeaveAReadyHiddenSession() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }
        var observedStartingState = false

        for _ in 0..<10 {
            let session = TerminalSession(
                workingDirectory: workspaceDirectory,
                environment: ["SHELL": "/bin/sh"]
            )
            let startTask = Task { try await session.start() }

            while session.state == .idle {
                await Task.yield()
            }

            if session.state == .starting {
                observedStartingState = true
                _ = await session.stop(gracePeriod: .milliseconds(250))
                _ = try? await startTask.value
                let stateAfterRace = session.state
                if stateAfterRace == .ready {
                    _ = await session.stop(gracePeriod: .milliseconds(250))
                }
                XCTAssertEqual(
                    stateAfterRace,
                    .closed,
                    "A stop accepted during launch must finish bounded cleanup before returning."
                )
                XCTAssertNil(session.processIdentifier)
                XCTAssertNil(session.processGroupIdentifier)
                XCTAssertNil(session.pseudoTerminalDescriptor)
            } else {
                _ = try? await startTask.value
                _ = await session.stop(gracePeriod: .milliseconds(250))
            }
        }

        XCTAssertTrue(observedStartingState, "The stress run did not exercise the startup cleanup race.")
    }

    func testConcurrentWorkspaceStopsLeaveBothSessionsClosed() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }

        let workspace = TerminalWorkspace(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        try await workspace.start()

        async let firstStop: Void = workspace.stop(gracePeriod: .milliseconds(250))
        async let secondStop: Void = workspace.stop(gracePeriod: .milliseconds(250))
        _ = await (firstStop, secondStop)

        XCTAssertEqual(workspace.terminal1.state, .closed)
        XCTAssertEqual(workspace.terminal2.state, .closed)
    }

    func testWorkspaceStopIsIdempotentAfterBoundedHighOutput() async throws {
        let workspaceDirectory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspaceDirectory) }

        let workspace = TerminalWorkspace(
            workingDirectory: workspaceDirectory,
            environment: ["SHELL": "/bin/sh"]
        )
        try await workspace.start()

        let started = workspaceDirectory.appendingPathComponent("output flood started.txt")
        try workspace.terminal1.send(
            "printf ready > 'output flood started.txt'; while :; do printf 0123456789abcdef; done\n"
        )
        try await waitForFile(started)

        let clock = ContinuousClock()
        let beganStopping = clock.now
        await workspace.stop(gracePeriod: .milliseconds(250))
        let elapsed = beganStopping.duration(to: clock.now)
        XCTAssertLessThan(elapsed, .seconds(4), "Output backpressure must not prevent bounded cleanup.")
        XCTAssertEqual(workspace.terminal1.state, .closed)
        XCTAssertEqual(workspace.terminal2.state, .closed)

        await workspace.stop(gracePeriod: .milliseconds(250))
        XCTAssertEqual(workspace.terminal1.state, .closed)
        XCTAssertEqual(workspace.terminal2.state, .closed)
    }

    private func makeTemporaryDirectory() throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode dual terminal workspace ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func waitForSessionState(
        _ session: TerminalSession,
        _ expectedState: TerminalSessionState
    ) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while session.state != expectedState && clock.now < deadline {
            try await clock.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(session.state, expectedState, "Expected terminal session to report \(expectedState).")
    }

    private func waitForFile(_ file: URL) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while !FileManager.default.fileExists(atPath: file.path) && clock.now < deadline {
            try await clock.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(
            FileManager.default.fileExists(atPath: file.path),
            "Expected controlled shell command to create \(file.lastPathComponent)."
        )
    }
}
