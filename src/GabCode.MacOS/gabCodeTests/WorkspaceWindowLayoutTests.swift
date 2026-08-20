import AppKit
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceWindowLayoutTests: XCTestCase {
    func testProjectWindowMinimumIsTheCompleteSidebarAndTerminalWidth() {
        XCTAssertEqual(WorkspaceWindowLayout.minimumWidth, 760)
        XCTAssertGreaterThanOrEqual(WorkspaceWindowLayout.defaultWidth, WorkspaceWindowLayout.minimumWidth)
        XCTAssertEqual(
            WorkspaceWindowLayout.minimumWidth,
            WorkspaceWindowLayout.sidebarMinimumWidth + WorkspaceWindowLayout.dividerWidth + WorkspaceWindowLayout.terminalMinimumWidth
        )
        XCTAssertGreaterThanOrEqual(WorkspaceWindowLayout.defaultHeight, WorkspaceWindowLayout.minimumHeight)
    }

    func testNativeWindowReceivesTheCompleteProjectMinimumSize() {
        let window = NSWindow()
        window.contentMinSize = .zero

        WorkspaceWindowLayout.applyMinimumSize(to: window)

        XCTAssertEqual(window.contentMinSize.width, WorkspaceWindowLayout.minimumWidth)
        XCTAssertEqual(window.contentMinSize.height, WorkspaceWindowLayout.minimumHeight)
    }

    func testSidebarWidthClampsToTheAvailableProjectWindowWidth() {
        XCTAssertEqual(
            WorkspaceWindowLayout.sidebarWidth(for: WorkspaceWindowLayout.minimumWidth),
            WorkspaceWindowLayout.sidebarMinimumWidth
        )
        XCTAssertEqual(
            WorkspaceWindowLayout.sidebarWidth(
                for: WorkspaceWindowLayout.sidebarIdealWidth
                    + WorkspaceWindowLayout.dividerWidth
                    + WorkspaceWindowLayout.terminalMinimumWidth
            ),
            WorkspaceWindowLayout.sidebarIdealWidth
        )
        XCTAssertEqual(
            WorkspaceWindowLayout.sidebarWidth(for: WorkspaceWindowLayout.defaultWidth + 2_000),
            WorkspaceWindowLayout.sidebarMaximumWidth
        )
    }

    func testTerminalAreaKeepsItsMinimumAfterTheSidebarAtWindowMinimum() {
        let width = WorkspaceWindowLayout.minimumWidth
        let remaining = width - WorkspaceWindowLayout.sidebarWidth(for: width) - WorkspaceWindowLayout.dividerWidth

        XCTAssertEqual(remaining, WorkspaceWindowLayout.terminalMinimumWidth)
    }

    func testFrameFittingMovesOnlyOverflowingRestoredWindowIntoVisibleFrame() {
        let visible = NSRect(x: 0, y: 0, width: 1_440, height: 900)
        let alreadyVisible = NSRect(x: 100, y: 100, width: 760, height: 640)
        let offscreenLeft = NSRect(x: -80, y: 100, width: 760, height: 640)

        XCTAssertEqual(WorkspaceWindowLayout.fittedFrame(alreadyVisible, in: visible), alreadyVisible)
        XCTAssertEqual(WorkspaceWindowLayout.fittedFrame(offscreenLeft, in: visible).minX, visible.minX)
    }
}
