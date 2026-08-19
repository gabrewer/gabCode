import AppKit
@testable import gabCode
import XCTest

@MainActor
final class WindowWorkspaceRegistryTests: XCTestCase {
    func testKeyWindowRoutesCommandsAndWindowCloseToItsOwnPresentation() async throws {
        let registry = WindowWorkspaceRegistry()
        let firstWindow = NSWindow()
        let secondWindow = NSWindow()
        let first = try presentation()
        let second = try presentation()

        registry.register(first, for: firstWindow)
        registry.register(second, for: secondWindow)
        await first.start()
        await second.start()
        defer {
            Task { await first.workspace.stop(gracePeriod: .milliseconds(250)) }
            Task { await second.workspace.stop(gracePeriod: .milliseconds(250)) }
        }

        XCTAssertTrue(registry.presentation(for: firstWindow) === first)
        XCTAssertTrue(registry.presentation(for: secondWindow) === second)
        registry.focus(secondWindow)
        XCTAssertTrue(registry.focusedPresentation === second)
        registry.resignFocus(secondWindow)
        XCTAssertNil(registry.focusedPresentation)
        XCTAssertEqual(registry.activeTerminalCount, 4)

        registry.unregister(firstWindow)
        XCTAssertNil(registry.presentation(for: firstWindow))
        XCTAssertTrue(registry.presentation(for: secondWindow) === second)
        XCTAssertEqual(registry.activeTerminalCount, 2)
    }

    func testOneWindowAggregatesRetainedPresentationsForCleanup() throws {
        let registry = WindowWorkspaceRegistry()
        let window = NSWindow()
        let first = try presentation()
        let second = try presentation()

        registry.register(first, for: window)
        registry.register(second, for: window)

        XCTAssertEqual(registry.presentations(for: window).count, 2)
        XCTAssertEqual(registry.presentations.count, 2)
        XCTAssertTrue(registry.presentation(for: window) === first)
        registry.unregister(window)
        XCTAssertTrue(registry.presentations(for: window).isEmpty)
    }

    func testSelectedPresentationRoutesFocusedWindowCommands() throws {
        let registry = WindowWorkspaceRegistry()
        let window = NSWindow()
        let first = try presentation()
        let second = try presentation()

        registry.register([first, second], for: window)
        registry.select(first, for: window)
        XCTAssertTrue(registry.presentation(for: window) === first)
        registry.focus(window)
        XCTAssertTrue(registry.focusedPresentation === first)

        registry.select(second, for: window)
        XCTAssertTrue(registry.presentation(for: window) === second)
        registry.focus(window)
        XCTAssertTrue(registry.focusedPresentation === second)
    }

    func testWorkspaceActionsStayInCurrentWindowOnlyWhenItIsEmpty() {
        XCTAssertFalse(WorkspaceWindowRouting.opensSeparateWindow(hasActiveProject: false))
        XCTAssertTrue(WorkspaceWindowRouting.opensSeparateWindow(hasActiveProject: true))
    }

    private func presentation() throws -> TerminalWorkspacePresentation {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return TerminalWorkspacePresentation(workspace: TerminalWorkspace(workingDirectory: directory), workingDirectory: directory)
    }
}
