import AppKit
import SwiftUI

enum WorkspaceWindowLayout {
    static let sidebarMinimumWidth: CGFloat = 190
    static let sidebarIdealWidth: CGFloat = 240
    static let sidebarMaximumWidth: CGFloat = 300
    static let terminalMinimumWidth: CGFloat = 790
    static let dividerWidth: CGFloat = 1
    static let minimumWidth = sidebarMinimumWidth + dividerWidth + terminalMinimumWidth
    static let minimumHeight: CGFloat = 640
    static let defaultWidth = sidebarIdealWidth + dividerWidth + terminalMinimumWidth
    static let defaultHeight: CGFloat = 720

    static func sidebarWidth(for windowWidth: CGFloat) -> CGFloat {
        min(
            sidebarMaximumWidth,
            max(sidebarMinimumWidth, windowWidth - terminalMinimumWidth - dividerWidth)
        )
    }

    static func applyMinimumSize(to window: NSWindow) {
        window.contentMinSize = NSSize(width: minimumWidth, height: minimumHeight)
    }
}

@MainActor
struct WindowMinimumSizeBridge: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        apply(to: view)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        apply(to: view)
    }

    private func apply(to view: NSView) {
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            WorkspaceWindowLayout.applyMinimumSize(to: window)
        }
    }
}
