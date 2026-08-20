import AppKit
import SwiftUI

enum WorkspaceWindowLayout {
    static let minimumWidth: CGFloat = 760
    static let minimumHeight: CGFloat = 640
    static let sidebarMinimumWidth: CGFloat = 190
    static let sidebarIdealWidth: CGFloat = 240
    static let sidebarMaximumWidth: CGFloat = 300
    static let dividerWidth: CGFloat = 1
    static let terminalMinimumWidth = minimumWidth - sidebarMinimumWidth - dividerWidth
    static let defaultWidth = minimumWidth
    static let defaultHeight: CGFloat = 720

    static func sidebarWidth(for windowWidth: CGFloat) -> CGFloat {
        min(
            sidebarMaximumWidth,
            max(sidebarMinimumWidth, windowWidth - terminalMinimumWidth - dividerWidth)
        )
    }

    static func fittedFrame(_ frame: NSRect, in visibleFrame: NSRect) -> NSRect {
        let fittedSize = NSSize(
            width: min(frame.width, visibleFrame.width),
            height: min(frame.height, visibleFrame.height)
        )
        let fittedOrigin = NSPoint(
            x: min(max(frame.minX, visibleFrame.minX), visibleFrame.maxX - fittedSize.width),
            y: min(max(frame.minY, visibleFrame.minY), visibleFrame.maxY - fittedSize.height)
        )
        return NSRect(origin: fittedOrigin, size: fittedSize)
    }

    static func applyMinimumSize(to window: NSWindow) {
        window.contentMinSize = NSSize(width: minimumWidth, height: minimumHeight)
        guard !window.styleMask.contains(.fullScreen), let screen = window.screen ?? NSScreen.main else { return }

        let fittedFrame = fittedFrame(window.frame, in: screen.visibleFrame)
        guard fittedFrame != window.frame else { return }
        window.setFrame(fittedFrame, display: true)
    }
}

@MainActor
struct WindowMinimumSizeBridge: NSViewRepresentable {
    final class Coordinator {
        weak var configuredWindow: NSWindow?
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        apply(to: view, coordinator: context.coordinator)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        apply(to: view, coordinator: context.coordinator)
    }

    private func apply(to view: NSView, coordinator: Coordinator) {
        DispatchQueue.main.async {
            guard let window = view.window, coordinator.configuredWindow !== window else { return }
            coordinator.configuredWindow = window
            WorkspaceWindowLayout.applyMinimumSize(to: window)
        }
    }
}
