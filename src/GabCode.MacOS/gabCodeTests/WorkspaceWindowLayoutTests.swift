import AppKit
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceWindowLayoutTests: XCTestCase {
    func testProjectWindowDefaultAndMinimumAccommodateSidebarAndTerminal() {
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
            WorkspaceWindowLayout.sidebarWidth(for: WorkspaceWindowLayout.defaultWidth),
            WorkspaceWindowLayout.sidebarIdealWidth
        )
        XCTAssertEqual(
            WorkspaceWindowLayout.sidebarWidth(for: WorkspaceWindowLayout.defaultWidth + 2_000),
            WorkspaceWindowLayout.sidebarMaximumWidth
        )
    }

    func testTerminalAreaKeepsItsMinimumAfterTheSidebarAtWindowMinimum() {
        let width = WorkspaceWindowLayout.minimumWidth
        let remaining = width - WorkspaceWindowLayout.sidebarWidth(for: width)

        XCTAssertGreaterThanOrEqual(remaining, WorkspaceWindowLayout.terminalMinimumWidth)
    }
}
