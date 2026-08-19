import AppKit
import Combine

enum WorkspaceWindowRouting {
    static func opensSeparateWindow(hasActiveProject: Bool) -> Bool {
        hasActiveProject
    }
}

@MainActor
final class WindowWorkspaceRegistry {
    static let shared = WindowWorkspaceRegistry()

    private struct Entry {
        weak var window: NSWindow?
        var presentations: [TerminalWorkspacePresentation]
        var selectedPresentation: TerminalWorkspacePresentation?
    }

    private var entries: [Entry] = []
    @Published private(set) var focusedPresentation: TerminalWorkspacePresentation?

    init(notificationCenter: NotificationCenter = .default) {
        notificationCenter.addObserver(
            forName: NSWindow.didBecomeKeyNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let window = notification.object as? NSWindow else { return }
            Task { @MainActor in self?.focus(window) }
        }
        notificationCenter.addObserver(
            forName: NSWindow.didResignKeyNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let window = notification.object as? NSWindow else { return }
            Task { @MainActor in self?.resignFocus(window) }
        }
    }

    var presentations: [TerminalWorkspacePresentation] {
        prune()
        return entries.flatMap(\.presentations)
    }

    var activeTerminalCount: Int {
        presentations.reduce(0) { $0 + $1.activeTerminalCount }
    }

    func presentations(for window: NSWindow?) -> [TerminalWorkspacePresentation] {
        prune()
        guard let window else { return [] }
        return entries.first { $0.window === window }?.presentations ?? []
    }

    func register(_ presentation: TerminalWorkspacePresentation, for window: NSWindow) {
        register([presentation], for: window)
    }

    func register(_ presentations: [TerminalWorkspacePresentation], for window: NSWindow) {
        prune()
        if let index = entries.firstIndex(where: { $0.window === window }) {
            for presentation in presentations where !entries[index].presentations.contains(where: { $0 === presentation }) {
                entries[index].presentations.append(presentation)
            }
            if entries[index].selectedPresentation == nil {
                entries[index].selectedPresentation = presentations.last
            }
        } else {
            entries.append(Entry(window: window, presentations: presentations, selectedPresentation: presentations.last))
        }
        if window.isKeyWindow { focusedPresentation = presentation(for: window) }
    }

    func select(_ presentation: TerminalWorkspacePresentation, for window: NSWindow) {
        register(presentation, for: window)
        guard let index = entries.firstIndex(where: { $0.window === window }) else { return }
        entries[index].selectedPresentation = presentation
        if window.isKeyWindow { focusedPresentation = presentation }
    }

    func unregister(_ window: NSWindow) {
        if focusedPresentation === presentation(for: window) { focusedPresentation = nil }
        entries.removeAll { $0.window == nil || $0.window === window }
    }

    func focus(_ window: NSWindow) {
        focusedPresentation = presentation(for: window)
    }

    func resignFocus(_ window: NSWindow) {
        if focusedPresentation === presentation(for: window) { focusedPresentation = nil }
    }

    func presentation(for window: NSWindow?) -> TerminalWorkspacePresentation? {
        prune()
        guard let window else { return nil }
        return entries.first { $0.window === window }?.selectedPresentation
    }

    func currentFocusedPresentation() -> TerminalWorkspacePresentation? {
        presentation(for: NSApp.keyWindow) ?? focusedPresentation
    }

    func setMutationLocked(_ locked: Bool) {
        presentations.forEach { $0.setMutationLocked(locked) }
    }

    private func prune() {
        entries.removeAll { $0.window == nil }
    }
}
