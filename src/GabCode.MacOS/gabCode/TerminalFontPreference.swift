import AppKit
import Combine
import Foundation

struct TerminalFontSelection: Equatable {
    enum Face: Equatable {
        case systemDefault
        case named(postScriptName: String)
    }

    static let minimumPointSize: CGFloat = 8
    static let maximumPointSize: CGFloat = 72
    static let defaultPointSize: CGFloat = NSFont.systemFontSize

    let face: Face
    let pointSize: CGFloat

    static func systemDefault(pointSize: CGFloat) -> TerminalFontSelection? {
        make(face: .systemDefault, pointSize: pointSize)
    }

    static func named(
        postScriptName: String,
        pointSize: CGFloat
    ) -> TerminalFontSelection? {
        guard !postScriptName.isEmpty else {
            return nil
        }
        return make(face: .named(postScriptName: postScriptName), pointSize: pointSize)
    }

    private static func make(
        face: Face,
        pointSize: CGFloat
    ) -> TerminalFontSelection? {
        guard
            pointSize.isFinite,
            (minimumPointSize...maximumPointSize).contains(pointSize)
        else {
            return nil
        }
        return TerminalFontSelection(face: face, pointSize: pointSize)
    }
}

struct TerminalFontFace: Equatable, Identifiable {
    let postScriptName: String
    let displayName: String
    let isFixedPitch: Bool

    var id: String { postScriptName }
}

@MainActor
struct TerminalFontCatalog {
    private let facesByPostScriptName: [String: TerminalFontFace]

    let selectableFaces: [TerminalFontFace]

    init(faces: [TerminalFontFace]) {
        var uniqueFaces: [String: TerminalFontFace] = [:]
        for face in faces where uniqueFaces[face.postScriptName] == nil {
            uniqueFaces[face.postScriptName] = face
        }
        facesByPostScriptName = uniqueFaces
        selectableFaces = uniqueFaces.values
            .filter(\.isFixedPitch)
            .sorted { left, right in
                let displayOrder = left.displayName.localizedCaseInsensitiveCompare(right.displayName)
                if displayOrder == .orderedSame {
                    return left.postScriptName < right.postScriptName
                }
                return displayOrder == .orderedAscending
            }
    }

    static func installed(fontManager: NSFontManager = .shared) -> TerminalFontCatalog {
        let faces = fontManager.availableFontFamilies.flatMap { family -> [TerminalFontFace] in
            guard let members = fontManager.availableMembers(ofFontFamily: family) else {
                return []
            }
            return members.compactMap { member in
                guard
                    let postScriptName = member[safe: 0] as? String,
                    let font = NSFont(
                        name: postScriptName,
                        size: TerminalFontSelection.defaultPointSize
                    )
                else {
                    return nil
                }
                return TerminalFontFace(
                    postScriptName: postScriptName,
                    displayName: font.displayName ?? postScriptName,
                    isFixedPitch: font.isFixedPitch
                )
            }
        }
        return TerminalFontCatalog(faces: faces)
    }

    func isSelectable(postScriptName: String) -> Bool {
        facesByPostScriptName[postScriptName]?.isFixedPitch == true
    }

    func font(for selection: TerminalFontSelection) -> NSFont {
        switch selection.face {
        case .systemDefault:
            return NSFont.monospacedSystemFont(
                ofSize: selection.pointSize,
                weight: .regular
            )
        case let .named(postScriptName):
            guard
                isSelectable(postScriptName: postScriptName),
                let font = NSFont(name: postScriptName, size: selection.pointSize),
                font.isFixedPitch
            else {
                return NSFont.monospacedSystemFont(
                    ofSize: TerminalFontSelection.defaultPointSize,
                    weight: .regular
                )
            }
            return font
        }
    }
}

@MainActor
final class TerminalFontPreferenceStore: ObservableObject {
    static let faceKey = "terminalFontPostScriptName"
    static let pointSizeKey = "terminalFontPointSize"

    let catalog: TerminalFontCatalog

    @Published private(set) var effectiveSelection: TerminalFontSelection

    private let defaults: UserDefaults

    convenience init(defaults: UserDefaults = .standard) {
        self.init(defaults: defaults, catalog: .installed())
    }

    init(
        defaults: UserDefaults,
        catalog: TerminalFontCatalog
    ) {
        self.defaults = defaults
        self.catalog = catalog
        let restored = Self.restore(defaults: defaults, catalog: catalog)
        effectiveSelection = restored.selection
        if restored.requiresRepair {
            persist(restored.selection)
        }
    }

    var effectiveFont: NSFont {
        catalog.font(for: effectiveSelection)
    }

    func save(_ selection: TerminalFontSelection) {
        let resolved = validated(selection) ?? Self.defaultSelection
        effectiveSelection = resolved
        persist(resolved)
    }

    func restoreSystemDefault(pointSize: CGFloat? = nil) {
        let size = pointSize ?? TerminalFontSelection.defaultPointSize
        save(
            TerminalFontSelection.systemDefault(pointSize: size)
                ?? Self.defaultSelection
        )
    }

    private static var defaultSelection: TerminalFontSelection {
        TerminalFontSelection.systemDefault(
            pointSize: TerminalFontSelection.defaultPointSize
        )!
    }

    private static func restore(
        defaults: UserDefaults,
        catalog: TerminalFontCatalog
    ) -> (selection: TerminalFontSelection, requiresRepair: Bool) {
        let rawSize = defaults.object(forKey: pointSizeKey)
        let storedSize = rawSize as? NSNumber
        let sizeRequiresRepair = rawSize != nil && storedSize == nil
        let pointSize = storedSize.map { CGFloat(truncating: $0) }
            ?? TerminalFontSelection.defaultPointSize

        let rawFace = defaults.object(forKey: faceKey)
        let storedFace = rawFace as? String
        let faceRequiresRepair = rawFace != nil && storedFace == nil

        guard let storedFace else {
            if let selection = TerminalFontSelection.systemDefault(pointSize: pointSize) {
                return (
                    selection,
                    storedSize == nil || sizeRequiresRepair || faceRequiresRepair
                )
            }
            return (defaultSelection, true)
        }

        guard
            catalog.isSelectable(postScriptName: storedFace),
            let selection = TerminalFontSelection.named(
                postScriptName: storedFace,
                pointSize: pointSize
            )
        else {
            return (defaultSelection, true)
        }
        return (selection, sizeRequiresRepair)
    }

    private func validated(_ selection: TerminalFontSelection) -> TerminalFontSelection? {
        switch selection.face {
        case .systemDefault:
            return TerminalFontSelection.systemDefault(pointSize: selection.pointSize)
        case let .named(postScriptName):
            guard catalog.isSelectable(postScriptName: postScriptName) else {
                return nil
            }
            return TerminalFontSelection.named(
                postScriptName: postScriptName,
                pointSize: selection.pointSize
            )
        }
    }

    private func persist(_ selection: TerminalFontSelection) {
        switch selection.face {
        case .systemDefault:
            defaults.removeObject(forKey: Self.faceKey)
        case let .named(postScriptName):
            defaults.set(postScriptName, forKey: Self.faceKey)
        }
        defaults.set(Double(selection.pointSize), forKey: Self.pointSizeKey)
    }
}

private extension Array {
    subscript(safe index: Index) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
