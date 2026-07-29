import XCTest

final class gabCodeUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launch()
    }

    override func tearDownWithError() throws {
        guard let app else {
            return
        }

        app.terminate()

        let stopped = expectation(
            for: NSPredicate(format: "state == %d", XCUIApplication.State.notRunning.rawValue),
            evaluatedWith: app
        )
        XCTAssertEqual(XCTWaiter.wait(for: [stopped], timeout: 5), .completed, "gabCode did not terminate within five seconds.")
    }

    @MainActor
    func testGabCodeWindowHasAccessibleIdentityAndStandardCloseQuitShortcuts() throws {
        let window = app.windows["gabCode"]
        XCTAssertTrue(window.waitForExistence(timeout: 5), "Expected an accessible window named gabCode.")
        XCTAssertTrue(app.staticTexts["macOS foundation ready."].waitForExistence(timeout: 5), "Expected the intentional macOS bootstrap message.")

        app.typeKey("w", modifierFlags: .command)
        let closed = expectation(for: NSPredicate(format: "exists == 0"), evaluatedWith: window)
        XCTAssertEqual(XCTWaiter.wait(for: [closed], timeout: 5), .completed, "Command-W did not close the active window.")
        XCTAssertNotEqual(app.state, .notRunning, "Command-W unexpectedly terminated gabCode.")

        app.typeKey("q", modifierFlags: .command)
        let stopped = expectation(
            for: NSPredicate(format: "state == %d", XCUIApplication.State.notRunning.rawValue),
            evaluatedWith: app
        )
        XCTAssertEqual(XCTWaiter.wait(for: [stopped], timeout: 5), .completed, "Command-Q did not terminate gabCode.")
    }
}
