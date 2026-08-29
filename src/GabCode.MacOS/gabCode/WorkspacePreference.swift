import Foundation

@MainActor
final class WorkspacePreference {
    static let lastWorkspaceKey = "lastWorkspaceDescriptorPath"
    static let sidebarOnRightKey = "worktreeSidebarOnRight"
    static let selectedWorktreeKeyPrefix = "workspaceSelectedWorktree."

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

    func selectedWorktreeURL(for workspaceURL: URL) -> URL? {
        guard let path = defaults.string(forKey: Self.selectedWorktreeKey(for: workspaceURL)), !path.isEmpty else { return nil }
        return URL(fileURLWithPath: path).standardizedFileURL
    }

    func setSelectedWorktreeURL(_ worktreeURL: URL?, for workspaceURL: URL, availableWorktrees: [URL]) {
        let key = Self.selectedWorktreeKey(for: workspaceURL)
        guard let worktreeURL else {
            defaults.removeObject(forKey: key)
            return
        }
        let normalized = worktreeURL.standardizedFileURL
        guard availableWorktrees.map(\.standardizedFileURL).contains(normalized) else { return }
        defaults.set(normalized.path, forKey: key)
    }

    var sidebarOnRight: Bool {
        get { defaults.bool(forKey: Self.sidebarOnRightKey) }
        set { defaults.set(newValue, forKey: Self.sidebarOnRightKey) }
    }

    private static func selectedWorktreeKey(for workspaceURL: URL) -> String {
        selectedWorktreeKeyPrefix + workspaceURL.standardizedFileURL.path
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
