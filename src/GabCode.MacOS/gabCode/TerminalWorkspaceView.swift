import AppKit
import SwiftUI

@MainActor
struct TerminalWorkspaceView: View {
    @EnvironmentObject private var fontPreference: TerminalFontPreferenceStore
    @StateObject private var presentation: TerminalWorkspacePresentation

    init(workingDirectory: URL, font: NSFont = NSFont.monospacedSystemFont(ofSize: TerminalFontSelection.defaultPointSize, weight: .regular)) {
        _presentation = StateObject(wrappedValue: TerminalWorkspacePresentation(workspace: TerminalWorkspace(workingDirectory: workingDirectory, font: font), workingDirectory: workingDirectory))
    }

    init(presentation: TerminalWorkspacePresentation) {
        _presentation = StateObject(wrappedValue: presentation)
    }

    var body: some View {
        VStack(spacing: 0) {
            workspaceHeader
            Divider()
            VSplitView {
                TerminalRegionView(
                    presentation: presentation,
                    terminal: presentation.mainTerminal,
                    placementName: "Main",
                    placementIdentifier: "main-terminal-label"
                )
                .frame(minHeight: 220, idealHeight: 390)
                .background(NativeSplitInitialPosition(mainFraction: 2.0 / 3.0))

                TerminalRegionView(
                    presentation: presentation,
                    terminal: presentation.bottomTerminal,
                    placementName: "Bottom",
                    placementIdentifier: "bottom-terminal-label"
                )
                .frame(minHeight: 140, idealHeight: 195)
            }
        }
        .frame(minWidth: 760, minHeight: 640)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("terminal-workspace")
        .background(WindowCloseInterceptor(presentation: presentation))
        .task {
            await presentation.start()
            await Task.yield()
            presentation.focusMainTerminal()
        }
        .onReceive(fontPreference.$effectiveSelection) { _ in
            presentation.apply(font: fontPreference.effectiveFont)
        }
        .onAppear {
            TerminalCommandRouter.shared.connect(presentation)
        }
        .onDisappear {
            TerminalCommandRouter.shared.disconnect(presentation)
        }
    }

    private var workspaceHeader: some View {
        HStack(spacing: 10) {
            Image(systemName: "folder")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(presentation.workingDirectory.path)
                .lineLimit(1)
                .truncationMode(.middle)
                .help(presentation.workingDirectory.path)
                .accessibilityLabel("Terminal directory")
                .accessibilityValue(presentation.workingDirectory.path)
                .accessibilityIdentifier("terminal-directory-path")
                .frame(maxWidth: .infinity, alignment: .leading)

            Button("Swap terminals") {
                presentation.swapTerminals()
            }
            .keyboardShortcut("t", modifiers: [.command, .shift])
            .disabled(presentation.isMutationLocked)
            .accessibilityIdentifier("swap-terminals")
        }
        .padding(.horizontal, 10)
        .frame(height: 38)
    }
}

@MainActor
private struct TerminalRegionView: View {
    @ObservedObject var presentation: TerminalWorkspacePresentation
    @ObservedObject private var session: TerminalSession

    let terminal: TerminalWorkspaceTerminal
    let placementName: String
    let placementIdentifier: String

    init(
        presentation: TerminalWorkspacePresentation,
        terminal: TerminalWorkspaceTerminal,
        placementName: String,
        placementIdentifier: String
    ) {
        self.presentation = presentation
        self.terminal = terminal
        self.placementName = placementName
        self.placementIdentifier = placementIdentifier
        _session = ObservedObject(wrappedValue: presentation.session(for: terminal))
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text("\(placementName): \(terminalTitle)")
                    .font(.callout.weight(.semibold))
                    .lineLimit(1)
                    .accessibilityIdentifier(placementIdentifier)

                Spacer(minLength: 6)

                Text(statusText)
                    .font(.caption)
                    .foregroundStyle(statusColor)
                    .lineLimit(1)
                    .accessibilityLabel("\(terminalTitle) status")
                    .accessibilityValue(statusText)

                if session.state == .failed && !session.requiresCleanup {
                    Button("Retry") {
                        Task { await presentation.retry(terminal) }
                    }
                    .controlSize(.small)
                    .disabled(presentation.isMutationLocked)
                    .accessibilityLabel("Retry \(terminalTitle)")
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 30)

            Group {
                switch session.state {
                case .ready, .exited, .closing:
                    RetainedTerminalHost(
                        session: session,
                        accessibilityLabel: "\(terminalTitle), \(placementName.lowercased()) region, \(statusText)",
                        accessibilityIdentifier: terminalHostIdentifier
                    )
                case .idle, .starting:
                    terminalPlaceholder("Starting local login shell…")
                case .failed:
                    if session.requiresCleanup {
                        terminalPlaceholder("Terminal cleanup could not be verified. Close or quit again to retry safely.")
                    } else {
                        terminalPlaceholder("This terminal failed to start. Retry only this terminal.")
                    }
                case .closed:
                    terminalPlaceholder("This terminal is closed.")
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .textBackgroundColor))
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(terminalTitle), \(placementName.lowercased()) terminal region")
        .accessibilityIdentifier(terminalIdentifier)
    }

