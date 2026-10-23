import Foundation

@MainActor
final class ReviewModePreference {
    static let key = "nativeDiffReviewMode"
    private let defaults: UserDefaults
    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    var mode: String {
        get {
            let stored = defaults.string(forKey: Self.key) ?? "side-by-side"
            return ["side-by-side", "inline", "file"].contains(stored) ? stored : "side-by-side"
        }
        set {
            guard ["side-by-side", "inline", "file"].contains(newValue) else { return }
            defaults.set(newValue, forKey: Self.key)
        }
    }
}
