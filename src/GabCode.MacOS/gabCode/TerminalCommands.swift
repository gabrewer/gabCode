import Combine
import SwiftUI

@MainActor
final class TerminalCommandRouter: ObservableObject {
    static let shared = TerminalCommandRouter()

    @Published private(set) var isAvailable = false
    private weak var presentation: TerminalWorkspacePresentation?
    private var mutationLockCancellable: AnyCancellable?

    private init() {}

    func connect(_ presentation: TerminalWorkspacePresentation) {
        self.presentation = presentation
        mutationLockCancellable = presentation.$isMutationLocked
            .map { !$0 }
            .removeDuplicates()
            .sink { [weak self] isAvailable in
                self?.isAvailable = isAvailable
            }
    }

    func disconnect(_ presentation: TerminalWorkspacePresentation) {
        guard self.presentation === presentation else {
            return
        }
        mutationLockCancellable?.cancel()
        mutationLockCancellable = nil
        self.presentation = nil
        isAvailable = false
    }

    func swapTerminals() {
        presentation?.swapTerminals()
    }

    func focusTerminal1() {
        presentation?.focus(.terminal1)
    }

    func focusTerminal2() {
        presentation?.focus(.terminal2)
    }
}

struct TerminalWorkspaceCommands: Commands {
    @ObservedObject private var router = TerminalCommandRouter.shared

    var body: some Commands {
        CommandMenu("Terminal") {
            Button("Swap Terminals") {
                router.swapTerminals()
            }
            .keyboardShortcut("t", modifiers: [.command, .shift])
            .disabled(!router.isAvailable)

            Divider()

            Button("Focus Terminal 1") {
                router.focusTerminal1()
            }
            .keyboardShortcut("1", modifiers: .command)
            .disabled(!router.isAvailable)

            Button("Focus Terminal 2") {
                router.focusTerminal2()
            }
            .keyboardShortcut("2", modifiers: .command)
            .disabled(!router.isAvailable)
        }
    }
}
