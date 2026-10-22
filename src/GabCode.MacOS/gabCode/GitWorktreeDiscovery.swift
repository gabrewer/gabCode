import Foundation

struct GitWorktreeEntry: Equatable, Sendable {
    let path: URL
    let branch: String
    let isPrimary: Bool
}

enum GitWorktreeDiscoveryError: Error, Equatable {
    case projectRootUnavailable(URL)
    case repositoryNotFound(URL)
    case multipleRepositories(URL)
    case branchNotFound(String, URL)
    case ambiguousBranch(String, URL)
    case detachedWorktree(URL)
    case gitUnavailable(URL)
    case cleanupFailed(URL)
    case gitFailed(URL, reason: String)
}

final class GitWorktreeDiscovery: @unchecked Sendable {
    private let gitExecutable: URL
    private let timeout: Duration

    init(
        gitExecutable: URL = URL(fileURLWithPath: "/usr/bin/git"),
        timeout: Duration = .seconds(5)
    ) {
        self.gitExecutable = gitExecutable
        self.timeout = timeout
    }

    func branches(in projectRoot: URL) async throws -> [String] {
        let entries = try await worktrees(in: projectRoot)
        return entries.map(\.branch).sorted()
    }

    func resolve(branch: String, in projectRoot: URL) async throws -> URL {
        let entries = try await worktrees(in: projectRoot)
        let matches = entries.filter { $0.branch == branch }
        guard matches.count == 1, let match = matches.first else {
            if matches.isEmpty {
                throw GitWorktreeDiscoveryError.branchNotFound(branch, projectRoot.standardizedFileURL)
            }
            throw GitWorktreeDiscoveryError.ambiguousBranch(branch, projectRoot.standardizedFileURL)
        }
        return match.path
    }

    func validateLocalBranch(_ branch: String, in projectRoot: URL) async throws {
        let root = projectRoot.standardizedFileURL
        guard !branch.isEmpty, !branch.contains("\0") else {
            throw GitWorktreeDiscoveryError.branchNotFound(branch, root)
        }
        guard try await Self.checkDirectory(root) else { throw GitWorktreeDiscoveryError.projectRootUnavailable(root) }
        guard FileManager.default.isExecutableFile(atPath: gitExecutable.path) else {
            throw GitWorktreeDiscoveryError.gitUnavailable(gitExecutable)
        }
        let deadline = ContinuousClock.now.advanced(by: timeout)
        let repositories = try await repositoryCandidates(under: root)
        guard repositories.count == 1, let repository = repositories.first else {
            if repositories.isEmpty { throw GitWorktreeDiscoveryError.repositoryNotFound(root) }
            throw GitWorktreeDiscoveryError.multipleRepositories(root)
        }
        do {
            let result = try await runGitResult(
                arguments: ["-C", repository.path, "show-ref", "--verify", "--quiet", "--", "refs/heads/\(branch)"],
                currentDirectory: repository,
                acceptedExitStatuses: [0, 1],
                budget: try remaining(until: deadline, root: root)
            )
            guard result.status == 0 else {
                throw GitWorktreeDiscoveryError.branchNotFound(branch, root)
            }
        } catch is CancellationError {
            throw GitWorktreeDiscoveryError.gitFailed(root, reason: "Local branch validation was cancelled.")
        } catch let error as GitWorktreeDiscoveryError {
            throw error
        } catch {
            throw GitWorktreeDiscoveryError.gitFailed(root, reason: error.localizedDescription)
        }
    }

    func worktrees(in projectRoot: URL) async throws -> [GitWorktreeEntry] {
        let root = projectRoot.standardizedFileURL
        guard try await Self.checkDirectory(root) else {
            throw GitWorktreeDiscoveryError.projectRootUnavailable(root)
        }
        guard FileManager.default.isExecutableFile(atPath: gitExecutable.path) else {
            throw GitWorktreeDiscoveryError.gitUnavailable(gitExecutable)
        }

        let deadline = ContinuousClock.now.advanced(by: timeout)
        let repositories = try await repositoryCandidates(under: root)
        guard !repositories.isEmpty else {
            throw GitWorktreeDiscoveryError.repositoryNotFound(root)
        }
        guard repositories.count == 1, let repository = repositories.first else {
            throw GitWorktreeDiscoveryError.multipleRepositories(root)
        }

        let output = try await runGit(
            arguments: ["-C", repository.path, "worktree", "list", "--porcelain"],
            currentDirectory: repository,
            budget: try remaining(until: deadline, root: root)
        )
        let entries = parse(output: output)
            .filter { isDescendant($0.path, of: root) }
        guard !entries.isEmpty else {
            throw GitWorktreeDiscoveryError.repositoryNotFound(root)
        }
        return entries
    }

