import XCTest

final class gabCodeUITests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments = ["-ApplePersistenceIgnoreState", "YES", "-NSQuitAlwaysKeepsWindows", "NO"]
    }

    override func tearDownWithError() throws {
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
    func testMissingTerminalDirectoryShowsExplanationWithoutStartingTerminalWorkspace() throws {
        app.launch()

        let window = app.windows["gabCode"]
        XCTAssertTrue(window.waitForExistence(timeout: 5), "Expected an accessible window named gabCode.")
        XCTAssertTrue(
            app.staticTexts["terminal-directory-required"].waitForExistence(timeout: 5),
            "Expected the native controlled-directory explanation."
        )
        XCTAssertFalse(app.groups["terminal-workspace"].exists, "Missing input must not create terminal hosts.")

        app.typeKey("q", modifierFlags: .command)
        let stopped = expectation(
            for: NSPredicate(format: "state == %d", XCUIApplication.State.notRunning.rawValue),
            evaluatedWith: app
        )
        XCTAssertEqual(XCTWaiter.wait(for: [stopped], timeout: 5), .completed, "No-session Command-Q must remain standard.")
    }

    @MainActor
    func testExplicitDirectoryShowsTwoGenericTerminalsAndSwapCommandRetainsWorkspace() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        app.launchArguments += ["--terminal-directory", directory.path]
        app.launch()

        XCTAssertTrue(app.groups["terminal-workspace"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.groups["terminal-1-region"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.groups["terminal-2-region"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["swap-terminals"].waitForExistence(timeout: 5))
        let initialMainHeight = app.groups["terminal-1-region"].frame.height
        let initialBottomHeight = app.groups["terminal-2-region"].frame.height
        let initialSplitRatio = initialMainHeight / initialBottomHeight
        XCTAssertTrue(
            (1.8...2.2).contains(initialSplitRatio),
            "Expected an approximately 2:1 default terminal split, got \(initialSplitRatio):1."
        )
        let mainTerminalLabel = app.staticTexts["main-terminal-label"]
        XCTAssertEqual(mainTerminalLabel.value as? String, "Main: Terminal 1")

        app.typeKey("t", modifierFlags: [.command, .shift])

        let swapped = expectation(
            for: NSPredicate(format: "value == %@", "Main: Terminal 2"),
            evaluatedWith: mainTerminalLabel
        )
        XCTAssertEqual(XCTWaiter.wait(for: [swapped], timeout: 5), .completed, "Command-Shift-T did not swap retained terminal placement.")
        XCTAssertTrue(app.groups["terminal-1-region"].exists)
        XCTAssertTrue(app.groups["terminal-2-region"].exists)

        let terminal1Host = app.descendants(matching: .any)["terminal-1-host"]
        let terminal2Host = app.descendants(matching: .any)["terminal-2-host"]
        XCTAssertTrue(terminal1Host.exists)
        XCTAssertTrue(terminal2Host.exists)

        app.typeKey("1", modifierFlags: .command)
        let terminal1Marker = directory.appendingPathComponent("terminal 1 focused.txt")
        app.typeText("printf terminal1 > 'terminal 1 focused.txt'\n")
        waitForFile(terminal1Marker, message: "Command-1 did not route ordinary shell input to Terminal 1.")

        app.typeKey("2", modifierFlags: .command)
        let terminal2Marker = directory.appendingPathComponent("terminal 2 focused.txt")
        app.typeText("printf terminal2 > 'terminal 2 focused.txt'\n")
        waitForFile(terminal2Marker, message: "Command-2 did not route ordinary shell input to Terminal 2.")
    }

    @MainActor
    func testActiveCloseAndQuitRequireConfirmationAndCancelPreservesTerminals() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        app.launchArguments += ["--terminal-directory", directory.path]
        app.launch()
        XCTAssertTrue(app.groups["terminal-workspace"].waitForExistence(timeout: 5))

        app.typeKey("w", modifierFlags: .command)
        XCTAssertTrue(app.staticTexts["Stop 2 active terminals?"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.sheets.buttons["Cancel"].exists)
        XCTAssertTrue(app.sheets.buttons["Close and Stop Terminals"].exists)
        let mainTerminalLabel = app.staticTexts["main-terminal-label"]
        XCTAssertEqual(mainTerminalLabel.value as? String, "Main: Terminal 1")
        XCTAssertFalse(app.buttons["swap-terminals"].isEnabled, "Cleanup confirmation must lock placement mutation.")
        app.typeKey("t", modifierFlags: [.command, .shift])
        XCTAssertEqual(mainTerminalLabel.value as? String, "Main: Terminal 1", "A cleanup lock must reject Command-Shift-T.")
        app.typeKey("q", modifierFlags: .command)
        XCTAssertTrue(app.sheets.buttons["Cancel"].exists, "Duplicate quit must preserve the original confirmation.")
        app.sheets.buttons["Cancel"].click()
        XCTAssertTrue(app.groups["terminal-workspace"].exists, "Cancel must preserve the workspace.")
        XCTAssertTrue(app.buttons["swap-terminals"].isEnabled, "Cancel must release the placement mutation lock.")
        app.typeKey("t", modifierFlags: [.command, .shift])
        let swappedAfterCancel = expectation(
            for: NSPredicate(format: "value == %@", "Main: Terminal 2"),
            evaluatedWith: mainTerminalLabel
        )
        XCTAssertEqual(XCTWaiter.wait(for: [swappedAfterCancel], timeout: 5), .completed)

        app.typeKey("q", modifierFlags: .command)
        XCTAssertTrue(app.staticTexts["Stop 2 active terminals?"].waitForExistence(timeout: 5))
        app.sheets.buttons["Cancel"].click()
        let marker = directory.appendingPathComponent("cancel preserved terminal.txt")
        app.typeKey("1", modifierFlags: .command)
        app.typeText("printf alive > 'cancel preserved terminal.txt'\n")
        waitForFile(marker, message: "Cancel must preserve Terminal 1 and its focus route.")
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
