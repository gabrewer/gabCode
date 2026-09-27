import AppKit
import Combine
import Foundation
import SwiftUI

@MainActor
final class ReadOnlyDiffDocumentState: ObservableObject {
    @Published private(set) var selectedFile: ReviewFile?
    @Published private(set) var content = ""
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var wantsFocus = false
    @Published private(set) var selectedBottomTerminal: TerminalWorkspaceTerminal = .terminal2
    private(set) var restoreTerminal: TerminalWorkspaceTerminal?

    var isOpen: Bool { selectedFile != nil }

    func open(_ file: ReviewFile, mainTerminal: TerminalWorkspaceTerminal) {
        if !isOpen { restoreTerminal = mainTerminal }
        selectedFile = file
        errorMessage = nil
        content = ""
        isLoading = true
        wantsFocus = true
        selectedBottomTerminal = restoreTerminal ?? mainTerminal
    }

    func selectBottom(_ terminal: TerminalWorkspaceTerminal) {
        selectedBottomTerminal = terminal
        wantsFocus = false
    }

    func didFocus() { wantsFocus = false }

    func apply(_ result: ReviewDiff) {
        guard isOpen else { return }
        isLoading = false
        content = result.text
        errorMessage = result.state == .patch ? nil : result.text
    }

    func fail(_ message: String) {
        guard isOpen else { return }
        isLoading = false
        content = ""
        errorMessage = message
    }

    func close() {
        selectedFile = nil
        content = ""
        errorMessage = nil
        isLoading = false
        wantsFocus = false
        selectedBottomTerminal = .terminal2
    }
}

enum ReviewCurrentFileLocator {
    static func existingFile(for file: ReviewFile, selectedWorktree: URL) -> URL? {
        guard file.worktree.standardizedFileURL == selectedWorktree.standardizedFileURL,
              !file.path.isEmpty, !file.path.hasPrefix("/"),
              !file.path.split(separator: "/").contains("..") else { return nil }
        let root = selectedWorktree.resolvingSymlinksInPath().standardizedFileURL
        let candidate = selectedWorktree.appendingPathComponent(file.path).resolvingSymlinksInPath().standardizedFileURL
        guard candidate.path.hasPrefix(root.path + "/") else { return nil }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory), !isDirectory.boolValue else { return nil }
        return candidate
    }
}

@MainActor
struct ReadOnlyUnifiedDiffView: NSViewRepresentable {
    let text: String
    let requestFocus: Bool
    let didFocus: () -> Void

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView(frame: .zero)
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        let view = ReadOnlyDiffTextView.make(text: text)
        view.isHorizontallyResizable = true
        view.isVerticallyResizable = true
        view.textContainer?.widthTracksTextView = false
        view.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        scroll.documentView = view
        focusIfRequested(scroll)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let view = scroll.documentView as? NSTextView else { return }
        if view.string != text { view.string = text }
        focusIfRequested(scroll)
    }

    private func focusIfRequested(_ scroll: NSScrollView) {
        guard requestFocus else { return }
        DispatchQueue.main.async {
            guard let window = scroll.window, let view = scroll.documentView, window.makeFirstResponder(view) else { return }
            didFocus()
        }
    }
}

@MainActor
struct ReadOnlyDiffAnnouncement: NSViewRepresentable {
    let message: String

    final class Coordinator { var previous: String? }
    func makeCoordinator() -> Coordinator { Coordinator() }
    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        announce(from: view, coordinator: context.coordinator)
        return view
    }
    func updateNSView(_ view: NSView, context: Context) { announce(from: view, coordinator: context.coordinator) }
    private func announce(from view: NSView, coordinator: Coordinator) {
        guard coordinator.previous != message else { return }
        coordinator.previous = message
        DispatchQueue.main.async {
            NSAccessibility.post(element: view, notification: .announcementRequested,
                                 userInfo: [.announcement: message, .priority: 50])
        }
    }
}

@MainActor
struct ReadOnlyDiffTextView {
    static func make(text: String) -> NSTextView {
        let view = NSTextView(frame: .zero)
        view.isEditable = false
        view.isSelectable = true
        view.isRichText = false
        view.allowsUndo = false
        view.usesFindBar = false
        view.font = .monospacedSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)
        view.string = text
        view.setAccessibilityRole(.textArea)
        view.setAccessibilityLabel("Read-only unified diff")
        view.setAccessibilityHelp("Review content only. Source files and Git state are not modified here.")
        return view
    }
}