    private nonisolated static func checkDirectory(_ root: URL) async throws -> Bool {
        let work = Task.detached(priority: .utility) { try Task.checkCancellation(); return isDirectory(root) }
        return try await withTaskCancellationHandler {
            try await work.value
        } onCancel: { work.cancel() }
    }

    private func repositoryCandidates(under root: URL) async throws -> [URL] {
        let work = Task.detached(priority: .utility) { [root] in try Self.scanCandidates(under: root) }
        return try await withTaskCancellationHandler {
            try await work.value
        } onCancel: { work.cancel() }
    }

    private nonisolated static func scanCandidates(under root: URL) throws -> [URL] {
        var candidates: [URL] = []
        let manager = FileManager.default
        if isDirectory(root.appendingPathComponent(".git")) {
            candidates.append(root)
        }
        guard let enumerator = manager.enumerator(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants]
        ) else {
            return candidates
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        var visited = 0
        for case let url as URL in enumerator {
            try Task.checkCancellation()
            visited += 1
            guard visited <= 5_000, ContinuousClock.now < deadline else {
                throw GitWorktreeDiscoveryError.gitFailed(root, reason: "Repository discovery exceeded its bounded directory scan. Choose a narrower project folder.")
            }
            let depth = url.pathComponents.count - root.pathComponents.count
            if depth >= 3 { enumerator.skipDescendants() }
            guard depth <= 3,
                  let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]),
                  values.isDirectory == true,
                  values.isSymbolicLink != true else { continue }
            if isDirectory(url.appendingPathComponent(".git")) {
                candidates.append(url.standardizedFileURL)
                enumerator.skipDescendants()
            }
        }
        return Array(Set(candidates)).sorted { $0.path < $1.path }
    }

    private func parse(output: String) -> [GitWorktreeEntry] {
        output.components(separatedBy: "\n\n").enumerated().compactMap { index, block in
            let lines = block.split(separator: "\n", omittingEmptySubsequences: true)
            guard let worktreeLine = lines.first(where: { $0.hasPrefix("worktree ") }),
                  let branchLine = lines.first(where: { $0.hasPrefix("branch refs/heads/") })
            else {
                // Detached worktrees have no branch-bearing workspace identity in v1.
                return nil
            }
            let path = URL(fileURLWithPath: String(worktreeLine.dropFirst("worktree ".count)), isDirectory: true)
                .standardizedFileURL
            let branch = String(branchLine.dropFirst("branch refs/heads/".count))
            return GitWorktreeEntry(path: path, branch: branch, isPrimary: index == 0)
        }
    }

    private nonisolated static func isDirectory(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    private func isDescendant(_ candidate: URL, of root: URL) -> Bool {
        let candidatePath = candidate.standardizedFileURL.path
        let rootPath = root.standardizedFileURL.path
        return candidatePath == rootPath || candidatePath.hasPrefix(rootPath + "/")
    }

    private nonisolated func remaining(until deadline: ContinuousClock.Instant, root: URL) throws -> Duration {
        let budget = ContinuousClock.now.duration(to: deadline)
        guard budget > .zero else { throw GitWorktreeDiscoveryError.gitFailed(root, reason: "Repository discovery exceeded its total time budget.") }
        return budget
    }

    private func runGit(arguments: [String], currentDirectory: URL, budget: Duration) async throws -> String {
        try await runGitResult(arguments: arguments, currentDirectory: currentDirectory, budget: budget).output
    }

    private func runGitResult(
        arguments: [String],
        currentDirectory: URL,
        acceptedExitStatuses: Set<Int32> = [0],
        budget: Duration
    ) async throws -> (output: String, status: Int32) {
        let operation = GitDiscoveryProcess(executable: gitExecutable, arguments: arguments, root: currentDirectory)
        do {
            let (output, status) = try await operation.run(budget: min(timeout, budget), accepted: acceptedExitStatuses)
            return (output, status)
        } catch GitDiscoveryProcessError.timedOut {
            throw GitWorktreeDiscoveryError.gitFailed(currentDirectory, reason: "Git worktree discovery timed out.")
        }
    }

}
