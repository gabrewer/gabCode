import Foundation

@MainActor
final class WorkspacePreference {
    static let lastWorkspaceKey = "lastWorkspaceDescriptorPath"
    static let sidebarOnRightKey = "worktreeSidebarOnRight"

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var lastWorkspaceURL: URL? {
        get {
            guard let path = defaults.string(forKey: Self.lastWorkspaceKey), !path.isEmpty else {
                return nil
            }
            return URL(fileURLWithPath: path)
        }
        set {
            if let newValue {
                defaults.set(newValue.standardizedFileURL.path, forKey: Self.lastWorkspaceKey)
            } else {
                defaults.removeObject(forKey: Self.lastWorkspaceKey)
            }
        }
    }

    var sidebarOnRight: Bool {
        get { defaults.bool(forKey: Self.sidebarOnRightKey) }
        set { defaults.set(newValue, forKey: Self.sidebarOnRightKey) }
    }

    func revalidateLastWorkspace(
        resolve: (URL) -> WorkspaceDescriptor?
    ) -> WorkspaceDescriptor? {
        guard let url = lastWorkspaceURL else { return nil }
        guard let descriptor = resolve(url) else {
            lastWorkspaceURL = nil
            return nil
        }
        return descriptor
    }
}
