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
        XCTAssertEqual(registry.activeTerminalCount, 4)

        registry.unregister(firstWindow)
        XCTAssertNil(registry.presentation(for: firstWindow))
        XCTAssertTrue(registry.presentation(for: secondWindow) === second)
        XCTAssertEqual(registry.activeTerminalCount, 2)
    }

    private func presentation() throws -> TerminalWorkspacePresentation {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return TerminalWorkspacePresentation(workspace: TerminalWorkspace(workingDirectory: directory), workingDirectory: directory)
    }
}
