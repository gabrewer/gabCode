import AppKit
import SwiftUI

extension Notification.Name {
    static let gabCodeOpenWorkspace = Notification.Name("gabCode.openWorkspace")
    static let gabCodeCreateWorkspace = Notification.Name("gabCode.createWorkspace")
}

@MainActor
struct WorkspaceProjectView: View {
    @ObservedObject var controller: WorkspaceProjectController
    @EnvironmentObject private var fontPreference: TerminalFontPreferenceStore
    @State private var isPresentingPanel = false

    var body: some View {
        Group {
            if controller.state == .ready, let descriptor = controller.activeDescriptor {
                TerminalWorkspaceView(workingDirectory: descriptor.resolvedFolder)
                    .background(WindowTitleBridge(title: controller.windowTitle))
            } else {
                emptyOrRecoverySurface
            }
        }
        .frame(minWidth: 760, minHeight: 640)
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeOpenWorkspace)) { _ in
            chooseWorkspace()
        }
        .onReceive(NotificationCenter.default.publisher(for: .gabCodeCreateWorkspace)) { _ in
            chooseGitFolderAndCreateWorkspace()
        }
        .onAppear {
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
                Button("Open Workspace…", action: chooseWorkspace)
                    .keyboardShortcut("o", modifiers: .command)
                    .accessibilityIdentifier("open-workspace")
                Button("Create Workspace from Git Folder…", action: chooseGitFolderAndCreateWorkspace)
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

    private var recoveryDescription: String {
        switch controller.state {
        case .loading: "Validating the workspace. No terminal will start until validation succeeds."
        case .recovery: "The workspace could not be opened. Choose another workspace or retry."
        case .empty, .ready: "Open an existing workspace descriptor or create one for an existing Git folder."
        }
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

    private func chooseGitFolderAndCreateWorkspace() {
        guard !isPresentingPanel else { return }
        isPresentingPanel = true
        let panel = NSOpenPanel()
        panel.title = "Choose Git Folder"
        panel.prompt = "Choose Folder"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        present(panel) { url in
            guard let url else {
                isPresentingPanel = false
                return
            }
            // Wait for the folder sheet to finish dismissing before presenting
            // another sheet on the same window.
            DispatchQueue.main.async { [self] in
                requestWorkspaceName { [self] name in
                    guard let name else {
                        isPresentingPanel = false
                        return
                    }
                    let save = NSSavePanel()
                    save.title = "Save Workspace"
                    save.prompt = "Save Workspace"
                    save.nameFieldStringValue = "\(name).gabcode-workspace"
                    save.allowedFileTypes = ["gabcode-workspace"]
                    // The name alert is also a sheet; defer the save panel until
                    // AppKit has removed it from the window.
                    DispatchQueue.main.async { [self] in
                        present(save) { descriptorURL in
                            isPresentingPanel = false
                            guard let descriptorURL else { return }
                            Task {
                                await openAfterReplacement {
                                    await controller.createWorkspace(name: name, folder: url, descriptorURL: descriptorURL)
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
private struct WindowTitleBridge: NSViewRepresentable {
    let title: String

    func makeNSView(context: Context) -> NSView { NSView(frame: .zero) }

    func updateNSView(_ view: NSView, context: Context) {
        view.window?.title = title
    }
}
