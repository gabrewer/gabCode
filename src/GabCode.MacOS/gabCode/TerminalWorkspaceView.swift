import AppKit
import SwiftUI

@MainActor
struct WorkspaceTerminalStackView: View {
    @ObservedObject var registry: WorkspaceTerminalRegistry
    let selectedPath: URL

    var body: some View {
        ZStack {
            ForEach(registry.retainedPresentations, id: \.workingDirectory) { presentation in
                TerminalWorkspaceView(
                    presentation: presentation,
                    isActive: presentation.workingDirectory.standardizedFileURL == selectedPath.standardizedFileURL
                )
                .opacity(presentation.workingDirectory.standardizedFileURL == selectedPath.standardizedFileURL ? 1 : 0)
                .allowsHitTesting(presentation.workingDirectory.standardizedFileURL == selectedPath.standardizedFileURL)
                .accessibilityHidden(presentation.workingDirectory.standardizedFileURL != selectedPath.standardizedFileURL)
            }
        }
        .task(id: selectedPath.standardizedFileURL.path) {
            guard let presentation = await registry.ensureStarted(for: selectedPath) else { return }
            await Task.yield()
            presentation.focusMainTerminal()
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("terminal-workspace-stack")
    }
}

@MainActor
struct TerminalWorkspaceView: View {
    @EnvironmentObject private var fontPreference: TerminalFontPreferenceStore
    @ObservedObject private var presentation: TerminalWorkspacePresentation
    @ObservedObject private var document: ReadOnlyDiffDocumentState
    private let reviewService = WorktreeChangeReviewService()
    private let modePreference = ReviewModePreference()
    @State private var reviewMode = "side-by-side"
    private let isActive: Bool

    init(workingDirectory: URL, font: NSFont = NSFont.monospacedSystemFont(ofSize: TerminalFontSelection.defaultPointSize, weight: .regular), isActive: Bool = true) {
        let presentation = TerminalWorkspacePresentation(workspace: TerminalWorkspace(workingDirectory: workingDirectory, font: font), workingDirectory: workingDirectory)
        _presentation = ObservedObject(wrappedValue: presentation)
        _document = ObservedObject(wrappedValue: presentation.reviewDocument)
        self.isActive = isActive
    }

    init(presentation: TerminalWorkspacePresentation, isActive: Bool = true) {
        _presentation = ObservedObject(wrappedValue: presentation)
        _document = ObservedObject(wrappedValue: presentation.reviewDocument)
        self.isActive = isActive
    }

    var body: some View {
        VStack(spacing: 0) {
            workspaceHeader
            Divider()
            VSplitView {
                Group {
                    if document.isOpen {
                        readOnlyDiffSurface
                    } else {
                        TerminalRegionView(
                            presentation: presentation,
                            terminal: presentation.mainTerminal,
                            placementName: "Main",
                            placementIdentifier: "main-terminal-label"
                        )
                    }
                }
                .frame(minHeight: 220, idealHeight: 390)
                .background(NativeSplitInitialPosition(mainFraction: 2.0 / 3.0))

                Group {
                    if document.isOpen {
                        retainedTerminalTabs
                    } else {
                        TerminalRegionView(
                            presentation: presentation,
                            terminal: presentation.bottomTerminal,
                            placementName: "Bottom",
                            placementIdentifier: "bottom-terminal-label"
                        )
                    }
                }
                .frame(minHeight: 140, idealHeight: 195)
            }
        }
        .frame(minWidth: WorkspaceWindowLayout.terminalMinimumWidth, minHeight: WorkspaceWindowLayout.minimumHeight)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("terminal-workspace")
        .onChange(of: isActive) { _, active in
            if active {
                TerminalCommandRouter.shared.connect(presentation)
                presentation.focusMainTerminal()
            } else {
                TerminalCommandRouter.shared.disconnect(presentation)
            }
        }
        .task(id: document.revision) {
            guard let file = document.selectedFile, isActive else { return }
            let revision = document.revision
            guard let branch = presentation.reviewMainBranch, let host = document.viewerHost else {
                document.fail("Configured local main branch is unavailable.")
                return
            }
            do {
                let result = try await reviewService.document(for: file, in: presentation.workingDirectory, mainBranch: branch)
                guard !Task.isCancelled, document.revision == revision, document.selectedFile?.id == file.id else { return }
                if !host.isReady { try await host.load() }
                guard !Task.isCancelled, document.revision == revision, document.selectedFile?.id == file.id else { return }
                host.select(identity: result.identity, generation: revision)
                host.onEvent = { [weak document] event in document?.updateViewerEvent(event) }
                try await host.show(before: result.before, after: result.after, path: file.path,
                                    identity: result.identity, generation: revision, mode: reviewMode,
                                    beforeLabel: beforeVersionLabel(for: file), afterLabel: afterVersionLabel(for: file),
                                    beforeFacts: facts(result.beforeFacts), afterFacts: facts(result.afterFacts))
                guard !Task.isCancelled, document.revision == revision, document.selectedFile?.id == file.id else { return }
                document.apply(result)
                if document.wantsFocus { try? await host.focusCode(); document.didFocus() }
            } catch {
                guard !Task.isCancelled, document.revision == revision, document.selectedFile?.id == file.id else { return }
                document.fail(error.localizedDescription)
            }
        }
        .onReceive(fontPreference.$effectiveSelection) { _ in
            if isActive { presentation.apply(font: fontPreference.effectiveFont) }
        }
        .onAppear {
            reviewMode = modePreference.mode
            if isActive { TerminalCommandRouter.shared.connect(presentation) }
        }
        .onDisappear {
            TerminalCommandRouter.shared.disconnect(presentation)
        }
    }

