import Darwin
import XCTest

final class gabCodeUITests: XCTestCase {
    private var app: XCUIApplication!
    private var preferenceSuiteName: String!

    override func setUpWithError() throws {
        continueAfterFailure = false
        preferenceSuiteName = "gabCode.UITests.TerminalFont.\(UUID().uuidString)"
        app = XCUIApplication()
        app.launchArguments = ["-ApplePersistenceIgnoreState", "YES", "-NSQuitAlwaysKeepsWindows", "NO"]
        app.launchEnvironment["GABCODE_UI_TEST_PREFERENCE_SUITE"] = preferenceSuiteName
    }

    override func tearDownWithError() throws {
        defer {
            if let preferenceSuiteName {
                UserDefaults(suiteName: preferenceSuiteName)?
                    .removePersistentDomain(forName: preferenceSuiteName)
            }
        }

        guard let app, app.state != .notRunning else {
            return
        }

        if app.sheets.buttons["Close and Stop Terminals"].exists == false {
            app.typeKey("q", modifierFlags: .command)
        }
        if app.sheets.buttons["Close and Stop Terminals"].waitForExistence(timeout: 2) {
            app.sheets.buttons["Close and Stop Terminals"].click()
        }
        let stopped = expectation(
            for: NSPredicate(format: "state == %d", XCUIApplication.State.notRunning.rawValue),
            evaluatedWith: app
        )
        XCTAssertEqual(XCTWaiter.wait(for: [stopped], timeout: 8), .completed, "gabCode did not terminate within bounded cleanup time.")
    }

    @MainActor
    func testLaunchWithoutWorkspaceShowsEmptyProjectSurface() throws {
        app.launch()

        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["open-workspace"].exists)
        XCTAssertTrue(app.buttons["create-workspace"].exists)
        XCTAssertFalse(app.groups["terminal-workspace"].exists)
    }

    @MainActor
    func testExplicitDirectoryDoesNotBypassWorkspaceValidation() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        app.launchArguments += ["--terminal-directory", directory.path]
        app.launch()

        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.groups["terminal-workspace"].exists)
    }

    @MainActor
    func testCommandNCreatesIndependentEmptyWorkspaceWindow() throws {
        app.launch()

        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        app.typeKey("n", modifierFlags: .command)

        let workspaces = app.buttons.matching(identifier: "open-workspace")
        let secondWorkspace = workspaces.element(boundBy: 1)
        XCTAssertTrue(secondWorkspace.waitForExistence(timeout: 5), "Command-N must create a second native workspace window.")
        XCTAssertGreaterThanOrEqual(app.windows.count, 2)
        XCTAssertEqual(app.buttons.matching(identifier: "open-workspace").count, 2)
        XCTAssertEqual(app.buttons.matching(identifier: "create-workspace").count, 2)
    }

    @MainActor
    func testCommandCommaOpensAccessibleTerminalFontSettings() throws {
        app.launch()
        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))

        app.typeKey(",", modifierFlags: .command)
        XCTAssertTrue(app.staticTexts["Terminal font"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.popUpButtons["terminal-font-face"].exists)
        let pointSize = app.textFields["terminal-font-size"]
        XCTAssertTrue(pointSize.exists)
        XCTAssertTrue(app.descendants(matching: .any)["terminal-font-preview"].exists)
        XCTAssertTrue(app.buttons["Restore System Default"].exists)

        let effectiveValue = app.staticTexts["terminal-font-effective-value"]
        pointSize.click()
        pointSize.typeKey("a", modifierFlags: .command)
        pointSize.typeText("24")
        let changedImmediately = expectation(
            for: NSPredicate(format: "value CONTAINS %@", "24 pt"),
            evaluatedWith: effectiveValue
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [changedImmediately], timeout: 5),
            .completed,
            "A valid point-size edit must update the effective preference without Return or Apply."
        )

        pointSize.typeKey("a", modifierFlags: .command)
        pointSize.typeText("7")
        XCTAssertTrue(
            (effectiveValue.value as? String)?.contains("24 pt") == true,
            "An invalid intermediate edit must not replace the last effective size."
        )
        pointSize.typeKey(.return, modifierFlags: [])
        XCTAssertEqual(pointSize.value as? String, "24")
    }

    @MainActor
    func testCloseWithoutWorkspaceDoesNotOfferTerminalCleanup() throws {
        app.launch()
        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        app.typeKey("w", modifierFlags: .command)
        XCTAssertFalse(app.staticTexts["Stop 2 active terminals?"].waitForExistence(timeout: 2))
    }

    private var loginHomeDirectory: URL {
        guard let passwordEntry = getpwuid(getuid()) else {
            fatalError("The current user's home directory could not be resolved.")
        }
        return URL(
            fileURLWithPath: String(cString: passwordEntry.pointee.pw_dir),
            isDirectory: true
        ).standardizedFileURL
    }

    private func waitForFile(_ file: URL, message: String) {
        let fileExists = expectation(
            for: NSPredicate { _, _ in FileManager.default.fileExists(atPath: file.path) },
            evaluatedWith: NSObject()
        )
        XCTAssertEqual(XCTWaiter.wait(for: [fileExists], timeout: 5), .completed, message)
    }

    private func makeTemporaryDirectory() throws -> URL {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode UI workspace ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
}
