import AppKit
import Combine
import SwiftUI

@MainActor
final class TerminalShutdownCoordinator: ObservableObject {
    static let shared = TerminalShutdownCoordinator()

    @Published private(set) var isStopping = false
    private weak var presentation: TerminalWorkspacePresentation?
    private weak var pendingWindow: NSWindow?
    private var permitsWindowClose = false

    private init() {}

    func connect(_ presentation: TerminalWorkspacePresentation) {
        self.presentation = presentation
    }

    func disconnect(_ presentation: TerminalWorkspacePresentation) {
        guard self.presentation === presentation else {
            return
        }
        self.presentation = nil
    }

    func requestWindowClose(_ window: NSWindow) -> Bool {
        if permitsWindowClose {
            return true
        }
        guard activeTerminalCount > 0 else {
            return true
        }
        requestConfirmation(for: window) { [weak self, weak window] cleanedUp in
            guard cleanedUp, let self, let window else {
                return
            }
            self.permitsWindowClose = true
            window.performClose(nil)
            self.permitsWindowClose = false
        }
        return false
    }

    func requestApplicationTermination(_ application: NSApplication) -> NSApplication.TerminateReply {
        guard pendingWindow == nil, !isStopping else {
            return .terminateCancel
        }
        guard activeTerminalCount > 0 else {
            return .terminateNow
        }
        guard let window = application.keyWindow ?? application.windows.first else {
            return .terminateCancel
        }
        requestConfirmation(for: window) { cleanedUp in
            application.reply(toApplicationShouldTerminate: cleanedUp)
        }
        return .terminateLater
    }

    private var activeTerminalCount: Int {
        presentation?.activeTerminalCount ?? 0
    }

    private func requestConfirmation(for window: NSWindow, completion: @escaping (Bool) -> Void) {
        guard !isStopping, pendingWindow == nil, let presentation else {
            return
        }
        presentation.setMutationLocked(true)
        pendingWindow = window
        let responder = window.firstResponder
        let alert = NSAlert()
        alert.messageText = "Stop \(activeTerminalCount) active terminals?"
        alert.informativeText = "Running shell work will be interrupted."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Close and Stop Terminals")
        alert.beginSheetModal(for: window) { [weak self, weak window, weak presentation] response in
            guard let self, let presentation else {
                presentation?.setMutationLocked(false)
                completion(false)
                return
            }
            self.pendingWindow = nil
            guard response == NSApplication.ModalResponse(rawValue: 1001) else {
                presentation.setMutationLocked(false)
                if let window, let responder {
                    window.makeFirstResponder(responder)
                }
                completion(false)
                return
            }
            guard self.presentation === presentation else {
                presentation.setMutationLocked(false)
                completion(false)
                return
            }
            self.isStopping = true
            Task {
                let results = await presentation.workspace.stopResults(gracePeriod: .milliseconds(500))
                self.isStopping = false
                if results.allSatisfy({ $0 != .failed }) {
                    completion(true)
                } else {
                    presentation.setMutationLocked(false)
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
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        TerminalShutdownCoordinator.shared.requestApplicationTermination(sender)
    }
}

@MainActor
struct WindowCloseInterceptor: NSViewRepresentable {
    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        context.coordinator.install(on: view)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        context.coordinator.install(on: view)
    }

    final class Coordinator: NSObject, NSWindowDelegate {
        private weak var window: NSWindow?

        func install(on view: NSView) {
            DispatchQueue.main.async { [weak self, weak view] in
                guard let self, let window = view?.window, self.window !== window else {
                    return
                }
                self.window = window
                window.delegate = self
            }
        }

        func windowShouldClose(_ sender: NSWindow) -> Bool {
            TerminalShutdownCoordinator.shared.requestWindowClose(sender)
        }
    }
}