    private var terminalTitle: String {
        switch terminal {
        case .terminal1: "Terminal 1"
        case .terminal2: "Terminal 2"
        }
    }

    private var terminalIdentifier: String {
        switch terminal {
        case .terminal1: "terminal-1-region"
        case .terminal2: "terminal-2-region"
        }
    }

    private var terminalHostIdentifier: String {
        switch terminal {
        case .terminal1: "terminal-1-host"
        case .terminal2: "terminal-2-host"
        }
    }

    private var statusText: String {
        switch presentation.state(for: terminal) {
        case .idle: "Waiting"
        case .starting: "Starting"
        case let .ready(shellSelection):
            switch shellSelection {
            case let .configured(path): "Ready · \(URL(fileURLWithPath: path).lastPathComponent)"
            case let .fallback(path): "Ready · fallback \(URL(fileURLWithPath: path).lastPathComponent)"
            }
        case .failed: "Failed"
        case .cleanupFailed: "Cleanup failed"
        case .exited: "Exited"
        case .closing: "Closing"
        case .closed: "Closed"
        }
    }

    private var statusColor: Color {
        switch session.state {
        case .failed: .red
        case .exited, .closing, .closed: .secondary
        case .idle, .starting, .ready: .primary
        }
    }

    private func terminalPlaceholder(_ message: String) -> some View {
        Text(message)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .accessibilityLabel("\(terminalTitle) \(statusText.lowercased())")
    }
}

@MainActor
private struct NativeSplitInitialPosition: NSViewRepresentable {
    let mainFraction: CGFloat

    final class Coordinator {
        private weak var splitView: NSSplitView?
        private var applied = false

        func apply(from probe: NSView, mainFraction: CGFloat) {
            var ancestor = probe.superview
            while let current = ancestor, !(current is NSSplitView) {
                ancestor = current.superview
            }
            guard let splitView = ancestor as? NSSplitView else {
                return
            }

            if self.splitView !== splitView {
                self.splitView = splitView
                applied = false
            }
            guard !applied, splitView.bounds.height > 0 else {
                return
            }

            splitView.layoutSubtreeIfNeeded()
            let fractionFromOrigin = splitView.isFlipped ? mainFraction : 1 - mainFraction
            splitView.setPosition(
                splitView.bounds.height * fractionFromOrigin,
                ofDividerAt: 0
            )
            applied = true
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let probe = NSView(frame: .zero)
        scheduleApply(from: probe, coordinator: context.coordinator)
        return probe
    }

    func updateNSView(_ probe: NSView, context: Context) {
        scheduleApply(from: probe, coordinator: context.coordinator)
    }

    private func scheduleApply(from probe: NSView, coordinator: Coordinator) {
        DispatchQueue.main.async {
            coordinator.apply(from: probe, mainFraction: mainFraction)
        }
    }
}

@MainActor
private struct RetainedTerminalHost: NSViewRepresentable {
    let session: TerminalSession
    let accessibilityLabel: String
    let accessibilityIdentifier: String

    func makeNSView(context: Context) -> NSView {
        let host = NSView(frame: .zero)
        attach(to: host)
        return host
    }

    func updateNSView(_ host: NSView, context: Context) {
        attach(to: host)
    }

    private func attach(to host: NSView) {
        guard session.state == .ready || session.state == .exited || session.state == .closing else {
            return
        }
        let terminalView = session.terminalView
        terminalView.setAccessibilityElement(true)
        terminalView.setAccessibilityRole(.textArea)
        terminalView.setAccessibilityLabel(accessibilityLabel)
        terminalView.setAccessibilityIdentifier(accessibilityIdentifier)
        terminalView.setAccessibilityHelp("Interactive local shell. gabCode does not interpret its commands or output.")
        session.attachTerminalView(to: host)
    }
}
