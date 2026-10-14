import AppKit
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceWindowLayoutTests: XCTestCase {
    func testProjectWindowMinimumIsTheCompleteSidebarAndTerminalWidth() {
        XCTAssertGreaterThanOrEqual(WorkspaceWindowLayout.minimumWidth,
            WorkspaceWindowLayout.sidebarMaximumWidth + WorkspaceWindowLayout.dividerWidth
                + WorkspaceWindowLayout.terminalMinimumWidth + WorkspaceWindowLayout.dividerWidth
                + WorkspaceWindowLayout.reviewIdealWidth,
            "The native minimum must cover both sidebar layout proposals and the retained terminal minimum")
        XCTAssertGreaterThanOrEqual(WorkspaceWindowLayout.defaultWidth, WorkspaceWindowLayout.minimumWidth)
        XCTAssertEqual(
            WorkspaceWindowLayout.minimumWidth,
            WorkspaceWindowLayout.sidebarMaximumWidth + WorkspaceWindowLayout.dividerWidth + WorkspaceWindowLayout.terminalMinimumWidth
                + WorkspaceWindowLayout.dividerWidth + WorkspaceWindowLayout.reviewIdealWidth
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
            WorkspaceWindowLayout.sidebarMaximumWidth
        )
        XCTAssertEqual(
            WorkspaceWindowLayout.sidebarWidth(
                for: WorkspaceWindowLayout.sidebarIdealWidth
                    + WorkspaceWindowLayout.dividerWidth
                    + WorkspaceWindowLayout.terminalMinimumWidth
                    + WorkspaceWindowLayout.dividerWidth
                    + WorkspaceWindowLayout.reviewMinimumWidth
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
            - WorkspaceWindowLayout.reviewMinimumWidth - WorkspaceWindowLayout.dividerWidth

        XCTAssertGreaterThanOrEqual(remaining, WorkspaceWindowLayout.terminalMinimumWidth)
        XCTAssertGreaterThanOrEqual(width - WorkspaceWindowLayout.sidebarMaximumWidth - WorkspaceWindowLayout.dividerWidth
            - WorkspaceWindowLayout.reviewIdealWidth - WorkspaceWindowLayout.dividerWidth,
            WorkspaceWindowLayout.terminalMinimumWidth)
    }

    func testFrameFittingMovesOnlyOverflowingRestoredWindowIntoVisibleFrame() {
        let visible = NSRect(x: 0, y: 0, width: 1_440, height: 900)
        let alreadyVisible = NSRect(x: 100, y: 100, width: WorkspaceWindowLayout.minimumWidth, height: 640)
        let offscreenLeft = NSRect(x: -80, y: 100, width: WorkspaceWindowLayout.minimumWidth, height: 640)

        XCTAssertEqual(WorkspaceWindowLayout.fittedFrame(alreadyVisible, in: visible), alreadyVisible)
        XCTAssertEqual(WorkspaceWindowLayout.fittedFrame(offscreenLeft, in: visible).minX, visible.minX)
    }
}
