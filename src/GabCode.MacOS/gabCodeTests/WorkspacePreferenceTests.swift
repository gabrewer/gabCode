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
        XCTAssertNil(UserDefaults.standard.string(forKey: WorkspacePreference.lastWorkspaceKey))
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
