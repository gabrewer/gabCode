import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorktreeReferenceLauncherTests: XCTestCase {
    func testMarkdownRejectsMissingAndNonRegularMarkdownTargets() {
        let launcher = WorktreeReferenceLauncher(
            isRegularMarkdownFile: { _ in false },
            codeApplicationURL: { URL(fileURLWithPath: "/Applications/Visual Studio Code.app") },
            openInApplication: { _, _, _ in XCTFail("Must not launch an invalid Markdown target.") },
            openURL: { _ in true }
        )

        var error: String?
        launcher.openMarkdown(URL(fileURLWithPath: "/tmp/missing.md")) { error = $0 }
        XCTAssertTrue(error?.contains("missing") == true)
    }

    func testMarkdownReportsVSCodeUnavailableAndCompletionFailureWithoutLaunchingIssue() {
        let markdown = URL(fileURLWithPath: "/tmp/reference.md")
        let unavailable = WorktreeReferenceLauncher(
            isRegularMarkdownFile: { $0 == markdown },
            codeApplicationURL: { nil },
            openInApplication: { _, _, _ in XCTFail("Must not launch without VS Code.") },
            openURL: { _ in true }
        )
        var unavailableError: String?
        unavailable.openMarkdown(markdown) { unavailableError = $0 }
        XCTAssertEqual(unavailableError, "Visual Studio Code could not be found.")

        let failing = WorktreeReferenceLauncher(
            isRegularMarkdownFile: { $0 == markdown },
            codeApplicationURL: { URL(fileURLWithPath: "/Applications/Visual Studio Code.app") },
            openInApplication: { _, _, completion in completion(NSError(domain: "test", code: 1, userInfo: [NSLocalizedDescriptionKey: "launch denied"])) },
            openURL: { _ in true }
        )
        var launchError: String?
        failing.openMarkdown(markdown) { launchError = $0 }
        XCTAssertTrue(launchError?.contains("launch denied") == true)
    }

    func testIssueReportsBrowserLaunchFailure() {
        let launcher = WorktreeReferenceLauncher(
            isRegularMarkdownFile: { _ in true },
            codeApplicationURL: { nil },
            openInApplication: { _, _, _ in },
            openURL: { _ in false }
        )
        let issue = try! XCTUnwrap(GitHubIssueReference(urlString: "https://github.com/gabrewer/gabCode/issues/92"))
        XCTAssertEqual(launcher.openIssue(issue), "The default browser could not open the issue.")
    }
}
