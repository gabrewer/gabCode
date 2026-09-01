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
    enum Intent { case openPanel, open(URL), create }

    static let shared = WorkspaceWindowIntentStore()
    private var pendingIntents: [Intent] = []

    func enqueue(_ action: Action) {
        switch action {
        case .open: pendingIntents.append(.openPanel)
        case .create: pendingIntents.append(.create)
        }
    }

    func enqueueOpen(_ url: URL) {
        pendingIntents.append(.open(url.standardizedFileURL))
    }

    func take() -> Intent? {
        guard !pendingIntents.isEmpty else { return nil }
        return pendingIntents.removeFirst()
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
    private struct WorktreeCreationPresentation: Identifiable {
        let id = UUID()
        let base: WorktreeCreationBase
        let selectedBranch: String?
    }

    @State private var creationPresentation: WorktreeCreationPresentation?
    @StateObject private var terminalRegistry = WorkspaceTerminalRegistry()

    var body: some View {
        Group {
            if controller.state == .ready, let descriptor = controller.activeDescriptor {
                HStack(spacing: 0) {
                    if !controller.preference.sidebarOnRight { worktreeSidebar }
                    VStack(spacing: 0) {
                        if let fallbackNotice = controller.fallbackNotice {
                            Text(fallbackNotice)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(8)
                                .background(.blue.opacity(0.12))
                                .accessibilityLabel(fallbackNotice)
                                .accessibilityAddTraits(.isStaticText)
                                .accessibilityIdentifier("workspace-fallback-notice")
                                .background(FallbackAnnouncementBridge(notice: fallbackNotice))
                        }
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
                    if let path {
                        controller.persistSelectedWorktree(path: path)
                        presentation(for: path).focusMainTerminal()
                    }
                }
                .onChange(of: controller.requestedSelectionPath) { _, path in
                    if let path { selectedWorktreePath = path }
                }
                .background(WindowTitleBridge(title: selectedWorktreePath.map { "\(controller.activeDescriptor?.name ?? "gabCode") — \($0.lastPathComponent) — gabCode" } ?? controller.windowTitle))
            } else {
                emptyOrRecoverySurface
            }
        }
        .sheet(item: $creationPresentation) { presentation in
            WorktreeCreationSheet(
                controller: controller,
                base: presentation.base,
                selectedWorktreeBranch: presentation.selectedBranch,
                isPresented: Binding(
                    get: { creationPresentation != nil },
                    set: { if !$0 { creationPresentation = nil } }
                )
            )
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
            if let url = notification.userInfo?["workspaceURL"] as? URL {
                routeExternalWorkspace(url)
            } else {
                chooseWorkspace()
            }
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
            if let intent = WorkspaceWindowIntentStore.shared.take() {
                switch intent {
                case .openPanel: chooseWorkspace()
                case let .open(url): Task { _ = await controller.openWorkspace(at: url) }
                case .create: chooseProjectFolderAndCreateWorkspace()
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
                Label(recoveryHeading, systemImage: "folder")
            } description: {
                Text(recoveryDescription)
            }
            HStack(spacing: 12) {
                Button("Open Workspace…") { route(.open) }
                    .keyboardShortcut("o", modifiers: .command)
                    .accessibilityIdentifier("open-workspace")
                Button("Create Workspace from Project Folder…") { route(.create) }
                    .accessibilityIdentifier("create-workspace")
                if let recoveryURL = controller.recoveryDescriptorURL {
                    Button("Retry") { Task { _ = await controller.openWorkspace(at: recoveryURL) } }
                        .accessibilityIdentifier("retry-workspace")
                }
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
                                if terminalRegistry.existingPresentation(for: worktree.path)?.activeTerminalCount ?? 0 > 0 {
                                    Image(systemName: "bolt.horizontal.circle.fill")
                                        .foregroundStyle(.secondary)
                                        .accessibilityHidden(true)
                                }
                            }
                            Text(worktree.branch).font(.caption).foregroundStyle(.secondary)
                            if worktree.availability == .unavailable { Text("Unavailable").font(.caption2) }
                        }
                        .accessibilityLabel("\(worktree.path.lastPathComponent), \(worktree.branch)\(selectedWorktreePath?.standardizedFileURL == worktree.path.standardizedFileURL ? ", selected" : "")\((terminalRegistry.existingPresentation(for: worktree.path)?.activeTerminalCount ?? 0) > 0 ? ", running terminals" : "")\(worktree.availability == .unavailable ? ", unavailable" : "")")
                        .tag(worktree.path)
                        .contextMenu {
                            Button("Create Worktree from \(controller.descriptorBranch ?? "Unknown")") {
                                presentCreation(.workspaceSelectedBranch, selectedBranch: nil)
                            }
                            Button("Create Worktree from \(worktree.branch)") {
                                presentCreation(.selectedWorktreeBranch, selectedBranch: worktree.branch)
                            }
                            Button("Create Worktree from Existing Branch…") {
                                presentCreation(.existingLocalBranch(""), selectedBranch: nil)
                            }
                            Divider()
                            Button("Open in VS Code") { openInVSCode(worktree.path) }
                            Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([worktree.path]) }
                            if !worktree.isPrimary {
                                Divider()
                                Button("Delete Worktree ‘\(worktree.branch)’…", role: .destructive) {
                                    requestDeletion(worktree)
                                }
                            }
                        }
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

    private func presentCreation(_ base: WorktreeCreationBase, selectedBranch: String?) {
        creationPresentation = WorktreeCreationPresentation(base: base, selectedBranch: selectedBranch)
    }

    private func requestDeletion(_ worktree: WorktreeNavigationEntry) {
        guard !worktree.isPrimary else { return }
        Task { @MainActor in
            guard let isDirty = await controller.worktreeIsDirty(path: worktree.path) else { return }
            confirmDeletion(worktree, isDirty: isDirty)
        }
    }

    private func confirmDeletion(_ worktree: WorktreeNavigationEntry, isDirty: Bool) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else { return }
        let presentation = terminalRegistry.existingPresentation(for: worktree.path)
        let activeCount = presentation?.activeTerminalCount ?? 0
        let alert = NSAlert()
        alert.messageText = "Delete worktree \(worktree.path.lastPathComponent)?"
        alert.informativeText = "Branch: \(worktree.branch)\nPath: \(worktree.path.path)\n\(isDirty ? "This worktree has uncommitted or untracked files. Git will first attempt safe removal." : "Git will attempt safe removal first.")\(activeCount > 0 ? "\nDeleting requires stopping \(activeCount) gabCode-owned terminal processes." : "")"
        alert.alertStyle = .warning
        let branchCheckbox = NSButton(checkboxWithTitle: "Also delete local branch", target: nil, action: nil)
        branchCheckbox.setAccessibilityLabel("Also delete local branch")
        alert.accessoryView = branchCheckbox
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: activeCount > 0 ? "Stop Terminals and Delete" : "Delete")
        alert.beginSheetModal(for: window) { [self] response in
            guard response == .alertSecondButtonReturn else { return }
            Task { @MainActor in
                if let presentation, presentation.activeTerminalCount > 0 {
                    let results = await presentation.workspace.stopResults(gracePeriod: .milliseconds(500))
                    guard results.allSatisfy({ $0 != .failed }) else {
                        showAlert(title: "Terminal cleanup did not complete", message: "The worktree was not deleted because an owned process could not be verified as stopped.")
                        return
                    }
                }
                let deleted = await controller.removeWorktree(
                    path: worktree.path,
                    force: false,
                    deleteLocalBranch: branchCheckbox.state == .on,
                    retainedPaths: terminalRegistry.retainedPaths
                )
                if !deleted {
                    if case let .localBranchDeletionFailed(branch, _) = controller.worktreeActionError {
                        terminalRegistry.remove(worktree.path)
                        confirmForceBranchDeletion(branch)
                    } else if isDirty {
                        confirmForceDeletion(worktree, deleteLocalBranch: branchCheckbox.state == .on)
                    } else {
                        showAlert(title: "Worktree was not deleted", message: "Git rejected safe removal. Resolve the reported blocker and try again.")
                    }
                } else {
                    terminalRegistry.remove(worktree.path)
                }
            }
        }
    }

    private func confirmForceBranchDeletion(_ branch: String) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else { return }
        let alert = NSAlert()
        alert.messageText = "Force delete unmerged branch \(branch)?"
        alert.informativeText = "The worktree was removed, but Git reported that the local branch is unmerged. Force deletion permanently discards the branch reference."
        alert.alertStyle = .critical
        alert.addButton(withTitle: "Keep Branch")
        alert.addButton(withTitle: "Force Delete Branch")
        alert.beginSheetModal(for: window) { [self] response in
            guard response == .alertSecondButtonReturn else { return }
            Task { @MainActor in
                if !(await controller.deleteLocalBranch(branch, force: true)) {
                    showAlert(title: "Branch was not deleted", message: "Git rejected force deletion of the local branch.")
                }
            }
        }
    }

    private func confirmForceDeletion(_ worktree: WorktreeNavigationEntry, deleteLocalBranch: Bool) {
        guard let window = NSApp.keyWindow ?? NSApp.windows.first else { return }
        let alert = NSAlert()
        alert.messageText = "Force delete this worktree?"
        alert.informativeText = "Safe removal was blocked. Force deletion may permanently lose uncommitted or untracked files. This cannot be undone."
        alert.alertStyle = .critical
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Force Delete")
        alert.beginSheetModal(for: window) { [self] response in
            guard response == .alertSecondButtonReturn else { return }
            Task { @MainActor in
                let deleted = await controller.removeWorktree(
                    path: worktree.path,
                    force: true,
                    deleteLocalBranch: deleteLocalBranch,
                    retainedPaths: terminalRegistry.retainedPaths
                )
                if !deleted {
                    if case let .localBranchDeletionFailed(branch, _) = controller.worktreeActionError {
                        terminalRegistry.remove(worktree.path)
                        confirmForceBranchDeletion(branch)
                    } else {
                        showAlert(title: "Worktree was not deleted", message: "Git rejected force removal. Resolve the reported blocker and try again.")
                    }
                } else {
                    terminalRegistry.remove(worktree.path)
                    if selectedWorktreePath == worktree.path.standardizedFileURL { selectedWorktreePath = controller.worktrees.first?.path }
                }
            }
        }
    }

    private func openInVSCode(_ path: URL) {
        guard let application = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode") else {
            showAlert(title: "VS Code Unavailable", message: "Visual Studio Code could not be found.")
            return
        }
        NSWorkspace.shared.open([path], withApplicationAt: application, configuration: NSWorkspace.OpenConfiguration())
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

    private var recoveryHeading: String {
        guard case let .recovery(error) = controller.state else {
            return controller.state == .loading ? "Opening workspace" : "No workspace open"
        }
        switch error {
        case .malformedDescriptor, .unreadableDescriptor, .branchNotFound:
            return "Invalid workspace file"
        default:
            return "Workspace could not be opened"
        }
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

    private func routeExternalWorkspace(_ url: URL) {
        if WorkspaceWindowRouting.opensSeparateWindow(hasActiveProject: controller.activeDescriptor != nil) {
            WorkspaceWindowIntentStore.shared.enqueueOpen(url)
            openWindow(id: "main")
        } else {
            Task { _ = await controller.openWorkspace(at: url) }
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
        alert.messageText = "Choose the project's main branch"
        alert.informativeText = "Select a local branch to record as this project's main branch."
        let popup = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 280, height: 26), pullsDown: false)
        popup.addItems(withTitles: branches)
        if branches.contains("main") {
            popup.selectItem(withTitle: "main")
        }
        popup.setAccessibilityLabel("Project main branch")
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
private struct FallbackAnnouncementBridge: NSViewRepresentable {
    let notice: String

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        announce(notice, from: view, coordinator: context.coordinator)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        announce(notice, from: view, coordinator: context.coordinator)
    }

    private func announce(_ notice: String, from view: NSView, coordinator: Coordinator) {
        guard coordinator.lastNotice != notice else { return }
        coordinator.lastNotice = notice
        NSAccessibility.post(
            element: view,
            notification: .announcementRequested,
            userInfo: [.announcement: notice, .priority: 50]
        )
    }

    final class Coordinator {
        var lastNotice: String?
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
