import Foundation

enum WorktreeAvailability: Equatable, Sendable {
    case available
    case unavailable
}

struct WorktreeNavigationEntry: Equatable, Sendable {
    let path: URL
    let branch: String
    let isPrimary: Bool
    let availability: WorktreeAvailability
    let consecutiveMissingRefreshes: Int

    init(
        path: URL,
        branch: String,
        isPrimary: Bool,
        availability: WorktreeAvailability = .available,
        consecutiveMissingRefreshes: Int = 0
    ) {
        self.path = path.standardizedFileURL
        self.branch = branch
        self.isPrimary = isPrimary
        self.availability = availability
        self.consecutiveMissingRefreshes = consecutiveMissingRefreshes
    }
}

struct WorktreeReconciliationResult: Equatable, Sendable {
    let worktrees: [WorktreeNavigationEntry]
    let orphanedPaths: [URL]
}

enum WorktreeReconciliation {
    static func reconcile(
        previous: [WorktreeNavigationEntry],
        discovered: [GitWorktreeEntry],
        retainedPaths: [URL],
        existingOrphanedPaths: [URL] = []
    ) -> WorktreeReconciliationResult {
        let normalizedRetained = Set(retainedPaths.map(\.standardizedFileURL))
        var discoveredByPath = Dictionary(uniqueKeysWithValues: discovered.map { ($0.path.standardizedFileURL, $0) })
        var next: [WorktreeNavigationEntry] = []
        var orphaned = Set(existingOrphanedPaths.map(\.standardizedFileURL))
        orphaned.subtract(discoveredByPath.keys)

        for prior in previous {
            let path = prior.path.standardizedFileURL
            if let entry = discoveredByPath.removeValue(forKey: path) {
                orphaned.remove(path)
                next.append(WorktreeNavigationEntry(path: entry.path, branch: entry.branch, isPrimary: entry.isPrimary))
            } else {
                let misses = prior.consecutiveMissingRefreshes + 1
                if misses < 2 {
                    next.append(WorktreeNavigationEntry(
                        path: path,
                        branch: prior.branch,
                        isPrimary: prior.isPrimary,
                        availability: .unavailable,
                        consecutiveMissingRefreshes: misses
                    ))
                } else if normalizedRetained.contains(path) {
                    orphaned.insert(path)
                }
            }
        }

        for entry in discoveredByPath.values {
            next.append(WorktreeNavigationEntry(path: entry.path, branch: entry.branch, isPrimary: entry.isPrimary))
        }

        next.sort { lhs, rhs in
            if lhs.isPrimary != rhs.isPrimary { return lhs.isPrimary }
            if lhs.availability != rhs.availability { return lhs.availability == .available }
            let folderOrder = lhs.path.lastPathComponent.localizedStandardCompare(rhs.path.lastPathComponent)
            if folderOrder != .orderedSame { return folderOrder == .orderedAscending }
            return lhs.path.path < rhs.path.path
        }

        return WorktreeReconciliationResult(
            worktrees: next,
            orphanedPaths: orphaned.sorted { $0.path < $1.path }
        )
    }
}
