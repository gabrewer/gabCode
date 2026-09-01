import AppKit
import Combine
import SwiftUI

@MainActor
final class TerminalShutdownCoordinator: ObservableObject {
    static let shared = TerminalShutdownCoordinator()

    @Published private(set) var isStopping = false
    private weak var pendingWindow: NSWindow?
    private var permitsWindowClose = false

    private init() {}

    func requestWindowClose(_ window: NSWindow) -> Bool {
        if permitsWindowClose { return true }
        let presentations = WindowWorkspaceRegistry.shared.presentations(for: window)
        guard presentations.contains(where: { $0.activeTerminalCount > 0 }) else {
            WindowWorkspaceRegistry.shared.unregister(window)
            return true
        }
        requestConfirmation(for: window, presentations: presentations) { [weak self, weak window] cleanedUp in
            guard cleanedUp, let self, let window else { return }
            self.permitsWindowClose = true
            window.performClose(nil)
            self.permitsWindowClose = false
        }
        return false
    }

    func requestApplicationTermination(_ application: NSApplication) -> NSApplication.TerminateReply {
        guard pendingWindow == nil, !isStopping else { return .terminateCancel }
        let presentations = WindowWorkspaceRegistry.shared.presentations
        guard WindowWorkspaceRegistry.shared.activeTerminalCount > 0 else { return .terminateNow }
        guard let window = application.keyWindow ?? application.windows.first else { return .terminateCancel }
        requestConfirmation(for: window, presentations: presentations) { cleanedUp in
            application.reply(toApplicationShouldTerminate: cleanedUp)
        }
        return .terminateLater
    }

    private func requestConfirmation(
        for window: NSWindow,
        presentations: [TerminalWorkspacePresentation],
        completion: @escaping (Bool) -> Void
    ) {
        guard !isStopping, pendingWindow == nil else { return }
        let activeCount = presentations.reduce(0) { $0 + $1.activeTerminalCount }
        guard activeCount > 0 else { completion(true); return }

        WindowWorkspaceRegistry.shared.setMutationLocked(true)
        pendingWindow = window
        let responder = window.firstResponder
        let alert = NSAlert()
        alert.messageText = "Stop \(activeCount) active terminals?"
        alert.informativeText = "Running shell work will be interrupted."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Close and Stop Terminals")
        alert.beginSheetModal(for: window) { [weak self, weak window] response in
            guard let self else { completion(false); return }
            self.pendingWindow = nil
            guard response == NSApplication.ModalResponse(rawValue: 1001) else {
                WindowWorkspaceRegistry.shared.setMutationLocked(false)
                if let window, let responder { window.makeFirstResponder(responder) }
                completion(false)
                return
            }

            self.isStopping = true
            Task { @MainActor in
                var results: [TerminalShutdownResult] = []
                for presentation in presentations {
                    results.append(contentsOf: await presentation.workspace.stopResults(gracePeriod: .milliseconds(500)))
                }
                self.isStopping = false
                if results.allSatisfy({ $0 != .failed }) {
                    WindowWorkspaceRegistry.shared.setMutationLocked(false)
                    completion(true)
                } else {
                    WindowWorkspaceRegistry.shared.setMutationLocked(false)
                    let failure = NSAlert()
                    failure.messageText = "Terminal cleanup did not complete"
                    failure.informativeText = "gabCode remains open because one or more owned terminal sessions could not be verified as stopped."
                    failure.alertStyle = .critical
                    failure.runModal()
                    completion(false)
                }
            }
        }
    }
}

@MainActor
final class GabCodeAppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        let workspaceURLs = ProcessInfo.processInfo.arguments.dropFirst().compactMap { argument -> URL? in
            let url = URL(fileURLWithPath: argument)
            return url.pathExtension.caseInsensitiveCompare("gabcode-workspace") == .orderedSame
                ? url.standardizedFileURL
                : nil
        }
        guard !workspaceURLs.isEmpty else { return }
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.application(NSApp, open: workspaceURLs)
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        TerminalShutdownCoordinator.shared.requestApplicationTermination(sender)
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        let workspaceURLs = urls.filter {
            $0.pathExtension.caseInsensitiveCompare("gabcode-workspace") == .orderedSame
        }
        guard !workspaceURLs.isEmpty else { return }

        guard let targetWindow = application.keyWindow ?? application.windows.first else {
            for workspaceURL in workspaceURLs {
                WorkspaceWindowIntentStore.shared.enqueueOpen(workspaceURL)
                requestNewWindow(in: application)
            }
            return
        }

        for workspaceURL in workspaceURLs {
            NotificationCenter.default.post(
                name: .gabCodeOpenWorkspace,
                object: nil,
                userInfo: [
                    "windowNumber": targetWindow.windowNumber,
                    "workspaceURL": workspaceURL.standardizedFileURL
                ]
            )
        }
    }

    private func requestNewWindow(in application: NSApplication) {
        guard
            let fileMenu = application.mainMenu?.item(withTitle: "File")?.submenu,
            let newWindowItem = fileMenu.items.first(where: { $0.title == "New Window" && $0.isEnabled }),
            let action = newWindowItem.action
        else { return }
        application.sendAction(action, to: newWindowItem.target, from: newWindowItem)
    }
}

@MainActor
struct WindowCloseInterceptor: NSViewRepresentable {
    let registry: WorkspaceTerminalRegistry
    let selectedPath: URL

    func makeCoordinator() -> Coordinator { Coordinator(registry: registry) }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        context.coordinator.install(on: view, selectedPath: selectedPath)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        context.coordinator.install(on: view, selectedPath: selectedPath)
    }

    final class Coordinator: NSObject, NSWindowDelegate {
        private weak var window: NSWindow?
        private let registry: WorkspaceTerminalRegistry

        init(registry: WorkspaceTerminalRegistry) { self.registry = registry }

        func install(on view: NSView, selectedPath: URL) {
            DispatchQueue.main.async { [weak self, weak view] in
                guard let self, let window = view?.window else { return }
                WindowWorkspaceRegistry.shared.register(self.registry.retainedPresentations, for: window)
                WindowWorkspaceRegistry.shared.select(self.registry.presentation(for: selectedPath), for: window)
                if self.window !== window {
                    self.window = window
                    window.delegate = self
                }
            }
        }

        func windowShouldClose(_ sender: NSWindow) -> Bool {
            TerminalShutdownCoordinator.shared.requestWindowClose(sender)
        }

        func windowWillClose(_ notification: Notification) {
            if let window { WindowWorkspaceRegistry.shared.unregister(window) }
        }
    }
}