    @ViewBuilder
    private var readOnlyDiffSurface: some View {
        if let file = document.selectedFile {
            VStack(spacing: 0) {
                Text("\(file.oldPath.map { "\($0) → \(file.path)" } ?? file.path) — \(file.status.rawValue.capitalized) — \(document.reviewPair?.comparison ?? file.comparison)")
                    .font(.caption.weight(.semibold))
                    .lineLimit(1).truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 8).padding(.top, 5)
                HStack(spacing: 6) {
                    Picker("Review mode", selection: $reviewMode) {
                        Text("Side by side").tag("side-by-side")
                        Text("Inline").tag("inline")
                        Text("File").tag("file")
                    }
                    .pickerStyle(.segmented)
                    .frame(maxWidth: 230)
                    .accessibilityIdentifier("review-mode")
                    .onChange(of: reviewMode) { _, mode in
                        modePreference.mode = mode
                        viewerCommand("mode", mode)
                    }
                    Button("Previous change", systemImage: "chevron.up") { viewerCommand("change", "previous") }
                        .labelStyle(.iconOnly)
                    Button("Next change", systemImage: "chevron.down") { viewerCommand("change", "next") }
                        .labelStyle(.iconOnly)
                    Text(document.changePosition).font(.caption).lineLimit(1)
                        .accessibilityIdentifier("review-change-position")
                    if document.canJumpToDeletion {
                        Button("Show deletion in Inline") {
                            guard let host = document.viewerHost else { return }
                            Task {
                                try? await host.command("deletion")
                                guard document.viewerHost === host else { return }
                                reviewMode = "inline"
                            }
                        }
                    }
                    Spacer(minLength: 0)
                    Button("Find", systemImage: "magnifyingglass") { viewerCommand("find") }
                        .labelStyle(.iconOnly)
                    Button("Focus code (F6 to leave)", systemImage: "text.cursor") { viewerCommand("focus") }
                        .labelStyle(.iconOnly)
                    if let currentFile = validCurrentFile(for: file) {
                        Button("Open in VS Code", systemImage: "arrow.up.forward.app") { openCurrentFileInVSCode(currentFile) }
                            .labelStyle(.iconOnly)
                            .accessibilityIdentifier("review-open-current-file-vscode")
                    }
                    if document.errorMessage != nil {
                        Button("Retry", systemImage: "arrow.clockwise") { document.retry() }
                            .labelStyle(.iconOnly).accessibilityIdentifier("review-retry")
                    }
                    Button("Close Diff", systemImage: "xmark") { presentation.closeReviewDocument() }
                        .labelStyle(.iconOnly)
                        .accessibilityIdentifier("close-review-diff")
                }
                .controlSize(.mini)
                .padding(.horizontal, 8).padding(.vertical, 2)
                if let error = document.errorMessage {
                    Text("Diff unavailable — \(error)\(document.reviewPair == nil ? "" : " · Previous review snapshot is stale")")
                        .font(.caption).foregroundStyle(.orange).lineLimit(1)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 8)
                        .accessibilityIdentifier("review-diff-error")
                } else if let pair = document.reviewPair, let notice = byteFactsNotice(for: pair) {
                    Text(notice)
                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 8)
                        .accessibilityIdentifier("review-byte-facts")
                }
                Divider()
                if let host = document.viewerHost {
                    NativeReviewDocumentHost(host: host, onExit: { presentation.focus(document.selectedBottomTerminal) })
                        .id(ObjectIdentifier(host))
                        .accessibilityIdentifier("native-diff-viewer")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .overlay(alignment: .topTrailing) {
                            if document.isLoading {
                                ProgressView(document.hasLoadedContent ? "Refreshing diff" : "Loading read-only diff")
                                    .controlSize(.mini).padding(5)
                            }
                        }
                } else {
                    ContentUnavailableView("Diff unavailable", systemImage: "doc.text.magnifyingglass")
                }
            }
            .background(ReadOnlyDiffAnnouncement(message: document.isLoading
                ? "Loading diff for \(file.path)"
                : document.errorMessage.map { "Diff unavailable for \(file.path). \($0)" }
                    ?? "Read-only diff ready for \(file.path)"))
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Read-only diff for \(file.path), \(file.comparison)")
            .accessibilityIdentifier("review-diff-document")
        }
    }

    private func byteFactsNotice(for pair: ReviewDocument) -> String? {
        var facts: [String] = []
        if pair.beforeFacts.eol != pair.afterFacts.eol {
            facts.append("line endings \(pair.beforeFacts.eol) → \(pair.afterFacts.eol)")
        }
        if pair.beforeFacts.finalNewline != pair.afterFacts.finalNewline { facts.append("final newline differs") }
        if pair.beforeFacts.bom != pair.afterFacts.bom { facts.append("UTF-8 BOM differs") }
        if !pair.beforeExists { facts.append("Before version absent") }
        if !pair.afterExists { facts.append("Deleted result") }
        if pair.rawFilter { facts.append("Raw content — Git filters not applied") }
        return facts.isEmpty ? nil : facts.joined(separator: " — ")
    }

    private func beforeVersionLabel(for file: ReviewFile) -> String {
        switch file.group {
        case .staged: "HEAD"
        case .unstaged: file.status == .untracked ? "Empty" : "Index"
        case .commit: file.comparison.hasPrefix("empty tree") ? "Empty tree" : "First parent"
        case .branch: "Merge base"
        }
    }

    private func afterVersionLabel(for file: ReviewFile) -> String {
        switch file.group {
        case .staged: "Index"
        case .unstaged: "Worktree"
        case .commit: "Selected commit"
        case .branch: "HEAD"
        }
    }

    private func facts(_ value: ReviewTextFacts) -> [String: Any] {
        ["bom": value.bom, "eol": value.eol, "finalNewline": value.finalNewline]
    }

    private func viewerCommand(_ kind: String, _ value: String? = nil) {
        guard let host = document.viewerHost, host.isReady else { return }
        let revision = document.revision
        Task {
            do {
                if kind == "find" || kind == "focus" {
                    guard document.reviewPair != nil else { return }
                    try await host.focusCode()
                }
                try await host.command(kind, value: value)
            } catch {
                guard document.viewerHost === host, document.revision == revision else { return }
                document.fail(error.localizedDescription)
            }
        }
    }

    private func validCurrentFile(for file: ReviewFile) -> URL? {
        guard NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode") != nil else { return nil }
        return ReviewCurrentFileLocator.existingFile(for: file, selectedWorktree: presentation.workingDirectory)
    }

    private func openCurrentFileInVSCode(_ url: URL) {
        guard let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode"),
              let file = document.selectedFile, validCurrentFile(for: file) == url else { return }
        NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
    }

    private var retainedTerminalTabs: some View {
        TabView(selection: Binding(
            get: { document.selectedBottomTerminal },
            set: { document.selectBottom($0) }
        )) {
            TerminalRegionView(presentation: presentation, terminal: .terminal1, placementName: "Bottom tab", placementIdentifier: "terminal-1-bottom-tab")
                .tabItem { Text("Terminal 1") }.tag(TerminalWorkspaceTerminal.terminal1)
            TerminalRegionView(presentation: presentation, terminal: .terminal2, placementName: "Bottom tab", placementIdentifier: "terminal-2-bottom-tab")
                .tabItem { Text("Terminal 2") }.tag(TerminalWorkspaceTerminal.terminal2)
        }
        .accessibilityLabel("Retained terminal tabs")
        .accessibilityIdentifier("review-terminal-tabs")
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
private struct NativeReviewDocumentHost: NSViewRepresentable {
    let host: MacDiffViewerHost
    let onExit: () -> Void

    func makeNSView(context: Context) -> NSView {
        host.onFocusExit = onExit
        host.webView.setAccessibilityLabel("Read-only code review. Press F6 to return to native controls.")
        host.webView.setAccessibilityIdentifier("native-diff-viewer")
        return host.webView
    }

    func updateNSView(_ view: NSView, context: Context) { host.onFocusExit = onExit }
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
