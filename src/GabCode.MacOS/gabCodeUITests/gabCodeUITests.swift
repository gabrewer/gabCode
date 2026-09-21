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
        let openWorkspace = app.buttons["open-workspace"]
        let createWorkspace = app.buttons["create-workspace"]
        XCTAssertTrue(openWorkspace.exists)
        XCTAssertEqual(openWorkspace.label, "Open Workspace")
        XCTAssertTrue(createWorkspace.exists)
        XCTAssertEqual(createWorkspace.label, "Create Workspace from Project Folder")
        XCTAssertFalse(app.groups["terminal-workspace"].exists)
    }

    @MainActor
    func testCommandOOpensWorkspaceChooserFromEmptyStartupSurface() throws {
        app.launch()
        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))

        app.typeKey("o", modifierFlags: .command)
        XCTAssertTrue(app.sheets.firstMatch.waitForExistence(timeout: 5))
        app.typeKey(.escape, modifierFlags: [])
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
    func testQuotedCommandLineWorkspacePathReachesVisibleRecovery() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let workspace = directory.appendingPathComponent("Broken workspace.gabcode-workspace")
        try Data("not json".utf8).write(to: workspace)
        app.launchArguments.append(workspace.path)

        app.launch()

        XCTAssertTrue(
            app.staticTexts["Invalid workspace file"].waitForExistence(timeout: 5),
            "A quoted command-line workspace path must reach visible native recovery."
        )
        let recovery = app.staticTexts["workspace-recovery-message"]
        XCTAssertTrue(recovery.exists)
        XCTAssertTrue((recovery.value as? String)?.contains(workspace.path) == true)
        XCTAssertTrue(app.buttons["retry-workspace"].exists)
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
    func testReferencesBarAssignsCanonicalIssueWithoutReplacingTerminalSurface() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()

        let terminalPath = app.staticTexts["terminal-directory-path"]
        XCTAssertTrue(terminalPath.waitForExistence(timeout: 8))
        let issueMenu = app.buttons["GitHub issue"]
        XCTAssertTrue(issueMenu.waitForExistence(timeout: 5))
        issueMenu.click()
        XCTAssertTrue(app.menuItems["Add issue…"].waitForExistence(timeout: 3))
        app.menuItems["Add issue…"].click()
        let issueURL = app.textFields["GitHub issue URL"]
        XCTAssertTrue(issueURL.waitForExistence(timeout: 3))
        issueURL.typeText("https://github.com/gabrewer/gabCode/issues/92?source=ui")
        app.buttons["Add"].click()

        XCTAssertTrue(app.staticTexts["gabrewer/gabCode#92"].waitForExistence(timeout: 3))
        XCTAssertEqual(terminalPath.value as? String, fixture.primary.path)
    }

    @MainActor
    func testReturningToClosedWorkspaceStartsFreshTerminalsAutomatically() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()

        let terminalPath = app.staticTexts["terminal-directory-path"]
        XCTAssertTrue(terminalPath.waitForExistence(timeout: 8))
        let primaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "primary")).firstMatch
        XCTAssertTrue(primaryRow.waitForExistence(timeout: 5))
        primaryRow.rightClick()
        let closeWorkspace = app.menuItems.matching(NSPredicate(format: "title BEGINSWITH %@", "Close Workspace")).firstMatch
        XCTAssertTrue(closeWorkspace.waitForExistence(timeout: 3))
        closeWorkspace.click()

        XCTAssertTrue(app.sheets.buttons["Close Workspace"].waitForExistence(timeout: 3))
        app.sheets.buttons["Close Workspace"].click()
        XCTAssertTrue(app.staticTexts["Workspace closed"].waitForExistence(timeout: 8))

        let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
        XCTAssertTrue(secondaryRow.waitForExistence(timeout: 5))
        secondaryRow.click()
        XCTAssertTrue(terminalPath.waitForExistence(timeout: 8))
        primaryRow.click()
        XCTAssertTrue(terminalPath.waitForExistence(timeout: 8))
        XCTAssertFalse(app.staticTexts["Workspace closed"].exists)
        XCTAssertEqual(terminalPath.value as? String, fixture.primary.path)
    }

    @MainActor
    func testCloseWithoutWorkspaceDoesNotOfferTerminalCleanup() throws {
        app.launch()
        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        app.typeKey("w", modifierFlags: .command)
        XCTAssertFalse(app.staticTexts["Stop 2 active terminals?"].waitForExistence(timeout: 2))
    }

    private struct WorkspaceFixture {
        let root: URL
        let primary: URL
        let descriptor: URL
    }

    private func makeWorkspaceFixture() throws -> WorkspaceFixture {
        let root = try makeTemporaryDirectory()
        let primary = root.appendingPathComponent("primary ünicode", isDirectory: true)
        let secondary = primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        try FileManager.default.createDirectory(at: primary, withIntermediateDirectories: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        try run(git, ["init", "-b", "main", primary.path])
        try Data("fixture\n".utf8).write(to: primary.appendingPathComponent("README.md"))
        try run(git, ["-C", primary.path, "add", "README.md"])
        try run(git, ["-C", primary.path, "-c", "user.name=gabCode UI Tests", "-c", "user.email=ui-tests@example.invalid", "commit", "-m", "fixture"])
        try run(git, ["-C", primary.path, "worktree", "add", "-b", "secondary", secondary.path])

        let descriptor = root.appendingPathComponent("fixture.gabcode-workspace")
        let encodedPath = primary.path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        try Data("{\"version\":1,\"name\":\"Close Workspace UI\",\"project\":{\"path\":\"\(encodedPath)\",\"mainBranch\":\"main\"}}".utf8).write(to: descriptor)
        return WorkspaceFixture(root: root, primary: primary.standardizedFileURL, descriptor: descriptor)
    }

    private func developerToolPath(_ tool: String) throws -> String {
        let xcodeSelect = URL(fileURLWithPath: "/usr/bin/xcode-select")
        let output = try run(xcodeSelect, ["-p"])
        return URL(fileURLWithPath: output.trimmingCharacters(in: .whitespacesAndNewlines), isDirectory: true)
            .appendingPathComponent("usr/bin/\(tool)").path
    }

    @discardableResult
    private func run(_ executable: URL, _ arguments: [String]) throws -> String {
        let process = Process()
        let output = Pipe()
        process.executableURL = executable
        process.arguments = arguments
        process.standardOutput = output
        process.standardError = output
        try process.run()
        process.waitUntilExit()
        let text = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        guard process.terminationStatus == 0 else {
            throw NSError(domain: "gabCodeUITests", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: text])
        }
        return text
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
