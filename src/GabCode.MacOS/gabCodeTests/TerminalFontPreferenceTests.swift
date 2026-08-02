import AppKit
@testable import gabCode
import XCTest

@MainActor
final class TerminalFontPreferenceTests: XCTestCase {
    func testFixedPitchFaceAndSizeRoundTripThroughAnIsolatedPreferenceDomain() throws {
        let defaults = makeIsolatedDefaults()
        let catalog = TerminalFontCatalog(
            faces: [
                TerminalFontFace(
                    postScriptName: "MesloLGMNerdFontMono-Regular",
                    displayName: "MesloLGM Nerd Font Mono Regular",
                    isFixedPitch: true
                ),
            ]
        )
        let expected = try XCTUnwrap(
            TerminalFontSelection.named(
                postScriptName: "MesloLGMNerdFontMono-Regular",
                pointSize: 15
            )
        )

        TerminalFontPreferenceStore(defaults: defaults, catalog: catalog).save(expected)
        let restored = TerminalFontPreferenceStore(defaults: defaults, catalog: catalog)

        XCTAssertEqual(restored.effectiveSelection, expected)
    }

    func testInvalidOrNoLongerFixedPitchSavedFaceFallsBackAndRepairsStoredValues() throws {
        let defaults = makeIsolatedDefaults()
        let catalog = TerminalFontCatalog(
            faces: [
                TerminalFontFace(
                    postScriptName: "Proportional-Regular",
                    displayName: "Proportional Regular",
                    isFixedPitch: false
                ),
            ]
        )
        defaults.set("Proportional-Regular", forKey: TerminalFontPreferenceStore.faceKey)
        defaults.set(200, forKey: TerminalFontPreferenceStore.pointSizeKey)

        let store = TerminalFontPreferenceStore(defaults: defaults, catalog: catalog)

        XCTAssertEqual(
            store.effectiveSelection,
            try XCTUnwrap(TerminalFontSelection.systemDefault(pointSize: TerminalFontSelection.defaultPointSize))
        )
        XCTAssertNil(defaults.string(forKey: TerminalFontPreferenceStore.faceKey))
        XCTAssertEqual(
            defaults.double(forKey: TerminalFontPreferenceStore.pointSizeKey),
            Double(TerminalFontSelection.defaultPointSize)
        )
    }

    func testCatalogExposesFixedPitchFacesInDeterministicDisplayOrderWithoutNameBasedNerdFontFiltering() {
        let catalog = TerminalFontCatalog(
            faces: [
                TerminalFontFace(
                    postScriptName: "zzz.Mono",
                    displayName: "Zulu Mono",
                    isFixedPitch: true
                ),
                TerminalFontFace(
                    postScriptName: "custom.patched",
                    displayName: "Acme Terminal",
                    isFixedPitch: true
                ),
                TerminalFontFace(
                    postScriptName: "decorative.Proportional",
                    displayName: "Decorative",
                    isFixedPitch: false
                ),
            ]
        )

        XCTAssertEqual(
            catalog.selectableFaces.map(\.postScriptName),
            ["custom.patched", "zzz.Mono"],
            "A fixed-pitch patched face must remain selectable even when its name does not contain Nerd Font or Powerline."
        )
    }

    func testSelectionRejectsNonFiniteAndOutOfRangePointSizes() {
        XCTAssertNil(
            TerminalFontSelection.named(
                postScriptName: "MesloLGMNerdFontMono-Regular",
                pointSize: .infinity
            )
        )
        XCTAssertNil(
            TerminalFontSelection.named(
                postScriptName: "MesloLGMNerdFontMono-Regular",
                pointSize: 7.5
            )
        )
        XCTAssertNil(TerminalFontSelection.systemDefault(pointSize: 72.5))
        XCTAssertNotNil(
            TerminalFontSelection.named(
                postScriptName: "MesloLGMNerdFontMono-Regular",
                pointSize: 72
            )
        )
    }

    func testPointSizeInputResolvesEveryValidEditAndPreservesInvalidIntermediateText() throws {
        XCTAssertEqual(
            TerminalPointSizeInput.selection(from: "8", face: .systemDefault),
            try XCTUnwrap(TerminalFontSelection.systemDefault(pointSize: 8))
        )
        XCTAssertEqual(
            TerminalPointSizeInput.selection(
                from: "15.5",
                face: .named(postScriptName: "MesloLGMNerdFontMono-Regular")
            ),
            try XCTUnwrap(
                TerminalFontSelection.named(
                    postScriptName: "MesloLGMNerdFontMono-Regular",
                    pointSize: 15.5
                )
            )
        )
        XCTAssertEqual(
            TerminalPointSizeInput.selection(from: "72", face: .systemDefault),
            try XCTUnwrap(TerminalFontSelection.systemDefault(pointSize: 72))
        )
        XCTAssertNil(TerminalPointSizeInput.selection(from: "", face: .systemDefault))
        XCTAssertNil(TerminalPointSizeInput.selection(from: "7", face: .systemDefault))
        XCTAssertNil(TerminalPointSizeInput.selection(from: "73", face: .systemDefault))
        XCTAssertNil(TerminalPointSizeInput.selection(from: "not a size", face: .systemDefault))
    }

    func testMalformedStoredPrimitiveTypesAreRepairedInsteadOfReloadedForever() {
        let catalog = TerminalFontCatalog(
            faces: [
                TerminalFontFace(
                    postScriptName: "MesloLGMNerdFontMono-Regular",
                    displayName: "MesloLGM Nerd Font Mono Regular",
                    isFixedPitch: true
                ),
            ]
        )

        let malformedSizeDefaults = makeIsolatedDefaults()
        malformedSizeDefaults.set(
            "MesloLGMNerdFontMono-Regular",
            forKey: TerminalFontPreferenceStore.faceKey
        )
        malformedSizeDefaults.set("not-a-number", forKey: TerminalFontPreferenceStore.pointSizeKey)
        _ = TerminalFontPreferenceStore(defaults: malformedSizeDefaults, catalog: catalog)
        XCTAssertTrue(
            malformedSizeDefaults.object(forKey: TerminalFontPreferenceStore.pointSizeKey) is NSNumber,
            "A malformed size must be replaced by the effective default rather than parsed again on every launch."
        )

        let malformedFaceDefaults = makeIsolatedDefaults()
        malformedFaceDefaults.set(42, forKey: TerminalFontPreferenceStore.faceKey)
        malformedFaceDefaults.set(15, forKey: TerminalFontPreferenceStore.pointSizeKey)
        _ = TerminalFontPreferenceStore(defaults: malformedFaceDefaults, catalog: catalog)
        XCTAssertNil(
            malformedFaceDefaults.object(forKey: TerminalFontPreferenceStore.faceKey),
            "A non-string face identifier must be removed rather than retained as hidden corrupt state."
        )
    }

    private func makeIsolatedDefaults() -> UserDefaults {
        let suiteName = "gabCode.TerminalFontPreferenceTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        addTeardownBlock {
            defaults.removePersistentDomain(forName: suiteName)
        }
        return defaults
    }
}
