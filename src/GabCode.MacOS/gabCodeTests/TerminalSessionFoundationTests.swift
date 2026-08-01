import AppKit
@testable import gabCode
import XCTest

@MainActor
final class TerminalSessionFoundationTests: XCTestCase {
    func testSessionRejectsAnInaccessibleWorkingDirectoryBeforeLaunching() async throws {
        let missingDirectory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("gabCode missing terminal workspace", isDirectory: true)

        let session = TerminalSession(
            workingDirectory: missingDirectory,
            environment: ["SHELL": "/bin/sh"]
        )

        await XCTAssertThrowsErrorAsync(try await session.start()) { error in
            XCTAssertEqual(error as? TerminalSessionError, .inaccessibleWorkingDirectory(missingDirectory))
        }
        XCTAssertEqual(session.state, .failed)
        XCTAssertNil(session.processIdentifier)
        XCTAssertNil(session.processGroupIdentifier)
        XCTAssertNil(session.pseudoTerminalDescriptor)
    }

    func testSessionRejectsAWorkingDirectoryWithoutSearchPermissionBeforeLaunching() async throws {
        let workspace = try makeTemporaryDirectory()
        defer {
            try? FileManager.default.setAttributes(
                [.posixPermissions: 0o700],
                ofItemAtPath: workspace.path
            )
            try? FileManager.default.removeItem(at: workspace)
        }
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o400],
            ofItemAtPath: workspace.path
        )
        XCTAssertTrue(FileManager.default.isReadableFile(atPath: workspace.path))
        XCTAssertFalse(FileManager.default.isExecutableFile(atPath: workspace.path))

        let session = TerminalSession(
            workingDirectory: workspace,
            environment: ["SHELL": "/bin/sh"]
        )

        do {
            try await session.start()
            _ = await session.stop(gracePeriod: .milliseconds(250))
            XCTFail("A directory without search permission must not launch a shell in an inherited directory.")
        } catch {
            XCTAssertEqual(error as? TerminalSessionError, .inaccessibleWorkingDirectory(workspace))
        }
        XCTAssertNil(session.processIdentifier)
        XCTAssertNil(session.processGroupIdentifier)
        XCTAssertNil(session.pseudoTerminalDescriptor)
    }

    func testSessionFallsBackWhenShellEnvironmentNamesADirectory() async throws {
        let workspace = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspace) }
        let invalidShell = workspace.appendingPathComponent("not an executable shell", isDirectory: true)
        try FileManager.default.createDirectory(at: invalidShell, withIntermediateDirectories: false)

        let session = TerminalSession(
            workingDirectory: workspace,
            environment: ["SHELL": invalidShell.path]
        )

        do {
            try await session.start()
            try session.send("printf fallback > 'shell fallback proof.txt'\n")
            try await waitForFile(workspace.appendingPathComponent("shell fallback proof.txt"))
        } catch {
            _ = await session.stop(gracePeriod: .milliseconds(250))
            throw error
        }
        _ = await session.stop(gracePeriod: .milliseconds(250))
    }

    func testSessionRetainsItsTerminalViewAndProcessIdentityAcrossRehosting() async throws {
        let workspace = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspace) }

        let session = TerminalSession(
            workingDirectory: workspace,
            environment: ["SHELL": "/bin/sh"]
        )
        defer { Task { await session.stop(gracePeriod: .milliseconds(250)) } }

        try await session.start()

        let originalView = session.terminalView
        let originalProcessIdentifier = try XCTUnwrap(session.processIdentifier)
        let originalProcessGroupIdentifier = try XCTUnwrap(session.processGroupIdentifier)
        let originalPseudoTerminalDescriptor = try XCTUnwrap(session.pseudoTerminalDescriptor)
        XCTAssertEqual(
            getsid(originalProcessIdentifier),
            originalProcessIdentifier,
            "A terminal shell must own its own Unix session before session-wide cleanup is permitted."
        )

        let mainHost = NSView(frame: .zero)
        let bottomHost = NSView(frame: .zero)
        session.attachTerminalView(to: mainHost)
        XCTAssertTrue(originalView.superview === mainHost)

        session.attachTerminalView(to: bottomHost)
        XCTAssertTrue(originalView.superview === bottomHost)
        XCTAssertTrue(session.terminalView === originalView)
        XCTAssertEqual(session.processIdentifier, originalProcessIdentifier)
        XCTAssertEqual(session.processGroupIdentifier, originalProcessGroupIdentifier)
        XCTAssertEqual(session.pseudoTerminalDescriptor, originalPseudoTerminalDescriptor)
        XCTAssertEqual(session.state, .ready)

        let marker = workspace.appendingPathComponent("cwd proof.txt")
        try session.send("printf retained > 'cwd proof.txt'\n")
        try await waitForFile(marker)

        let childIdentifierFile = workspace.appendingPathComponent("child pid.txt")
        try session.send("sleep 30 & printf '%s' $! > 'child pid.txt'\n")
        try await waitForFile(childIdentifierFile)
        let childIdentifier = try XCTUnwrap(
            pid_t(String(contentsOf: childIdentifierFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))
        )
        XCTAssertEqual(kill(childIdentifier, 0), 0, "Expected the controlled descendant to be alive before cleanup.")

        let shutdown = await session.stop(gracePeriod: .milliseconds(250))
        XCTAssertEqual(shutdown, .forced, "The login shell did not exit within the bounded graceful interval; process-group escalation must complete cleanup.")
        try await waitForProcessExit(childIdentifier)
        XCTAssertEqual(session.state, .closed)
        XCTAssertNil(session.pseudoTerminalDescriptor)
    }

    func testSessionObservesNaturalShellExitAndReleasesOnStop() async throws {
        let workspace = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspace) }

        let session = TerminalSession(
            workingDirectory: workspace,
            environment: ["SHELL": "/bin/sh"]
        )

        try await session.start()
        try session.send("exit\n")
        try await waitForSessionState(session, .exited)

        XCTAssertEqual(session.state, .exited)
        let shutdown = await session.stop(gracePeriod: .milliseconds(250))
        XCTAssertEqual(shutdown, .graceful)
        XCTAssertEqual(session.state, .closed)
        XCTAssertNil(session.pseudoTerminalDescriptor)
    }

    func testStopTerminatesAReparentedTerminalDescendant() async throws {
        let workspace = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: workspace) }

        let session = TerminalSession(
            workingDirectory: workspace,
            environment: ["SHELL": "/bin/sh"]
        )
        defer { Task { await session.stop(gracePeriod: .milliseconds(250)) } }

        try await session.start()
        let shellIdentifier = try XCTUnwrap(session.processIdentifier)
        let childIdentifierFile = workspace.appendingPathComponent("reparented child pid.txt")
        try session.send(
            "/bin/sh -c '/bin/sleep 30 & printf \"%s\" $! > \"reparented child pid.txt\"' &\n"
        )
        try await waitForFile(childIdentifierFile)
        let childIdentifier = try XCTUnwrap(
            pid_t(String(contentsOf: childIdentifierFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))
        )
        defer { _ = kill(childIdentifier, SIGKILL) }

        try await waitForParentChange(of: childIdentifier, from: shellIdentifier)
        XCTAssertEqual(kill(childIdentifier, 0), 0, "Expected the reparented terminal descendant to be alive before cleanup.")

        _ = await session.stop(gracePeriod: .milliseconds(250))
        try await waitForProcessExit(childIdentifier)
    }

    private func makeTemporaryDirectory() throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode terminal workspace ünicode", isDirectory: true)
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

    private func waitForProcessExit(_ processIdentifier: pid_t) async throws {
        let exited = expectation(
            for: NSPredicate { _, _ in kill(processIdentifier, 0) == -1 && errno == ESRCH },
            evaluatedWith: nil
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [exited], timeout: 5),
            .completed,
            "Expected controlled descendant \(processIdentifier) to be gone after process-group cleanup."
        )
    }

    private func waitForParentChange(of processIdentifier: pid_t, from originalParent: pid_t) async throws {
        let reparented = expectation(
            for: NSPredicate { [self] _, _ in
                guard let parent = parentProcessIdentifier(of: processIdentifier) else {
                    return false
                }
                return parent != originalParent
            },
            evaluatedWith: nil
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [reparented], timeout: 5),
            .completed,
            "Expected controlled descendant \(processIdentifier) to reparent before terminal cleanup."
        )
    }

    private func parentProcessIdentifier(of processIdentifier: pid_t) -> pid_t? {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/ps")
        process.arguments = ["-o", "ppid=", "-p", String(processIdentifier)]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        do {
            try process.run()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else {
                return nil
            }
            let data = output.fileHandleForReading.readDataToEndOfFile()
            guard let value = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) else {
                return nil
            }
            return pid_t(value)
        } catch {
            return nil
        }
    }

    private func waitForFile(_ file: URL) async throws {
        let exists = expectation(
            for: NSPredicate { _, _ in FileManager.default.fileExists(atPath: file.path) },
            evaluatedWith: nil
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [exists], timeout: 5),
            .completed,
            "Expected controlled shell command to create \(file.lastPathComponent)."
        )
    }
}

@MainActor
private func XCTAssertThrowsErrorAsync<T>(
    _ expression: @autoclosure () async throws -> T,
    _ handler: (Error) -> Void
) async {
    do {
        _ = try await expression()
        XCTFail("Expected an error.")
    } catch {
        handler(error)
    }
}
