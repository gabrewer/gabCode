import AppKit
import SwiftUI

extension Notification.Name {
    static let gabCodeOpenWorkspace = Notification.Name("gabCode.openWorkspace")
    static let gabCodeCreateWorkspace = Notification.Name("gabCode.createWorkspace")
    static let gabCodeRefreshWorktrees = Notification.Name("gabCode.refreshWorktrees")
    static let gabCodeMoveSidebar = Notification.Name("gabCode.moveSidebar")
}

@MainActor
final class WorkspaceWindowIntentStore {
    enum Action { case open, create }
    static let shared = WorkspaceWindowIntentStore()
    private var pendingAction: Action?

    func enqueue(_ action: Action) { pendingAction = action }
    func take() -> Action? {
        defer { pendingAction = nil }
        return pendingAction
    }
}

@MainActor
struct WorkspaceProjectView: View {
    @ObservedObject var controller: WorkspaceProjectController
    @EnvironmentObject private var fontPreference: TerminalFontPreferenceStore
    @Environment(\.openWindow) private var openWindow
    @State private var isPresentingPanel = false
    @State private var windowNumber: Int?
    @State private var selectedWorktreePath: URL?
    @StateObject private var terminalRegistry = WorkspaceTerminalRegistry()

    var body: some View {
        Group {
            if controller.state == .ready, let descriptor = controller.activeDescriptor {
                HStack(spacing: 0) {
                    if !controller.preference.sidebarOnRight { worktreeSidebar }
                    VStack(spacing: 0) {
                        if let selectedWorktreePath, controller.orphanedWorktreePaths.contains(selectedWorktreePath.standardizedFileURL) {
                            Text("Orphaned terminal — Git worktree unavailable")
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(8)
                                .background(.yellow.opacity(0.2))
                                .accessibilityLabel("Orphaned terminal. Git worktree unavailable.")
                        }
                        WorkspaceTerminalStackView(
                            registry: terminalRegistry,
                            selectedPath: selectedWorktreePath ?? descriptor.resolvedFolder
                        )
                    }
                    if controller.preference.sidebarOnRight { worktreeSidebar }
                }
                .onAppear { selectedWorktreePath = descriptor.resolvedFolder }
                .onChange(of: selectedWorktreePath) { _, path in
                    if let path { presentation(for: path).focusMainTerminal() }
                }
                .background(WindowTitleBridge(title: selectedWorktreePath.map { "\(controller.activeDescriptor?.name ?? "gabCode") — \($0.lastPathComponent) — gabCode" } ?? controller.windowTitle))
            } else {
                emptyOrRecoverySurface
            }
        }
        .frame(
            minWidth: WorkspaceWindowLayout.minimumWidth,
            minHeight: WorkspaceWindowLayout.minimumHeight
        )
        .background(WindowMinimumSizeBridge())
        .background(WindowIdentityReader { number in
            windowNumber = number
        })
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeOpenWorkspace)) { notification in
            guard handles(notification) else { return }
            chooseWorkspace()
        }
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeCreateWorkspace)) { notification in
            guard handles(notification) else { return }
            chooseProjectFolderAndCreateWorkspace()
        }
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeRefreshWorktrees)) { notification in
            guard handles(notification) else { return }
            Task { await controller.refreshWorktrees(retainedPaths: terminalRegistry.retainedPaths) }
        }
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeMoveSidebar)) { notification in
            guard handles(notification) else { return }
            if let right = notification.userInfo?["right"] as? Bool {
                controller.preference.sidebarOnRight = right
            } else {
                controller.preference.sidebarOnRight.toggle()
            }
        }
        .onAppear {
            if let action = WorkspaceWindowIntentStore.shared.take() {
                DispatchQueue.main.async { [self] in
                    switch action {
                    case .open: chooseWorkspace()
                    case .create: chooseProjectFolderAndCreateWorkspace()
                    }
                }
                return
            }
            guard controller.state == .empty, controller.activeDescriptor == nil else { return }
            Task { _ = await controller.reopenRememberedWorkspace() }
        }
    }

    @ViewBuilder
    private var emptyOrRecoverySurface: some View {
        VStack(spacing: 18) {
            ContentUnavailableView {
                Label(
                    controller.state == .recovery(.cancelled) ? "Open a gabCode project" : "No workspace open",
                    systemImage: "folder"
                )
            } description: {
                Text(recoveryDescription)
            }
            HStack(spacing: 12) {
                Button("Open Workspace…") { route(.open) }
                    .keyboardShortcut("o", modifiers: .command)
                    .accessibilityIdentifier("open-workspace")
                Button("Create Workspace from Project Folder…") { route(.create) }
                    .accessibilityIdentifier("create-workspace")
            }
            if case let .recovery(error) = controller.state {
                Text(error.message)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .accessibilityLabel("Workspace recovery message")
                    .accessibilityValue(error.message)
                    .accessibilityIdentifier("workspace-recovery-message")
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("workspace-empty-surface")
    }

    @ViewBuilder
    private var worktreeSidebar: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Worktrees").font(.headline)
                Spacer()
                if controller.isRefreshing {
                    ProgressView()
                        .controlSize(.small)
                        .accessibilityLabel("Refreshing worktrees")
                    Button("Cancel") { controller.cancelRefresh() }
                        .controlSize(.small)
                        .accessibilityIdentifier("cancel-refresh-worktrees")
                } else {
                    Button { Task { await controller.refreshWorktrees(retainedPaths: terminalRegistry.retainedPaths) } } label: { Image(systemName: "arrow.clockwise") }
                        .accessibilityLabel("Refresh Worktrees")
                        .accessibilityIdentifier("refresh-worktrees")
                }
            }.padding(10)
            if let refreshError = controller.refreshError {
                Text(refreshError.message)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .padding(.horizontal, 10)
                    .padding(.bottom, 6)
                    .accessibilityLabel("Refresh worktrees failed")
                    .accessibilityValue(refreshError.message)
                    .accessibilityIdentifier("refresh-worktrees-error")
            }
            List(selection: $selectedWorktreePath) {
                Section("Worktrees") {
                    ForEach(controller.worktrees, id: \.path) { worktree in
                        VStack(alignment: .leading) {
                            HStack(spacing: 6) {
                                Text(worktree.path.lastPathComponent)
                                if selectedWorktreePath?.standardizedFileURL == worktree.path.standardizedFileURL {
                                    Image(systemName: "checkmark.circle.fill")
                                        .foregroundStyle(.secondary)
                                        .accessibilityHidden(true)
                                }
                                if terminalRegistry.existingPresentation(for: worktree.path)?.activeTerminalCount ?? 0 > 0 {
                                    Image(systemName: "bolt.horizontal.circle.fill")
                                        .foregroundStyle(.secondary)
                                        .accessibilityHidden(true)
                                }
                            }
                            Text(worktree.branch).font(.caption).foregroundStyle(.secondary)
                            if worktree.availability == .unavailable { Text("Unavailable").font(.caption2) }
                            if terminalRegistry.existingPresentation(for: worktree.path)?.activeTerminalCount ?? 0 > 0 {
                                Text("Running terminals").font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                        .accessibilityLabel("\(worktree.path.lastPathComponent), \(worktree.branch)\(selectedWorktreePath?.standardizedFileURL == worktree.path.standardizedFileURL ? ", selected" : "")\((terminalRegistry.existingPresentation(for: worktree.path)?.activeTerminalCount ?? 0) > 0 ? ", running terminals" : "")\(worktree.availability == .unavailable ? ", unavailable" : "")")
                        .tag(worktree.path)
                    }
                }
                if !controller.orphanedWorktreePaths.isEmpty {
                    Section("Orphaned terminals") {
                        ForEach(controller.orphanedWorktreePaths, id: \.self) { path in
                            HStack {
                                Text(path.lastPathComponent)
                                Spacer()
                                Button("Close Terminals") { closeOrphan(path) }
                                    .controlSize(.small)
                            }
                            .tag(path)
                            .accessibilityLabel("\(path.lastPathComponent), orphaned terminal")
                        }
                    }
                }
            }
        }
        .frame(
            minWidth: WorkspaceWindowLayout.sidebarMinimumWidth,
            idealWidth: WorkspaceWindowLayout.sidebarIdealWidth,
            maxWidth: WorkspaceWindowLayout.sidebarMaximumWidth
        )
        .accessibilityIdentifier("worktree-sidebar")
        Divider()
    }

    private func closeOrphan(_ path: URL) {
        let presentation = terminalRegistry.presentation(for: path)
        guard let window = NSApp.keyWindow else { return }
        let alert = NSAlert()
        alert.messageText = "Close orphaned terminals?"
        alert.informativeText = "This stops the retained terminal processes for \(path.lastPathComponent)."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Close Terminals")
        alert.beginSheetModal(for: window) { response in
            guard response == .alertSecondButtonReturn else { return }
            Task { @MainActor in
                let results = await presentation.workspace.stopResults(gracePeriod: .milliseconds(500))
                guard results.allSatisfy({ $0 != .failed }) else { return }
                terminalRegistry.remove(path)
                controller.forgetOrphan(path: path)
                if selectedWorktreePath == path.standardizedFileURL { selectedWorktreePath = nil }
            }
        }
    }

    private func presentation(for path: URL) -> TerminalWorkspacePresentation {
        terminalRegistry.presentation(for: path)
    }

    private var recoveryDescription: String {
        switch controller.state {
        case .loading: "Validating the workspace. No terminal will start until validation succeeds."
        case .recovery: "The workspace could not be opened. Choose another workspace or retry."
        case .empty, .ready: "Open an existing workspace descriptor or create one for a project folder and selected branch."
        }
    }

    private func route(_ action: WorkspaceWindowIntentStore.Action) {
        if WorkspaceWindowRouting.opensSeparateWindow(hasActiveProject: controller.activeDescriptor != nil) {
            WorkspaceWindowIntentStore.shared.enqueue(action)
            openWindow(id: "main")
        } else {
            switch action {
            case .open: chooseWorkspace()
            case .create: chooseProjectFolderAndCreateWorkspace()
            }
        }
    }

    private func handles(_ notification: Notification) -> Bool {
        guard let target = notification.userInfo?["windowNumber"] as? Int else {
            return true
        }
        return target == windowNumber
    }

    private func chooseWorkspace() {
        guard !isPresentingPanel else { return }
        isPresentingPanel = true
        let panel = NSOpenPanel()
        panel.title = "Open Workspace"
        panel.prompt = "Open Workspace"
        panel.allowedFileTypes = ["gabcode-workspace"]
        panel.allowsMultipleSelection = false
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        present(panel) { url in
            isPresentingPanel = false
            guard let url else { return }
            Task { await openAfterReplacement { await controller.openWorkspace(at: url) } }
        }
    }

    private func chooseProjectFolderAndCreateWorkspace() {
        guard !isPresentingPanel else { return }
        isPresentingPanel = true
        let panel = NSOpenPanel()
        panel.title = "Choose Project Folder"
        panel.prompt = "Choose Folder"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        present(panel) { url in
            guard let url else {
                isPresentingPanel = false
                return
            }
            DispatchQueue.main.async { [self] in
                requestWorkspaceName { [self] name in
                    guard let name else {
                        isPresentingPanel = false
                        return
                    }
                    Task { @MainActor in
                        let branchResult = await controller.availableBranches(in: url)
                        guard case let .success(branches) = branchResult, !branches.isEmpty else {
                            isPresentingPanel = false
                            let message = (try? branchResult.get()).map { _ in "No branches were found beneath the selected project folder." }
                                ?? "Git worktree discovery could not resolve this project folder."
                            showAlert(title: "Choose Another Project Folder", message: message)
                            return
                        }
                        DispatchQueue.main.async { [self] in
                            requestBranch(branches) { [self] branch in
                                guard let branch else {
                                    isPresentingPanel = false
                                    return
                                }
                                let save = NSSavePanel()
                                save.title = "Save Workspace"
                                save.prompt = "Save Workspace"
                                save.directoryURL = url
                                save.nameFieldStringValue = name
                                save.allowedFileTypes = ["gabcode-workspace"]
                                DispatchQueue.main.async { [self] in
                                    present(save) { descriptorURL in
                                        isPresentingPanel = false
                                        guard let descriptorURL else { return }
                                        Task {
                                            _ = await controller.createWorkspace(
                                                name: name,
                                                projectRoot: url,
                                                branch: branch,
                                                descriptorURL: descriptorURL
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private func openAfterReplacement(_ action: @escaping () async -> Bool) async {
        guard let presentation = WindowWorkspaceRegistry.shared.currentFocusedPresentation(),
              presentation.activeTerminalCount > 0,
              let window = NSApp.keyWindow ?? NSApp.windows.first
        else {
            _ = await action()
            return
        }

        let alert = NSAlert()
        alert.messageText = "Replace the current workspace?"
        alert.informativeText = "Running shell work in this window will be interrupted."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Replace Workspace")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        WindowWorkspaceRegistry.shared.setMutationLocked(true)
        let results = await presentation.workspace.stopResults(gracePeriod: .milliseconds(500))
        WindowWorkspaceRegistry.shared.setMutationLocked(false)
        guard results.allSatisfy({ $0 != .failed }) else {
            let failure = NSAlert()
            failure.messageText = "Terminal cleanup did not complete"
            failure.informativeText = "The current workspace remains open because owned terminals could not be verified as stopped."
            failure.alertStyle = .critical
            _ = failure.runModal()
            return
        }
        _ = await action()
    }

    private func requestBranch(_ branches: [String], completion: @escaping (String?) -> Void) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else {
            completion(nil)
            return
        }
        let alert = NSAlert()
        alert.messageText = "Choose a branch"
        alert.informativeText = "Select the worktree to use for this workspace."
        let popup = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 280, height: 26), pullsDown: false)
        popup.addItems(withTitles: branches)
        if branches.contains("main") {
            popup.selectItem(withTitle: "main")
        }
        popup.setAccessibilityLabel("Workspace branch")
        alert.accessoryView = popup
        alert.addButton(withTitle: "Continue")
        alert.addButton(withTitle: "Cancel")
        alert.beginSheetModal(for: window) { response in
            completion(response == .alertFirstButtonReturn ? popup.titleOfSelectedItem : nil)
        }
    }

    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    private func requestWorkspaceName(completion: @escaping (String?) -> Void) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else {
            completion(nil)
            return
        }
        let alert = NSAlert()
        alert.messageText = "Name this workspace"
        alert.informativeText = "A workspace name is required."
        let field = NSTextField(string: "")
        field.placeholderString = "Workspace name"
        field.setAccessibilityLabel("Workspace name")
        field.frame.size.width = 280
        alert.accessoryView = field
        alert.addButton(withTitle: "Continue")
        alert.addButton(withTitle: "Cancel")
        alert.beginSheetModal(for: window) { response in
            guard response == .alertFirstButtonReturn else {
                completion(nil)
                return
            }
            let name = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            completion(name.isEmpty ? nil : name)
        }
    }

    private func present(_ panel: NSOpenPanel, completion: @escaping (URL?) -> Void) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else {
            isPresentingPanel = false
            completion(nil)
            return
        }
        panel.beginSheetModal(for: window) { response in
            completion(response == .OK ? panel.url : nil)
        }
    }

    private func present(_ panel: NSSavePanel, completion: @escaping (URL?) -> Void) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else {
            isPresentingPanel = false
            completion(nil)
            return
        }
        panel.beginSheetModal(for: window) { response in
            completion(response == .OK ? panel.url : nil)
        }
    }
}

@MainActor
private struct WindowIdentityReader: NSViewRepresentable {
    let onWindowNumber: (Int) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        DispatchQueue.main.async {
            if let number = view.window?.windowNumber {
                onWindowNumber(number)
            }
        }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        guard let number = view.window?.windowNumber else { return }
        DispatchQueue.main.async {
            onWindowNumber(number)
        }
    }
}

@MainActor
private struct WindowTitleBridge: NSViewRepresentable {
    let title: String

    func makeNSView(context: Context) -> NSView { NSView(frame: .zero) }

    func updateNSView(_ view: NSView, context: Context) {
        view.window?.title = title
    }
}
