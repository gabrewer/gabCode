import AppKit
import Combine
import Foundation

@MainActor
final class WorkspaceTerminalRegistry: ObservableObject {
    @Published private(set) var presentationsByPath: [URL: TerminalWorkspacePresentation] = [:]
    private let environment: [String: String]
    private let font: NSFont
    @Published private var closingPaths: Set<URL> = []

    init(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        font: NSFont = NSFont.monospacedSystemFont(ofSize: TerminalFontSelection.defaultPointSize, weight: .regular)
    ) {
        self.environment = environment
        self.font = font
    }

    var isClosing: Bool {
        !closingPaths.isEmpty
    }

    var retainedPaths: [URL] {
        Array(presentationsByPath.keys)
    }

    var retainedPresentations: [TerminalWorkspacePresentation] {
        Array(presentationsByPath.values)
    }

    func presentation(for path: URL) -> TerminalWorkspacePresentation {
        let normalizedPath = path.standardizedFileURL
        if let presentation = presentationsByPath[normalizedPath] {
            return presentation
        }
        let presentation = TerminalWorkspacePresentation(
            workspace: TerminalWorkspace(
                workingDirectory: normalizedPath,
                environment: environment,
                font: font
            ),
            workingDirectory: normalizedPath
        )
        presentationsByPath[normalizedPath] = presentation
        return presentation
    }

    func existingPresentation(for path: URL) -> TerminalWorkspacePresentation? {
        presentationsByPath[path.standardizedFileURL]
    }

    @discardableResult
    func ensureStarted(for path: URL) async -> TerminalWorkspacePresentation? {
        let presentation = presentation(for: path)
        await presentation.start()
        return presentation
    }

    func remove(_ path: URL) {
        presentationsByPath.removeValue(forKey: path.standardizedFileURL)
    }

    /// Stops and forgets only the presentation captured before confirmation.
    /// A missing presentation is a successful no-op and must never start terminals.
    func close(
        path: URL,
        expectedPresentation: TerminalWorkspacePresentation?,
        gracePeriod: Duration
    ) async -> Bool {
        let normalizedPath = path.standardizedFileURL
        guard !closingPaths.contains(normalizedPath) else { return false }
        guard let expectedPresentation else { return presentationsByPath[normalizedPath] == nil }
        guard presentationsByPath[normalizedPath] === expectedPresentation else { return false }

        closingPaths.insert(normalizedPath)
        expectedPresentation.setMutationLocked(true)
        defer {
            closingPaths.remove(normalizedPath)
            if presentationsByPath[normalizedPath] === expectedPresentation {
                expectedPresentation.setMutationLocked(false)
            }
        }

        let results = await expectedPresentation.workspace.stopResults(gracePeriod: gracePeriod)
        guard results.allSatisfy({ $0 != .failed }) else { return false }
        guard presentationsByPath[normalizedPath] === expectedPresentation else { return false }
        presentationsByPath.removeValue(forKey: normalizedPath)
        return true
    }

    func stopAll(gracePeriod: Duration) async {
        for presentation in retainedPresentations {
            _ = await presentation.workspace.stopResults(gracePeriod: gracePeriod)
        }
    }
}
