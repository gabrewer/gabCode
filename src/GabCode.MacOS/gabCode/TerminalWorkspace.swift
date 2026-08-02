import AppKit
import Foundation

enum TerminalWorkspaceTerminal: Equatable {
    case terminal1
    case terminal2
}

enum TerminalWorkspaceError: Error, Equatable {
    case noTerminalStarted
}

/// Owns the two independent generic terminal sessions for one controlled directory.
/// Placement and SwiftUI/AppKit hosting remain outside this lifecycle coordinator.
@MainActor
final class TerminalWorkspace {
    let terminal1: TerminalSession
    let terminal2: TerminalSession

    init(
        workingDirectory: URL,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        font: NSFont = NSFont.monospacedSystemFont(ofSize: TerminalFontSelection.defaultPointSize, weight: .regular)
    ) {
        terminal1 = TerminalSession(workingDirectory: workingDirectory, environment: environment, font: font)
        terminal2 = TerminalSession(workingDirectory: workingDirectory, environment: environment, font: font)
    }

    init(terminal1: TerminalSession, terminal2: TerminalSession) {
        self.terminal1 = terminal1
        self.terminal2 = terminal2
    }

    func start() async throws {
        await start(terminal1)
        await start(terminal2)

        guard terminal1.state == .ready || terminal2.state == .ready else {
            throw TerminalWorkspaceError.noTerminalStarted
        }
    }

    func retry(_ terminal: TerminalWorkspaceTerminal) async throws {
        let session = session(for: terminal)
        guard session.state == .failed else {
            return
        }
        try await session.start()
    }

    func apply(font: NSFont) {
        terminal1.apply(font: font)
        terminal2.apply(font: font)
    }

    func stop(gracePeriod: Duration) async {
        _ = await stopResults(gracePeriod: gracePeriod)
    }

    func stopResults(gracePeriod: Duration) async -> [TerminalShutdownResult] {
        [
            await terminal1.stop(gracePeriod: gracePeriod),
            await terminal2.stop(gracePeriod: gracePeriod),
        ]
    }

    private func start(_ session: TerminalSession) async {
        do {
            try await session.start()
        } catch {
            // Terminal-local failure state is intentionally retained so the UI can retry only
            // that session while its peer remains usable.
        }
    }

    private func session(for terminal: TerminalWorkspaceTerminal) -> TerminalSession {
        switch terminal {
        case .terminal1:
            terminal1
        case .terminal2:
            terminal2
        }
    }
}
