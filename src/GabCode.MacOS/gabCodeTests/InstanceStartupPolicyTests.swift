@testable import gabCode
import XCTest

@MainActor
final class InstanceStartupPolicyTests: XCTestCase {
    func testExplicitIntentAlwaysWins() {
        XCTAssertEqual(InstanceStartupPolicy.action(hasExplicitWorkspace: true, ownsPresence: false, isFirstWindow: true), .openExplicit)
        XCTAssertEqual(InstanceStartupPolicy.action(hasExplicitWorkspace: true, ownsPresence: true, isFirstWindow: false), .openExplicit)
    }

    func testOnlyFirstWindowOfPresenceOwnerRestoresPlainWorkspace() {
        XCTAssertEqual(InstanceStartupPolicy.action(hasExplicitWorkspace: false, ownsPresence: true, isFirstWindow: true), .restoreRemembered)
        XCTAssertEqual(InstanceStartupPolicy.action(hasExplicitWorkspace: false, ownsPresence: true, isFirstWindow: false), .remainEmpty)
        XCTAssertEqual(InstanceStartupPolicy.action(hasExplicitWorkspace: false, ownsPresence: false, isFirstWindow: true), .remainEmpty)
    }

    func testExplicitFirstWindowConsumesTheOnlyRestoreClaim() {
        let claims = ProcessWindowStartupClaims()

        let explicit = InstanceStartupPolicy.action(
            hasExplicitWorkspace: true,
            ownsPresence: true,
            isFirstWindow: claims.claimFirstWindow()
        )
        let commandN = InstanceStartupPolicy.action(
            hasExplicitWorkspace: false,
            ownsPresence: true,
            isFirstWindow: claims.claimFirstWindow()
        )

        XCTAssertEqual(explicit, .openExplicit)
        XCTAssertEqual(commandN, .remainEmpty)
    }

    func testExistingUnlockedLockFileDoesNotIndicatePresence() throws {
        let path = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("gabCode stale instance \(UUID().uuidString)")
        XCTAssertTrue(FileManager.default.createFile(atPath: path.path, contents: Data()))
        let presence = try MacOSInstancePresence(lockURL: path)
        XCTAssertTrue(presence.ownsPresence)
        presence.close()
        try? FileManager.default.removeItem(at: path)
    }

    func testExclusiveLockIsReleasedWhenOwnerCloses() throws {
        let path = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("gabCode instance test \(UUID().uuidString)")
        let first = try MacOSInstancePresence(lockURL: path)
        XCTAssertTrue(first.ownsPresence)
        let second = try MacOSInstancePresence(lockURL: path)
        XCTAssertFalse(second.ownsPresence)
        first.close()
        let third = try MacOSInstancePresence(lockURL: path)
        XCTAssertTrue(third.ownsPresence)
        third.close()
        second.close()
        try? FileManager.default.removeItem(at: path)
    }
}
