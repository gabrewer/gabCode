@testable import gabCode
import XCTest

@MainActor
final class WorkspaceWindowIntentStoreTests: XCTestCase {
    override func setUp() {
        super.setUp()
        while WorkspaceWindowIntentStore.shared.take() != nil {}
    }

    func testExternalWorkspaceURLsRemainConcreteAndOrderedUntilTheirWindowConsumesThem() {
        let firstURL = URL(fileURLWithPath: "/tmp/Workspace ünicode/one.gabcode-workspace")
        let secondURL = URL(fileURLWithPath: "/tmp/Workspace ünicode/two.gabcode-workspace")

        WorkspaceWindowIntentStore.shared.enqueueOpen(firstURL)
        WorkspaceWindowIntentStore.shared.enqueueOpen(secondURL)

        guard case let .open(first)? = WorkspaceWindowIntentStore.shared.take() else {
            return XCTFail("The first external open must retain its concrete workspace URL.")
        }
        guard case let .open(second)? = WorkspaceWindowIntentStore.shared.take() else {
            return XCTFail("The second external open must remain queued for its own window.")
        }

        XCTAssertEqual(first, firstURL.standardizedFileURL)
        XCTAssertEqual(second, secondURL.standardizedFileURL)
        XCTAssertNil(WorkspaceWindowIntentStore.shared.take())
    }

    func testApplicationBundleDeclaresGabCodeWorkspaceDocumentType() {
        let documentTypes = Bundle.main.object(forInfoDictionaryKey: "CFBundleDocumentTypes") as? [[String: Any]]
        let workspaceType = documentTypes?.first { document in
            (document["CFBundleTypeExtensions"] as? [String])?.contains("gabcode-workspace") == true
        }

        XCTAssertNotNil(workspaceType, "Finder must be able to route .gabcode-workspace files to gabCode.")
        XCTAssertEqual(workspaceType?["CFBundleTypeRole"] as? String, "Editor")
    }
}
