import Foundation

struct WorktreeActionPreview: Equatable, Sendable {
    let branch: String
    let location: URL
    let isValidBranch: Bool
    let isValidLocation: Bool

    static func make(name: String, under root: URL) -> WorktreeActionPreview {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let slug = trimmed
            .replacingOccurrences(of: "[^A-Za-z0-9_-]+", with: "-", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        let safeSlug = slug.isEmpty ? "worktree" : slug
        let branch = ["feature/", "bugfix/", "hotfix/"].contains(where: { trimmed.hasPrefix($0) })
            ? trimmed
            : "feature/\(safeSlug)"
        let location = root.standardizedFileURL.appendingPathComponent("wt-\(safeSlug)", isDirectory: true)
        return WorktreeActionPreview(
            branch: branch,
            location: location,
            isValidBranch: isValidBranchName(branch),
            isValidLocation: !location.path.isEmpty
        )
    }

    private static func isValidBranchName(_ branch: String) -> Bool {
        !branch.isEmpty && !branch.contains("..") && !branch.contains("~") && !branch.contains("^") &&
            !branch.contains(":") && !branch.contains("?") && !branch.contains("*") &&
            !branch.contains("[") && !branch.hasSuffix("/") && !branch.hasSuffix(".") &&
            !branch.contains("\\") && !branch.contains(" ")
    }
}

enum WorktreeCreationBase: Equatable, Sendable {
    case workspaceSelectedBranch
    case selectedWorktreeBranch
    case existingLocalBranch(String)
    case existingRemoteBranch(remote: String, branch: String)
}

struct WorktreeCreationRequest: Equatable, Sendable {
    let name: String
    let branch: String
    let location: URL
    let base: WorktreeCreationBase
    let useLatestRemote: Bool

    init(name: String, branch: String, location: URL, base: WorktreeCreationBase, useLatestRemote: Bool = false) {
        self.name = name
        self.branch = branch
        self.location = location.standardizedFileURL
        self.base = base
        self.useLatestRemote = useLatestRemote
    }
}

struct WorktreeBranchChoice: Equatable, Sendable {
    let name: String
    let isRemote: Bool
    let remote: String?
    let attachedPath: URL?
}

enum WorktreeActionError: Error, Equatable, Sendable {
    case invalidBranch(String)
    case invalidLocation(URL)
    case targetAlreadyExists(URL)
    case branchAlreadyExists(String)
    case branchAlreadyAttached(String, URL)
    case primaryWorktreeProtected(URL)
    case worktreeNotFound(URL)
    case gitUnavailable(URL)
    case gitFailed(URL, reason: String)
    case cancelled
}

final class GitWorktreeActionService: @unchecked Sendable {
    private let gitExecutable: URL
    private let timeout: Duration

    init(gitExecutable: URL = URL(fileURLWithPath: "/usr/bin/git"), timeout: Duration = .seconds(15)) {
        self.gitExecutable = gitExecutable
        self.timeout = timeout
    }

    func branchChoices(in projectRoot: URL) async throws -> [WorktreeBranchChoice] {
        let entries = try await discovery(in: projectRoot)
        let attached = Dictionary(uniqueKeysWithValues: entries.map { ($0.branch, $0.path) })
        let output = try await run(arguments: ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], in: entries[0].path)
        return output.split(whereSeparator: \.isNewline).compactMap { line in
            let ref = String(line)
            if ref == "refs/remotes" || ref.hasSuffix("/HEAD") { return nil }
            if ref.hasPrefix("refs/heads/") {
                let name = String(ref.dropFirst("refs/heads/".count))
                return WorktreeBranchChoice(name: name, isRemote: false, remote: nil, attachedPath: attached[name])
            }
            guard ref.hasPrefix("refs/remotes/") else { return nil }
            let remoteRef = String(ref.dropFirst("refs/remotes/".count))
            guard let separator = remoteRef.firstIndex(of: "/") else { return nil }
            let remote = String(remoteRef[..<separator])
            let name = String(remoteRef[remoteRef.index(after: separator)...])
            return WorktreeBranchChoice(name: name, isRemote: true, remote: remote, attachedPath: attached[name])
        }.sorted { $0.name == $1.name ? $0.isRemote && !$1.isRemote : $0.name < $1.name }
    }

