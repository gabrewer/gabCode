import AppKit
import Combine
import Foundation

@MainActor
final class WorkspaceTerminalRegistry: ObservableObject {
    @Published private(set) var presentationsByPath: [URL: TerminalWorkspacePresentation] = [:]
    private let environment: [String: String]
    private let font: NSFont

    init(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        font: NSFont = NSFont.monospacedSystemFont(ofSize: TerminalFontSelection.defaultPointSize, weight: .regular)
    ) {
        self.environment = environment
        self.font = font
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

    func stopAll(gracePeriod: Duration) async {
        for presentation in retainedPresentations {
            _ = await presentation.workspace.stopResults(gracePeriod: gracePeriod)
        }
    }
}
