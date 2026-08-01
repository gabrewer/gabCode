import Combine
import Darwin
import Foundation

enum TerminalWorkspacePresentationState: Equatable {
    case idle
    case starting
    case ready(shellSelection: TerminalShellSelection)
    case failed
    case exited
    case closing
    case closed
}

@MainActor
final class TerminalWorkspacePresentation: ObservableObject {
    let workspace: TerminalWorkspace
    let workingDirectory: URL

    @Published private(set) var mainTerminal: TerminalWorkspaceTerminal = .terminal1
    private var cancellables: Set<AnyCancellable> = []
    private var didStart = false

    var bottomTerminal: TerminalWorkspaceTerminal {
        mainTerminal == .terminal1 ? .terminal2 : .terminal1
    }

    var activeTerminalCount: Int {
        [workspace.terminal1, workspace.terminal2].filter(\.requiresCleanup).count
    }

    init(workspace: TerminalWorkspace, workingDirectory: URL) {
        self.workspace = workspace
        self.workingDirectory = workingDirectory

        workspace.terminal1.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
        workspace.terminal2.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
    }

    convenience init(workingDirectory: URL) {
        self.init(
            workspace: TerminalWorkspace(workingDirectory: workingDirectory),
            workingDirectory: workingDirectory
        )
    }

    func start() async {
        guard !didStart else {
            return
        }
        didStart = true
        try? await workspace.start()
    }

    func swapTerminals() {
        mainTerminal = bottomTerminal
        DispatchQueue.main.async { [weak self] in
            self?.focusMainTerminal()
        }
    }

    func focus(_ terminal: TerminalWorkspaceTerminal) {
        _ = session(for: terminal).focusTerminalView()
    }

    func focusMainTerminal() {
        focus(mainTerminal)
    }

    func retry(_ terminal: TerminalWorkspaceTerminal) async {
        try? await workspace.retry(terminal)
    }

    func state(for terminal: TerminalWorkspaceTerminal) -> TerminalWorkspacePresentationState {
        let session = session(for: terminal)
        switch session.state {
        case .idle:
            return .idle
        case .starting:
            return .starting
        case .ready:
            guard let shellSelection = session.shellSelection else {
                return .failed
            }
            return .ready(shellSelection: shellSelection)
        case .failed:
            return .failed
        case .exited:
            return .exited
        case .closing:
            return .closing
        case .closed:
            return .closed
        }
    }

    func session(for terminal: TerminalWorkspaceTerminal) -> TerminalSession {
        switch terminal {
        case .terminal1:
            workspace.terminal1
        case .terminal2:
            workspace.terminal2
        }
    }
}

enum TerminalWorkspaceLaunch {
    static let directoryArgument = "--terminal-directory"

    static func workingDirectory(arguments: [String] = ProcessInfo.processInfo.arguments) -> URL? {
        guard
            let optionIndex = arguments.firstIndex(of: directoryArgument),
            arguments.indices.contains(optionIndex + 1)
        else {
            return nil
        }

        let path = arguments[optionIndex + 1]
        guard path.hasPrefix("/"), !path.isEmpty else {
            return nil
        }

        let directory = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL
        var isDirectory: ObjCBool = false
        guard
            FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDirectory),
            isDirectory.boolValue,
            access(directory.path, R_OK | X_OK) == 0
        else {
            return nil
        }
        return directory
    }
}