    func create(
        request: WorktreeCreationRequest,
        projectRoot: URL,
        workspaceSelectedBranch: String,
        selectedWorktreeBranch: String? = nil
    ) async throws -> [GitWorktreeEntry] {
        try Task.checkCancellation()
        guard FileManager.default.isExecutableFile(atPath: gitExecutable.path) else { throw WorktreeActionError.gitUnavailable(gitExecutable) }
        guard !request.branch.isEmpty, !request.branch.contains(" "), !request.branch.contains("..") else {
            throw WorktreeActionError.invalidBranch(request.branch)
        }
        guard !FileManager.default.fileExists(atPath: request.location.path) else {
            throw WorktreeActionError.targetAlreadyExists(request.location)
        }
        guard FileManager.default.fileExists(atPath: request.location.deletingLastPathComponent().path) else {
            throw WorktreeActionError.invalidLocation(request.location)
        }
        let entries = try await discovery(in: projectRoot)
        if let attached = entries.first(where: { $0.branch == request.branch }) {
            throw WorktreeActionError.branchAlreadyAttached(request.branch, attached.path)
        }
        let repository = entries[0].path
        var arguments = ["worktree", "add", "--quiet"]
        var workspaceBase = workspaceSelectedBranch
        if request.useLatestRemote, case .workspaceSelectedBranch = request.base {
            let upstream = try await run(
                arguments: ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "\(workspaceSelectedBranch)@{upstream}"],
                in: repository
            ).trimmingCharacters(in: .whitespacesAndNewlines)
            guard let separator = upstream.firstIndex(of: "/"), separator != upstream.startIndex else {
                throw WorktreeActionError.gitFailed(repository, reason: "The workspace branch has no usable remote-tracking upstream.")
            }
            let remote = String(upstream[..<separator])
            let remoteBranch = String(upstream[upstream.index(after: separator)...])
            guard !remoteBranch.isEmpty, remote != "." else {
                throw WorktreeActionError.gitFailed(repository, reason: "The workspace branch has no usable remote-tracking upstream.")
            }
            _ = try await run(arguments: ["fetch", remote, remoteBranch], in: repository)
            workspaceBase = upstream
        }
        switch request.base {
        case .workspaceSelectedBranch:
            arguments += ["-b", request.branch, request.location.path, workspaceBase]
        case .selectedWorktreeBranch:
            guard let selectedWorktreeBranch else { throw WorktreeActionError.invalidBranch("missing selected worktree branch") }
            arguments += ["-b", request.branch, request.location.path, selectedWorktreeBranch]
        case let .existingLocalBranch(branch):
            arguments += [request.location.path, branch]
        case let .existingRemoteBranch(remote, branch):
            arguments += ["-b", branch, request.location.path, "\(remote)/\(branch)"]
        }
        _ = try await run(arguments: arguments, in: repository)
        return try await discovery(in: projectRoot)
    }

    func remove(path: URL, projectRoot: URL, force: Bool, deleteLocalBranch: Bool) async throws {
        try Task.checkCancellation()
        let normalized = path.standardizedFileURL
        let entries = try await discovery(in: projectRoot)
        guard let entry = entries.first(where: { $0.path.standardizedFileURL == normalized }) else {
            throw WorktreeActionError.worktreeNotFound(normalized)
        }
        guard !entry.isPrimary else { throw WorktreeActionError.primaryWorktreeProtected(normalized) }
        let repository = entries[0].path
        var arguments = ["worktree", "remove"]
        if force { arguments.append("--force") }
        arguments.append(normalized.path)
        _ = try await run(arguments: arguments, in: repository)
        if deleteLocalBranch {
            _ = try await run(arguments: ["branch", "-d", entry.branch], in: repository)
        }
        _ = try await discovery(in: projectRoot)
    }

    private func discovery(in projectRoot: URL) async throws -> [GitWorktreeEntry] {
        do { return try await GitWorktreeDiscovery(gitExecutable: gitExecutable, timeout: timeout).worktrees(in: projectRoot) }
        catch let error as GitWorktreeDiscoveryError { throw map(error) }
    }

    private func map(_ error: GitWorktreeDiscoveryError) -> WorktreeActionError {
        switch error {
        case let .gitUnavailable(url): return .gitUnavailable(url)
        case let .gitFailed(url, reason): return .gitFailed(url, reason: reason)
        case let .projectRootUnavailable(url), let .repositoryNotFound(url), let .multipleRepositories(url): return .invalidLocation(url)
        case let .branchNotFound(branch, _), let .ambiguousBranch(branch, _): return .invalidBranch(branch)
        case let .detachedWorktree(url): return .gitFailed(url, reason: "Detached worktrees cannot be used for this action.")
        }
    }

    private func run(arguments: [String], in directory: URL) async throws -> String {
        let operation = ActionProcessOperation(executable: gitExecutable, arguments: ["-C", directory.path] + arguments, currentDirectory: directory)
        return try await withTaskCancellationHandler(operation: {
            try await withThrowingTaskGroup(of: String.self) { group in
                group.addTask { try await operation.run() }
                group.addTask {
                    try await Task.sleep(for: self.timeout)
                    operation.cancelForTimeout()
                    throw WorktreeActionError.gitFailed(directory, reason: "Git operation timed out.")
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
        }, onCancel: { operation.cancel() })
    }
}

private final class ActionProcessOperation: @unchecked Sendable {
    private let process: Process
    private let lock = NSLock()
    private var cancelled = false
    private var timedOut = false

    init(executable: URL, arguments: [String], currentDirectory: URL) {
        process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
    }

    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        if process.isRunning { process.terminate() }
    }

    func cancelForTimeout() {
        lock.lock(); defer { lock.unlock() }
        timedOut = true
        if process.isRunning { process.terminate() }
    }

    private func state() -> (cancelled: Bool, timedOut: Bool) {
        lock.lock(); defer { lock.unlock() }
        return (cancelled, timedOut)
    }

    func run() async throws -> String {
        try await Task.detached(priority: .utility) { [self] in
            if state().cancelled { throw WorktreeActionError.cancelled }
            let stdout = Pipe(); let stderr = Pipe()
            process.standardOutput = stdout; process.standardError = stderr
            do { try process.run() } catch { throw WorktreeActionError.gitFailed(process.currentDirectoryURL ?? URL(fileURLWithPath: "/"), reason: error.localizedDescription) }
            process.waitUntilExit()
            let output = String(data: stdout.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            let errorOutput = String(data: stderr.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            let processState = state()
            if processState.timedOut { throw WorktreeActionError.gitFailed(process.currentDirectoryURL ?? URL(fileURLWithPath: "/"), reason: "Git operation timed out.") }
            if Task.isCancelled || processState.cancelled { throw WorktreeActionError.cancelled }
            guard process.terminationStatus == 0 else {
                throw WorktreeActionError.gitFailed(process.currentDirectoryURL ?? URL(fileURLWithPath: "/"), reason: String(errorOutput.prefix(64 * 1024)))
            }
            return output
        }.value
    }
}
