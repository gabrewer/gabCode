import Combine
import SwiftUI

@MainActor
final class TerminalCommandRouter: ObservableObject {
    static let shared = TerminalCommandRouter()

    @Published private(set) var isAvailable = false
    private var mutationLockCancellable: AnyCancellable?
    private var focusedPresentationCancellable: AnyCancellable?
    private weak var fallbackPresentation: TerminalWorkspacePresentation?

    private init() {
        focusedPresentationCancellable = WindowWorkspaceRegistry.shared.$focusedPresentation
            .sink { [weak self] presentation in
                self?.observeAvailability(for: presentation)
            }
    }

    func connect(_ presentation: TerminalWorkspacePresentation) {
        fallbackPresentation = presentation
        observeAvailability(for: WindowWorkspaceRegistry.shared.currentFocusedPresentation() ?? presentation)
    }

    private func observeAvailability(for presentation: TerminalWorkspacePresentation?) {
        mutationLockCancellable?.cancel()
        guard let presentation else {
            isAvailable = false
            return
        }
        mutationLockCancellable = presentation.$isMutationLocked
            .map { !$0 }
            .removeDuplicates()
            .sink { [weak self] isAvailable in self?.isAvailable = isAvailable }
    }

    func disconnect(_ presentation: TerminalWorkspacePresentation) {
        guard fallbackPresentation === presentation else {
            return
        }
        fallbackPresentation = nil
        mutationLockCancellable?.cancel()
        mutationLockCancellable = nil
        isAvailable = false
    }

    private var targetPresentation: TerminalWorkspacePresentation? {
        WindowWorkspaceRegistry.shared.currentFocusedPresentation() ?? fallbackPresentation
    }

    func swapTerminals() { targetPresentation?.swapTerminals() }
    func focusTerminal1() { targetPresentation?.focus(.terminal1) }
    func focusTerminal2() { targetPresentation?.focus(.terminal2) }
}

struct TerminalWorkspaceCommands: Commands {
    @Environment(\.openWindow) private var openWindow
    @ObservedObject private var router = TerminalCommandRouter.shared

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Window") { openWindow(id: "main") }
                .keyboardShortcut("n", modifiers: .command)
        }

        CommandGroup(after: .newItem) {
            Button("Open Workspace…") {
                WorkspaceWindowIntentStore.shared.enqueue(.open)
                openWindow(id: "main")
            }
            .keyboardShortcut("o", modifiers: .command)
            Button("Create Workspace from Git Folder…") {
                WorkspaceWindowIntentStore.shared.enqueue(.create)
                openWindow(id: "main")
            }
        }

        CommandMenu("Terminal") {
            Button("Swap Terminals") { router.swapTerminals() }
                .keyboardShortcut("t", modifiers: [.command, .shift])
                .disabled(!router.isAvailable)
            Divider()
            Button("Focus Terminal 1") { router.focusTerminal1() }
                .keyboardShortcut("1", modifiers: .command)
                .disabled(!router.isAvailable)
            Button("Focus Terminal 2") { router.focusTerminal2() }
                .keyboardShortcut("2", modifiers: .command)
                .disabled(!router.isAvailable)
        }
    }
}
