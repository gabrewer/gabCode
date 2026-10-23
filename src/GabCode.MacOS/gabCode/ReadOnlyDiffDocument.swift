import AppKit
import Combine
import Foundation
import SwiftUI

@MainActor
final class ReadOnlyDiffDocumentState: ObservableObject {
    @Published private(set) var selectedFile: ReviewFile?
    @Published private(set) var revision = 0
    @Published private(set) var content = ""
    @Published private(set) var reviewPair: ReviewDocument?
    @Published private(set) var hasLoadedContent = false
    @Published private(set) var viewerHost: MacDiffViewerHost?
    @Published private(set) var changePosition = "No text changes"
    @Published private(set) var canJumpToDeletion = false
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var wantsFocus = false
    @Published private(set) var selectedBottomTerminal: TerminalWorkspaceTerminal = .terminal2
    private(set) var restoreTerminal: TerminalWorkspaceTerminal?

    var isOpen: Bool { selectedFile != nil }

    func open(_ file: ReviewFile, mainTerminal: TerminalWorkspaceTerminal) {
        if !isOpen { restoreTerminal = mainTerminal }
        let replacing = selectedFile?.id != file.id
        if replacing {
            viewerHost?.close()
            viewerHost = MacDiffViewerHost()
            reviewPair = nil
        }
        selectedFile = file
        revision += 1
        errorMessage = nil
        content = ""
        hasLoadedContent = false
        isLoading = true
        wantsFocus = true
        selectedBottomTerminal = restoreTerminal ?? mainTerminal
    }

    func reload() {
        guard selectedFile != nil else { return }
        revision += 1
        isLoading = true
        errorMessage = nil
        wantsFocus = false
    }

    func apply(_ pair: ReviewDocument) {
        guard isOpen else { return }
        reviewPair = pair
        isLoading = false
        hasLoadedContent = true
        errorMessage = nil
    }

    func updateViewerEvent(_ event: [String: Any]) {
        guard let pair = reviewPair, let identity = event["identity"] as? String, identity == pair.identity else { return }
        if event["kind"] as? String == "failure", let message = event["message"] as? String {
            errorMessage = message
        } else if event["kind"] as? String == "progress" {
            let index = event["index"] as? Int ?? 0
            let total = event["total"] as? Int ?? 0
            let description = event["description"] as? String ?? ""
            changePosition = "\(index) of \(total) · \(description)"
            canJumpToDeletion = event["deletion"] as? Bool == true
        }
    }

    func retry() {
        guard selectedFile != nil else { return }
        if errorMessage != nil {
            viewerHost?.close()
            viewerHost = MacDiffViewerHost()
        }
        reload()
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
        hasLoadedContent = result.state == .patch
        errorMessage = result.state == .patch ? nil : result.text
    }

    func fail(_ message: String) {
        guard isOpen else { return }
        isLoading = false
        hasLoadedContent = reviewPair != nil
        errorMessage = message
    }

    func close() {
        viewerHost?.close()
        viewerHost = nil
        reviewPair = nil
        selectedFile = nil
        revision += 1
        content = ""
        hasLoadedContent = false
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
        if view.string != text {
            let selection = view.selectedRange()
            let visibleOrigin = scroll.contentView.bounds.origin
            view.string = text
            let length = (text as NSString).length
            let start = min(selection.location, length)
            view.setSelectedRange(NSRange(location: start, length: min(selection.length, length - start)))
            if let container = view.textContainer { view.layoutManager?.ensureLayout(for: container) }
            let maxY = max(0, (scroll.documentView?.frame.height ?? 0) - scroll.contentView.bounds.height)
            scroll.contentView.scroll(to: NSPoint(x: visibleOrigin.x, y: min(visibleOrigin.y, maxY)))
            scroll.reflectScrolledClipView(scroll.contentView)
        }
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
