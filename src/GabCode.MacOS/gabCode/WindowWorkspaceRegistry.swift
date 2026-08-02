import AppKit

@MainActor
final class WindowWorkspaceRegistry {
    static let shared = WindowWorkspaceRegistry()

    private struct Entry {
        weak var window: NSWindow?
        let presentation: TerminalWorkspacePresentation
    }

    private var entries: [Entry] = []

    var presentations: [TerminalWorkspacePresentation] {
        prune()
        return entries.map(\.presentation)
    }

    var activeTerminalCount: Int {
        presentations.reduce(0) { $0 + $1.activeTerminalCount }
    }

    func register(_ presentation: TerminalWorkspacePresentation, for window: NSWindow) {
        prune()
        entries.removeAll { $0.window === window || $0.presentation === presentation }
        entries.append(Entry(window: window, presentation: presentation))
    }

    func unregister(_ window: NSWindow) {
        entries.removeAll { $0.window == nil || $0.window === window }
    }

    func presentation(for window: NSWindow?) -> TerminalWorkspacePresentation? {
        prune()
        guard let window else { return nil }
        return entries.first { $0.window === window }?.presentation
    }

    func focusedPresentation() -> TerminalWorkspacePresentation? {
        presentation(for: NSApp.keyWindow)
    }

    func setMutationLocked(_ locked: Bool) {
        presentations.forEach { $0.setMutationLocked(locked) }
    }

    private func prune() {
        entries.removeAll { $0.window == nil }
    }
}
