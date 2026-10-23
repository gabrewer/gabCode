import AppKit
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
    func testClosingAnotherWindowPreservesAccessibleWorkspaceRefreshControls() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()

        // The parent workspace surface currently supplies a shared AX identifier
        // to this button; its accessible label is the stable native selector.
        let worktrees = app.buttons["Refresh Worktrees"]
        let review = app.buttons["refresh-change-review"]
        XCTAssertTrue(review.waitForExistence(timeout: 12))
        XCTAssertTrue(worktrees.waitForExistence(timeout: 5))
        XCTAssertEqual(worktrees.label, "Refresh Worktrees")
        XCTAssertEqual(review.label, "Refresh Changes and History")
        app.typeKey("n", modifierFlags: .command)
        XCTAssertTrue(app.buttons["open-workspace"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.windows.count, 2)
        app.typeKey("w", modifierFlags: .command)
        XCTAssertTrue(worktrees.waitForExistence(timeout: 5))
        XCTAssertTrue(review.exists)
        XCTAssertEqual(app.windows.count, 1)
        review.click()
        XCTAssertTrue(review.exists, "Second-window closure must not discard first-window review")
        XCTAssertTrue(worktrees.exists, "Review refresh must not replace worktree refresh")
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
        let issueMenu = app.menuButtons["issue-reference"]
        XCTAssertTrue(issueMenu.waitForExistence(timeout: 5))
        XCTAssertEqual(issueMenu.label, "GitHub issue reference")
        issueMenu.click()
        XCTAssertTrue(app.menuItems["Add issue…"].waitForExistence(timeout: 3))
        app.menuItems["Add issue…"].click()
        let issueURL = app.textFields["GitHub issue URL"]
        XCTAssertTrue(issueURL.waitForExistence(timeout: 3))
        issueURL.typeText("https://github.com/gabrewer/gabCode/issues/92?source=ui")
        app.buttons["Add"].click()

        let canonicalIssue = expectation(
            for: NSPredicate(format: "value == %@", "gabrewer/gabCode#92"),
            evaluatedWith: issueMenu
        )
        XCTAssertEqual(XCTWaiter.wait(for: [canonicalIssue], timeout: 5), .completed)
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
    func testNativeChangeReviewLoadsSelectedWorktreeAndOpensReadOnlyDiff() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let secondary = fixture.primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        try Data("history change\n".utf8).write(to: secondary.appendingPathComponent("history.txt"))
        try run(git, ["-C", secondary.path, "add", "history.txt"])
        try run(git, ["-C", secondary.path, "-c", "user.name=UI", "-c", "user.email=ui@example.invalid", "commit", "-m", "Ahead of main"])
        try Data("staged content\n".utf8).write(to: secondary.appendingPathComponent("staged.txt"))
        try run(git, ["-C", secondary.path, "add", "staged.txt"])
        try Data("unstaged content\n".utf8).write(to: secondary.appendingPathComponent("README.md"))
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()

        XCTAssertTrue(app.buttons["refresh-change-review"].waitForExistence(timeout: 12))
        let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
        XCTAssertTrue(secondaryRow.waitForExistence(timeout: 5))
        secondaryRow.click()
        let staged = app.buttons["review-file-staged-staged.txt"]
        XCTAssertTrue(staged.waitForExistence(timeout: 12), "Selected worktree's staged files must load")
        XCTAssertTrue(app.descendants(matching: .any).matching(identifier: "review-file-unstaged-README.md").firstMatch.exists)
        XCTAssertTrue(app.descendants(matching: .any).matching(NSPredicate(format: "identifier BEGINSWITH %@", "review-commit-")).firstMatch.exists)
        staged.click()
        XCTAssertEqual(staged.value as? String, "Selected diff", "Selecting a row must update coordinator state")
        XCTAssertTrue(app.buttons["close-review-diff"].waitForExistence(timeout: 8),
                      "A staged row click must open the diff document")
        XCTAssertTrue(app.descendants(matching: .any)["review-diff-document"].exists)
        XCTAssertTrue(app.descendants(matching: .any)["native-diff-viewer"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.descendants(matching: .any)["review-terminal-tabs"].exists)
        app.buttons["close-review-diff"].click()
        XCTAssertFalse(app.descendants(matching: .any)["review-diff-document"].exists)

        let commit = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", "review-commit-")).firstMatch
        commit.click()
        let history = app.buttons["review-file-commit-history.txt"]
        XCTAssertTrue(history.waitForExistence(timeout: 8), "Expanding a commit must load its changed files")
        history.click()
        XCTAssertEqual(history.value as? String, "Selected diff", "Commit file selection must reach the review coordinator")
        XCTAssertTrue(app.buttons["close-review-diff"].waitForExistence(timeout: 8),
                      "A branch-history file must open its read-only diff")
    }

    @MainActor
    func testReviewAndWorktreeControlsStayInsideWindowAtMinimumAfterReopen() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let secondary = fixture.primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        try Data("history\n".utf8).write(to: secondary.appendingPathComponent("history.txt"))
        try run(git, ["-C", secondary.path, "add", "history.txt"])
        try run(git, ["-C", secondary.path, "-c", "user.name=UI", "-c", "user.email=ui@example.invalid", "commit", "-m", "Review at minimum width"])
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()
        let window = app.windows.firstMatch
        XCTAssertTrue(window.waitForExistence(timeout: 12))
        if window.frame.width <= 1_161 {
            let growCorner = window.coordinate(withNormalizedOffset: CGVector(dx: 1, dy: 1)).withOffset(CGVector(dx: -2, dy: -2))
            growCorner.press(forDuration: 0.2, thenDragTo: growCorner.withOffset(CGVector(dx: 160, dy: 0)))
        }
        let initialWidth = window.frame.width
        XCTAssertGreaterThan(initialWidth, 1_161, "Resize fixture must start wider than the native minimum")
        checkControls()
        let corner = window.coordinate(withNormalizedOffset: CGVector(dx: 1, dy: 1)).withOffset(CGVector(dx: -2, dy: -2))
        corner.press(forDuration: 0.2, thenDragTo: corner.withOffset(CGVector(dx: -350, dy: 0)))
        XCTAssertLessThan(window.frame.width, initialWidth, "Resize fixture must exercise native minimum sizing")
        XCTAssertEqual(window.frame.width, 1_161, accuracy: 1, "Exercise the actual native minimum, not merely a wider passing size")
        func checkControls() {
            let bounds = app.windows.firstMatch.frame
            let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
            XCTAssertTrue(secondaryRow.waitForExistence(timeout: 12))
            XCTAssertGreaterThanOrEqual(secondaryRow.frame.minX, bounds.minX)
            XCTAssertLessThanOrEqual(secondaryRow.frame.maxX, bounds.maxX)
            XCTAssertTrue(secondaryRow.isHittable, "Worktree selection must remain reachable")
            secondaryRow.click()
            let refresh = app.buttons["refresh-change-review"]
            XCTAssertTrue(refresh.waitForExistence(timeout: 12))
            XCTAssertLessThanOrEqual(refresh.frame.maxX, bounds.maxX, "Refresh must not clip past the window")
            XCTAssertTrue(refresh.isHittable, "Refresh must remain clickable")
            refresh.click()
            let commit = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", "review-commit-")).firstMatch
            XCTAssertTrue(commit.waitForExistence(timeout: 12))
            XCTAssertLessThanOrEqual(commit.frame.maxX, bounds.maxX)
            XCTAssertTrue(commit.isHittable)
        }
        checkControls()
        app.terminate()
        app.launch()
        XCTAssertTrue(app.windows.firstMatch.waitForExistence(timeout: 12))
        XCTAssertEqual(app.windows.firstMatch.frame.width, 1_161, accuracy: 1, "Reopen must exercise the persisted minimum-width window")
        checkControls()
    }

    @MainActor
    func testBranchHistoryUsesCompactSingleLineRowsWithAccessibleExpandedDetails() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let secondary = fixture.primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        let subjects = (1...4).map { "Compact history \($0): a deliberately long subject that must not wrap onto multiple lines" }
        for (index, subject) in subjects.enumerated() {
            try Data("version \(index)\n".utf8).write(to: secondary.appendingPathComponent("history.txt"))
            try run(git, ["-C", secondary.path, "add", "history.txt"])
            try run(git, ["-C", secondary.path, "-c", "user.name=Reviewer", "-c", "user.email=reviewer@example.invalid", "commit", "-m", subject])
        }
        let head = try run(git, ["-C", secondary.path, "rev-parse", "HEAD"])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()
        let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
        XCTAssertTrue(secondaryRow.waitForExistence(timeout: 12))
        secondaryRow.click()
        let first = app.buttons["review-commit-\(head)"]
        XCTAssertTrue(first.waitForExistence(timeout: 12))
        let rows = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", "review-commit-"))
        XCTAssertEqual(rows.count, 4)
        let second = rows.element(boundBy: 1)
        let refresh = app.buttons["refresh-change-review"]
        XCTAssertLessThanOrEqual(first.frame.maxX, refresh.frame.maxX + 10, "The row must fit the actual sidebar width")
        XCTAssertLessThanOrEqual(first.frame.height, 42, "Collapsed history row should be a compact single line")
        XCTAssertLessThanOrEqual(second.frame.minY - first.frame.minY, 43, "No per-row metadata block or large divider gap")
        XCTAssertTrue(first.label.contains(subjects[3]), "Full subject remains available to accessibility when visually truncated")
        XCTAssertTrue(first.label.contains("Reviewer"))
        XCTAssertTrue(first.label.contains("HEAD"), "Identify only the actual current tip with a ref badge")
        XCTAssertTrue(first.label.contains(String(head.prefix(8))), "Collapsed accessibility retains the abbreviated SHA")
        XCTAssertFalse(second.label.contains("HEAD"))
        let screenshot = XCTAttachment(screenshot: app.screenshot())
        screenshot.name = "Compact branch history, four collapsed commits"
        screenshot.lifetime = .keepAlways
        add(screenshot)
        let details = app.staticTexts["review-commit-details-\(head)"]
        XCTAssertFalse(details.exists, "Commit metadata must never create an expanded block")
        first.click()
        let historyFile = app.buttons["review-file-commit-history.txt"]
        XCTAssertTrue(historyFile.waitForExistence(timeout: 8))
        XCTAssertFalse(details.exists, "Files follow the commit row directly, without duplicate metadata")
        XCTAssertFalse(historyFile.label.contains(head), "Historical file rows should not render raw commit IDs")
        XCTAssertTrue(historyFile.label.contains("First parent"), "Comparison basis remains accessible without a second visual line")
        XCTAssertLessThanOrEqual(historyFile.frame.height, 30, "File rows must fit on a single compact line")
        XCTAssertLessThanOrEqual(historyFile.frame.minY - first.frame.maxY, 10, "Files must appear directly under their commit")
        let expandedScreenshot = XCTAttachment(screenshot: app.screenshot())
        expandedScreenshot.name = "Expanded compact branch tree with files directly beneath commits"
        expandedScreenshot.lifetime = .keepAlways
        add(expandedScreenshot)
        first.click()
        XCTAssertFalse(historyFile.exists)
    }

    @MainActor
    func testReviewBadgesAndFullHashCopyFromNativeCommitMenu() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let secondary = fixture.primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        try Data("committed\n".utf8).write(to: secondary.appendingPathComponent("history.txt"))
        try run(git, ["-C", secondary.path, "add", "history.txt"])
        try run(git, ["-C", secondary.path, "-c", "user.name=Reviewer", "-c", "user.email=reviewer@example.invalid", "commit", "-m", "History"])
        let head = try run(git, ["-C", secondary.path, "rev-parse", "HEAD"])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        try Data("staged\n".utf8).write(to: secondary.appendingPathComponent("staged-new.txt"))
        try run(git, ["-C", secondary.path, "add", "staged-new.txt"])
        try Data("untracked\n".utf8).write(to: secondary.appendingPathComponent("untracked-new.txt"))
        app.launchArguments += ["-AppleKeyboardUIMode", "3", fixture.descriptor.path]
        app.launch()
        let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
        XCTAssertTrue(secondaryRow.waitForExistence(timeout: 12))
        secondaryRow.click()
        let staged = app.buttons["review-file-staged-staged-new.txt"]
        let untracked = app.buttons["review-file-unstaged-untracked-new.txt"]
        XCTAssertTrue(staged.waitForExistence(timeout: 12))
        XCTAssertTrue(untracked.exists)
        XCTAssertTrue(staged.label.contains("Added, Staged"))
        XCTAssertTrue(untracked.label.contains("Untracked, Unstaged"))
        XCTAssertFalse(staged.label.contains("Added ·"), "Do not repeat status as row detail")
        let commit = app.buttons["review-commit-\(head)"]
        XCTAssertTrue(commit.waitForExistence(timeout: 12))
        XCTAssertTrue(commit.label.contains(head), "Full SHA remains accessible without showing a row hash")
        commit.rightClick()
        let copy = app.menuItems["Copy Commit Hash"]
        XCTAssertTrue(copy.waitForExistence(timeout: 5), "Commit menu exposes a native full-SHA action")
        copy.click()
        XCTAssertEqual(NSPasteboard.general.string(forType: .string), head)
        XCTAssertTrue(commit.exists, "Copy must not change the selected review target")
        commit.click()
        let keyboardCopy = app.buttons["copy-commit-hash-button-\(head)"]
        XCTAssertTrue(keyboardCopy.waitForExistence(timeout: 5), "Commit row offers a focusable native copy action")
        NSPasteboard.general.clearContents()
        XCTAssertTrue(keyboardCopy.isHittable)
        // Commit activation establishes focus; Tab reaches the adjacent native copy button.
        app.typeKey(.tab, modifierFlags: [])
        app.typeKey(.space, modifierFlags: [])
        XCTAssertEqual(NSPasteboard.general.string(forType: .string), head, "Native keyboard activation copies the exact SHA")
    }

    @MainActor
    func testCleanWorktreeStillOpensCommittedFileFromBranchHistory() throws {
        let fixture = try makeWorkspaceFixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let secondary = fixture.primary.appendingPathComponent(".worktrees/secondary", isDirectory: true)
        let git = URL(fileURLWithPath: try developerToolPath("git"))
        try Data("committed content\n".utf8).write(to: secondary.appendingPathComponent("history.txt"))
        try run(git, ["-C", secondary.path, "add", "history.txt"])
        try run(git, ["-C", secondary.path, "-c", "user.name=UI", "-c", "user.email=ui@example.invalid", "commit", "-m", "Ahead of main"])
        XCTAssertTrue(try run(git, ["-C", secondary.path, "status", "--porcelain", "--untracked-files=all"]).isEmpty)
        app.launchArguments.append(fixture.descriptor.path)
        app.launch()

        XCTAssertTrue(app.buttons["refresh-change-review"].waitForExistence(timeout: 12))
        let secondaryRow = app.staticTexts.matching(NSPredicate(format: "value BEGINSWITH %@", "secondary")).firstMatch
        XCTAssertTrue(secondaryRow.waitForExistence(timeout: 5))
        secondaryRow.click()
        XCTAssertTrue(app.staticTexts["Working tree clean"].waitForExistence(timeout: 12))
        let stagedGroup = app.buttons["review-group-staged"]
        let unstagedGroup = app.buttons["review-group-unstaged"]
        XCTAssertTrue(stagedGroup.exists, "Empty staged group must remain in the tree")
        XCTAssertTrue(unstagedGroup.exists, "Empty unstaged group must remain in the tree")
        XCTAssertTrue(app.staticTexts["No staged files."].exists)
        XCTAssertTrue(app.staticTexts["No unstaged files."].exists)
        stagedGroup.click()
        XCTAssertFalse(app.staticTexts["No staged files."].exists, "Group disclosure collapses empty content")
        stagedGroup.click()
        XCTAssertTrue(app.staticTexts["No staged files."].exists)
        let commit = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH %@", "review-commit-")).firstMatch
        XCTAssertTrue(commit.waitForExistence(timeout: 8))
        commit.click()
        let history = app.buttons["review-file-commit-history.txt"]
        XCTAssertTrue(history.waitForExistence(timeout: 8))
        history.click()
        XCTAssertEqual(history.value as? String, "Selected diff")
        XCTAssertTrue(app.buttons["close-review-diff"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.descendants(matching: .any)["native-diff-viewer"].waitForExistence(timeout: 8))
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
