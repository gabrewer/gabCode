import Foundation

struct GitWorktreeEntry: Equatable, Sendable {
    let path: URL
    let branch: String?
}

enum GitWorktreeDiscoveryError: Error, Equatable {
    case projectRootUnavailable(URL)
    case repositoryNotFound(URL)
    case multipleRepositories(URL)
    case branchNotFound(String, URL)
    case detachedWorktree(URL)
    case gitUnavailable(URL)
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
        return entries.compactMap(\.branch).sorted()
    }

    func resolve(branch: String, in projectRoot: URL) async throws -> URL {
        let entries = try await worktrees(in: projectRoot)
        let matches = entries.filter { $0.branch == branch }
        guard let match = matches.first else {
            throw GitWorktreeDiscoveryError.branchNotFound(branch, projectRoot.standardizedFileURL)
        }
        return match.path
    }

    func worktrees(in projectRoot: URL) async throws -> [GitWorktreeEntry] {
        let root = projectRoot.standardizedFileURL
        guard isDirectory(root) else {
            throw GitWorktreeDiscoveryError.projectRootUnavailable(root)
        }
        guard FileManager.default.isExecutableFile(atPath: gitExecutable.path) else {
            throw GitWorktreeDiscoveryError.gitUnavailable(gitExecutable)
        }

        let repositories = repositoryCandidates(under: root)
        guard !repositories.isEmpty else {
            throw GitWorktreeDiscoveryError.repositoryNotFound(root)
        }
        guard repositories.count == 1, let repository = repositories.first else {
            throw GitWorktreeDiscoveryError.multipleRepositories(root)
        }

        let output = try await runGit(
            arguments: ["-C", repository.path, "worktree", "list", "--porcelain"],
            currentDirectory: repository
        )
        let entries = parse(output: output)
            .filter { isDescendant($0.path, of: root) }
        guard !entries.isEmpty else {
            throw GitWorktreeDiscoveryError.repositoryNotFound(root)
        }
        return entries
    }

    private func repositoryCandidates(under root: URL) -> [URL] {
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
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]),
                  values.isDirectory == true,
                  values.isSymbolicLink != true,
                  url.pathComponents.count - root.pathComponents.count <= 3
            else {
                continue
            }
            if isDirectory(url.appendingPathComponent(".git")) {
                candidates.append(url.standardizedFileURL)
                enumerator.skipDescendants()
            }
        }
        return Array(Set(candidates)).sorted { $0.path < $1.path }
    }

    private func parse(output: String) -> [GitWorktreeEntry] {
        output.components(separatedBy: "\n\n").compactMap { block in
            let lines = block.split(separator: "\n", omittingEmptySubsequences: true)
            guard let worktreeLine = lines.first(where: { $0.hasPrefix("worktree ") }) else {
                return nil
            }
            let path = URL(fileURLWithPath: String(worktreeLine.dropFirst("worktree ".count)), isDirectory: true)
                .standardizedFileURL
            let branch = lines.first(where: { $0.hasPrefix("branch refs/heads/") })
                .map { String($0.dropFirst("branch refs/heads/".count)) }
            return GitWorktreeEntry(path: path, branch: branch)
        }
    }

    private func isDirectory(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    private func isDescendant(_ candidate: URL, of root: URL) -> Bool {
        let candidatePath = candidate.standardizedFileURL.path
        let rootPath = root.standardizedFileURL.path
        return candidatePath == rootPath || candidatePath.hasPrefix(rootPath + "/")
    }

    private func runGit(arguments: [String], currentDirectory: URL) async throws -> String {
        let process = Process()
        process.executableURL = gitExecutable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        try process.run()

        let completed = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                process.waitUntilExit()
                return true
            }
            group.addTask {
                do {
                    try await Task.sleep(for: self.timeout)
                    if process.isRunning { process.terminate() }
                    return false
                } catch {
                    return true
                }
            }
            let result = await group.next() ?? false
            group.cancelAll()
            return result
        }
        guard completed else {
            if process.isRunning { process.terminate() }
            throw GitWorktreeDiscoveryError.gitFailed(currentDirectory, reason: "Git worktree discovery timed out.")
        }

        let output = String(data: stdout.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        let errorOutput = String(data: stderr.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            throw GitWorktreeDiscoveryError.gitFailed(currentDirectory, reason: String(errorOutput.prefix(64 * 1024)))
        }
        return output
    }
}
