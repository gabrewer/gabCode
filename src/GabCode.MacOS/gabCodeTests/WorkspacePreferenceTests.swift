import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorkspacePreferenceTests: XCTestCase {
    func testPreferenceStoresLastDescriptorInInjectedDefaultsOnly() throws {
        let suite = "gabCode.workspace.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let descriptor = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")

        let preference = WorkspacePreference(defaults: defaults)
        preference.lastWorkspaceURL = descriptor

        XCTAssertEqual(preference.lastWorkspaceURL, descriptor)
        XCTAssertEqual(defaults.string(forKey: WorkspacePreference.lastWorkspaceKey), descriptor.path)
    }

    func testSelectedWorktreeIsWorkspaceKeyedAndRejectsUnavailablePath() throws {
        let suite = "gabCode.workspace.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let preference = WorkspacePreference(defaults: defaults)
        let workspace = URL(fileURLWithPath: "/tmp/Workspace ünicode.gabcode-workspace")
        let available = URL(fileURLWithPath: "/tmp/project/wt/available", isDirectory: true)
        let unavailable = URL(fileURLWithPath: "/tmp/project/wt/unavailable", isDirectory: true)

        preference.setSelectedWorktreeURL(unavailable, for: workspace, availableWorktrees: [available])
        XCTAssertNil(preference.selectedWorktreeURL(for: workspace))
        preference.setSelectedWorktreeURL(available, for: workspace, availableWorktrees: [available])
        XCTAssertEqual(preference.selectedWorktreeURL(for: workspace)?.path, available.standardizedFileURL.path)
    }

    func testSidebarSideRoundTripsThroughInjectedDefaultsOnly() throws {
        let suite = "gabCode.workspace.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let preference = WorkspacePreference(defaults: defaults)

        XCTAssertFalse(preference.sidebarOnRight)
        preference.sidebarOnRight = true
        XCTAssertTrue(preference.sidebarOnRight)
        XCTAssertNil(UserDefaults.standard.object(forKey: WorkspacePreference.sidebarOnRightKey))
    }

    func testInvalidRememberedWorkspaceIsClearedWhenRevalidated() throws {
        let suite = "gabCode.workspace.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let preference = WorkspacePreference(defaults: defaults)
        preference.lastWorkspaceURL = URL(fileURLWithPath: "/tmp/missing.gabcode-workspace")

        let result = preference.revalidateLastWorkspace { _ in nil }

        XCTAssertNil(result)
        XCTAssertNil(preference.lastWorkspaceURL)
    }
}
