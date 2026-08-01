import AppKit
import Combine
import SwiftUI

/// A controlled dependency-gate surface. This is intentionally not the later Pi/Commands product UI.
@MainActor
struct TerminalFeasibilityView: View {
    @StateObject private var model: TerminalFeasibilityModel

    init(workingDirectory: URL) {
        _model = StateObject(wrappedValue: TerminalFeasibilityModel(workingDirectory: workingDirectory))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("SwiftTerm feasibility host")
                .font(.title2)
                .fontWeight(.semibold)
            Text("Controlled dependency validation only — one retained shell, two AppKit host regions.")
                .foregroundStyle(.secondary)

            HStack {
                Button(model.moveButtonTitle) {
                    model.moveTerminal()
                }
                .accessibilityLabel(model.moveButtonTitle)
                .accessibilityIdentifier("terminal-feasibility-move")
                .disabled(!model.isReady)

                Button("Focus terminal") {
                    model.focusTerminal()
                }
                .accessibilityLabel("Focus terminal")
                .accessibilityIdentifier("terminal-feasibility-focus")
                .disabled(!model.isReady)

                Button("Stop terminal") {
                    Task { await model.stop() }
                }
                .accessibilityLabel("Stop terminal")
                .accessibilityIdentifier("terminal-feasibility-stop")
                .disabled(!model.canStop)
            }

            Text(model.status)
                .accessibilityLabel("Terminal status")
                .accessibilityValue(model.status)
            Text(model.identitySummary)
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .accessibilityLabel("Terminal process identity")
                .accessibilityValue(model.identitySummary)

            terminalRegion(.main)
            terminalRegion(.bottom)
        }
        .padding()
        .frame(minWidth: 760, minHeight: 640)
        .task {
            await model.start()
        }
        .onDisappear {
            Task { await model.stop() }
        }
    }

    @ViewBuilder
    private func terminalRegion(_ placement: TerminalFeasibilityModel.Placement) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(placement.title)
                .font(.headline)

            Group {
                if model.placement == placement, model.isReady {
                    RetainedTerminalHost(
                        session: model.session,
                        accessibilityLabel: "SwiftTerm feasibility terminal in the \(placement.title.lowercased())"
                    )
                } else {
                    Text(model.isReady ? "Terminal retained in the other region." : "Terminal is not ready.")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 210)
            .background(.background.secondary)
            .clipShape(.rect(cornerRadius: 6))
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(placement.title)
    }
}

@MainActor
private final class TerminalFeasibilityModel: ObservableObject {
    enum Placement {
        case main
        case bottom

        var title: String {
            switch self {
            case .main: "Main host region"
            case .bottom: "Bottom host region"
            }
        }
    }

    let session: TerminalSession

    @Published private(set) var placement: Placement = .main
    @Published private(set) var status = "Waiting to start the controlled shell."
    @Published private(set) var identitySummary = "No process identity available."

    private var didStart = false
    private var didStop = false

    init(workingDirectory: URL) {
        session = TerminalSession(workingDirectory: workingDirectory)
    }

    var isReady: Bool {
        session.state == .ready && !didStop
    }

    var canStop: Bool {
        didStart && !didStop
    }

    var moveButtonTitle: String {
        switch placement {
        case .main: "Move terminal to bottom"
        case .bottom: "Move terminal to main"
        }
    }

    func start() async {
        guard !didStart else {
            return
        }
        didStart = true
        status = "Starting controlled login shell."

        do {
            try await session.start()
            status = "Terminal ready."
            identitySummary = "PID \(session.processIdentifier ?? -1), process group \(session.processGroupIdentifier ?? -1), PTY \(session.pseudoTerminalDescriptor ?? -1)"
        } catch {
            status = "Terminal failed to start: \(safeDescription(for: error))"
            identitySummary = "No process identity available."
        }
    }

    func moveTerminal() {
        guard isReady else {
            return
        }
        placement = placement == .main ? .bottom : .main
        status = "Terminal moved to the \(placement.title.lowercased()) without replacing its session."
    }

    func focusTerminal() {
        status = session.focusTerminalView()
            ? "Terminal focused in the \(placement.title.lowercased())."
            : "Terminal focus could not be moved because its host window is unavailable."
    }

    func stop() async {
        guard didStart, !didStop else {
            return
        }
        didStop = true
        status = "Stopping controlled terminal session."
        let result = await session.stop(gracePeriod: .milliseconds(500))
        status = "Terminal stop result: \(description(for: result))."
        identitySummary = "No process identity available."
    }

    private func safeDescription(for error: Error) -> String {
        switch error {
        case TerminalSessionError.inaccessibleWorkingDirectory:
            "working directory is inaccessible"
        case TerminalSessionError.unavailableShell:
            "no executable local shell is available"
        case TerminalSessionError.launchFailed:
            "local shell launch failed"
        default:
            "unexpected local launch error"
        }
    }

    private func description(for result: TerminalShutdownResult) -> String {
        switch result {
        case .graceful: "graceful"
        case .forced: "forced"
        case .alreadyStopped: "already stopped"
        case .failed: "failed"
        }
    }
}

@MainActor
private struct RetainedTerminalHost: NSViewRepresentable {
    let session: TerminalSession
    let accessibilityLabel: String

    func makeNSView(context: Context) -> NSView {
        let host = NSView(frame: .zero)
        attach(to: host)
        return host
    }

    func updateNSView(_ host: NSView, context: Context) {
        attach(to: host)
    }

    private func attach(to host: NSView) {
        let terminalView = session.terminalView
        terminalView.setAccessibilityElement(true)
        terminalView.setAccessibilityRole(.textArea)
        terminalView.setAccessibilityLabel(accessibilityLabel)
        terminalView.setAccessibilityHelp("Interactive local shell used only for the SwiftTerm dependency gate.")
        session.attachTerminalView(to: host)
    }
}

enum TerminalFeasibilityLaunch {
    static let directoryArgument = "--terminal-feasibility-directory"

    static func workingDirectory(arguments: [String] = ProcessInfo.processInfo.arguments) -> URL? {
        guard
            let optionIndex = arguments.firstIndex(of: directoryArgument),
            arguments.indices.contains(optionIndex + 1)
        else {
            return nil
        }

        return URL(fileURLWithPath: arguments[optionIndex + 1], isDirectory: true)
    }
}
