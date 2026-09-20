import Foundation

struct GitHubIssueReference: Equatable, Codable {
    let url: URL
    let owner: String
    let repository: String
    let number: Int

    var displayIdentity: String {
        "\(owner)/\(repository)#\(number)"
    }

    init?(urlString: String) {
        guard
            let suppliedURL = URL(string: urlString.trimmingCharacters(in: .whitespacesAndNewlines)),
            let components = URLComponents(url: suppliedURL, resolvingAgainstBaseURL: false),
            components.scheme?.lowercased() == "https",
            components.host?.lowercased() == "github.com",
            components.user == nil,
            components.password == nil,
            components.port == nil
        else {
            return nil
        }

        let path = components.percentEncodedPath.split(separator: "/", omittingEmptySubsequences: true)
        guard
            path.count == 4,
            path[2] == "issues",
            let owner = String(path[0]).removingPercentEncoding,
            let repository = String(path[1]).removingPercentEncoding,
            Self.isGitHubPathComponent(owner),
            Self.isGitHubPathComponent(repository),
            let number = Int(path[3]),
            number > 0
        else {
            return nil
        }

        self.owner = owner
        self.repository = repository
        self.number = number
        url = URL(string: "https://github.com/\(owner)/\(repository)/issues/\(number)")!
    }

    private static func isGitHubPathComponent(_ value: String) -> Bool {
        guard !value.isEmpty else { return false }
        return value.unicodeScalars.allSatisfy {
            CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_" || $0 == "."
        }
    }
}

struct WorktreeReferences: Equatable {
    var markdownURL: URL?
    var issue: GitHubIssueReference?

    static let empty = WorktreeReferences(markdownURL: nil, issue: nil)
}

@MainActor
final class WorktreeReferenceStore {
    static let storageKey = "worktreeReferences.v1"

    private struct StoredAssociation: Codable {
        let workspacePath: String
        let worktreePath: String
        let markdownPath: String?
        let issueURL: String?
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        repairMalformedStorageIfNeeded()
    }

    func references(for workspaceURL: URL, worktreeURL: URL) -> WorktreeReferences {
        let key = normalizedKey(workspaceURL: workspaceURL, worktreeURL: worktreeURL)
        return decodedAssociations()[key] ?? .empty
    }

    @discardableResult
    func setMarkdownURL(_ url: URL, for workspaceURL: URL, worktreeURL: URL) -> Bool {
        let normalizedURL = url.standardizedFileURL
        guard Self.isExistingMarkdownFile(normalizedURL) else { return false }
        mutate(workspaceURL: workspaceURL, worktreeURL: worktreeURL) { references in
            references.markdownURL = normalizedURL
        }
        return true
    }

    func removeMarkdown(for workspaceURL: URL, worktreeURL: URL) {
        mutate(workspaceURL: workspaceURL, worktreeURL: worktreeURL) { references in
            references.markdownURL = nil
        }
    }

    func setIssue(_ issue: GitHubIssueReference, for workspaceURL: URL, worktreeURL: URL) {
        mutate(workspaceURL: workspaceURL, worktreeURL: worktreeURL) { references in
            references.issue = issue
        }
    }

    func removeIssue(for workspaceURL: URL, worktreeURL: URL) {
        mutate(workspaceURL: workspaceURL, worktreeURL: worktreeURL) { references in
            references.issue = nil
        }
    }

    func removeConfirmedWorktrees(_ worktreeURLs: [URL], for workspaceURL: URL) {
        let workspacePath = workspaceURL.standardizedFileURL.path
        let removedPaths = Set(worktreeURLs.map { $0.standardizedFileURL.path })
        guard !removedPaths.isEmpty else { return }
        var associations = decodedAssociations()
        associations = associations.filter { key, _ in
            key.workspacePath != workspacePath || !removedPaths.contains(key.worktreePath)
        }
        persist(associations)
    }

    private struct Key: Hashable {
        let workspacePath: String
        let worktreePath: String
    }

    private func normalizedKey(workspaceURL: URL, worktreeURL: URL) -> Key {
        Key(
            workspacePath: workspaceURL.standardizedFileURL.path,
            worktreePath: worktreeURL.standardizedFileURL.path
        )
    }

    private func mutate(
        workspaceURL: URL,
        worktreeURL: URL,
        change: (inout WorktreeReferences) -> Void
    ) {
        let key = normalizedKey(workspaceURL: workspaceURL, worktreeURL: worktreeURL)
        var associations = decodedAssociations()
        var references = associations[key] ?? .empty
        change(&references)
        if references == .empty {
            associations.removeValue(forKey: key)
        } else {
            associations[key] = references
        }
        persist(associations)
    }

    private func repairMalformedStorageIfNeeded() {
        let associations = decodedAssociations(repairingMalformedStorage: true)
        if defaults.object(forKey: Self.storageKey) != nil {
            persist(associations)
        }
    }

    private func decodedAssociations(repairingMalformedStorage: Bool = false) -> [Key: WorktreeReferences] {
        guard let data = defaults.data(forKey: Self.storageKey) else { return [:] }
        guard let stored = try? JSONDecoder().decode([StoredAssociation].self, from: data) else {
            if repairingMalformedStorage { defaults.removeObject(forKey: Self.storageKey) }
            return [:]
        }

        var result: [Key: WorktreeReferences] = [:]
        for entry in stored {
            let workspaceURL = URL(fileURLWithPath: entry.workspacePath).standardizedFileURL
            let worktreeURL = URL(fileURLWithPath: entry.worktreePath).standardizedFileURL
            guard !entry.workspacePath.isEmpty, !entry.worktreePath.isEmpty else { continue }
            let markdown = entry.markdownPath.flatMap(Self.persistedMarkdownURL)
            let issue = entry.issueURL.flatMap(GitHubIssueReference.init(urlString:))
            let references = WorktreeReferences(markdownURL: markdown, issue: issue)
            if references != .empty {
                result[normalizedKey(workspaceURL: workspaceURL, worktreeURL: worktreeURL)] = references
            }
        }
        return result
    }

    private func persist(_ associations: [Key: WorktreeReferences]) {
        guard !associations.isEmpty else {
            defaults.removeObject(forKey: Self.storageKey)
            return
        }
        let stored = associations.map { key, references in
            StoredAssociation(
                workspacePath: key.workspacePath,
                worktreePath: key.worktreePath,
                markdownPath: references.markdownURL?.standardizedFileURL.path,
                issueURL: references.issue?.url.absoluteString
            )
        }
        guard let data = try? JSONEncoder().encode(stored) else { return }
        defaults.set(data, forKey: Self.storageKey)
    }

    private static func persistedMarkdownURL(path: String) -> URL? {
        guard path.hasPrefix("/") else { return nil }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        guard ["md", "markdown"].contains(url.pathExtension.lowercased()) else { return nil }
        return url
    }

    private static func isExistingMarkdownFile(_ url: URL) -> Bool {
        guard persistedMarkdownURL(path: url.path) == url.standardizedFileURL else { return false }
        let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
        return values?.isRegularFile == true
    }
}
