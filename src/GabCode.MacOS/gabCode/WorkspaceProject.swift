import Combine
import Darwin
import Foundation

enum WorkspaceProjectTitle {
    static func value(name: String, folder: URL) -> String {
        "\(name) — \(folder.lastPathComponent) — gabCode"
    }
}

enum WorkspaceOpenError: Error, Equatable {
    case cancelled
    case unreadableDescriptor(URL)
    case malformedDescriptor(URL)
    case invalidFolder(URL)
    case repositoryNotFound(URL)
    case multipleRepositories(URL)
    case branchNotFound(String, URL)
    case gitUnavailable(URL)
    case gitFailed(URL, reason: String)
    case descriptorWriteFailed(URL)

    var message: String {
        switch self {
        case .cancelled: "Workspace opening was cancelled."
        case let .unreadableDescriptor(url): "Could not read workspace file: \(url.path)"
        case let .malformedDescriptor(url): "The workspace file is malformed or unsupported: \(url.path)"
        case let .invalidFolder(url): "The project folder is missing or inaccessible: \(url.path)"
        case let .repositoryNotFound(url): "No Git worktree set was found beneath: \(url.path)"
        case let .multipleRepositories(url): "More than one Git repository was found beneath: \(url.path)"
        case let .branchNotFound(branch, url): "Branch \"\(branch)\" could not be resolved beneath: \(url.path)"
        case let .gitUnavailable(url): "Git could not be found at \(url.path)."
        case let .gitFailed(url, reason): "Git validation failed for \(url.path).\n\(reason)"
        case let .descriptorWriteFailed(url): "Could not write workspace file: \(url.path)"
        }
    }
}

enum WorkspaceProjectSurfaceState: Equatable {
    case empty
    case loading
    case ready
    case recovery(WorkspaceOpenError)
}

@MainActor
final class WorkspaceProjectController: ObservableObject {
    @Published private(set) var state: WorkspaceProjectSurfaceState = .empty
    @Published private(set) var activeDescriptor: WorkspaceDescriptor?
    @Published private(set) var projectRoot: URL?
    @Published private(set) var descriptorBranch: String?
    @Published private(set) var worktrees: [WorktreeNavigationEntry] = []
    @Published private(set) var orphanedWorktreePaths: [URL] = []
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshError: WorkspaceOpenError?
    @Published private(set) var worktreeActionError: WorktreeActionError?
    @Published private(set) var requestedSelectionPath: URL?
    private let worktreeActionService = GitWorktreeActionService()

    let preference: WorkspacePreference
    private var refreshGeneration = 0
    private var operationGeneration = 0
    private let gitValidator: GitRepositoryValidator
    private let worktreeDiscovery: GitWorktreeDiscovery
    private let worktreeLoader: @Sendable (URL) async throws -> [GitWorktreeEntry]

    init(
        defaults: UserDefaults = .standard,
        gitValidator: GitRepositoryValidator = GitRepositoryValidator(),
        worktreeDiscovery: GitWorktreeDiscovery = GitWorktreeDiscovery(),
        worktreeLoader: (@Sendable (URL) async throws -> [GitWorktreeEntry])? = nil
    ) {
        preference = WorkspacePreference(defaults: defaults)
        self.gitValidator = gitValidator
        self.worktreeDiscovery = worktreeDiscovery
        self.worktreeLoader = worktreeLoader ?? { root in
            try await worktreeDiscovery.worktrees(in: root)
        }
    }

    var windowTitle: String {
        guard let activeDescriptor else { return "gabCode" }
        return WorkspaceProjectTitle.value(name: activeDescriptor.name, folder: activeDescriptor.resolvedFolder)
    }

    func availableBranches(in projectRoot: URL) async -> Result<[String], WorkspaceOpenError> {
        do {
            return .success(try await worktreeDiscovery.branches(in: projectRoot))
        } catch {
            return .failure(map(error, projectRoot: projectRoot))
        }
    }

    func reopenRememberedWorkspace() async -> Bool {
        guard let url = preference.lastWorkspaceURL else {
            state = .empty
            return false
        }
        let opened = await openWorkspace(at: url)
        if !opened { preference.lastWorkspaceURL = nil }
        return opened
    }

    func openWorkspace(at url: URL) async -> Bool {
        operationGeneration += 1
        let generation = operationGeneration
        state = .loading
        do {
            let descriptor = try loadDescriptor(at: url)
            try validateFolder(descriptor.projectRoot)
            let discovered = try await worktreeLoader(descriptor.projectRoot)
            guard !Task.isCancelled, generation == operationGeneration else { return false }
            let matches = discovered.filter { $0.branch == descriptor.branch }
            guard matches.count == 1, let selected = matches.first else {
                throw matches.isEmpty
                    ? WorkspaceOpenError.branchNotFound(descriptor.branch, descriptor.projectRoot)
                    : WorkspaceOpenError.gitFailed(descriptor.projectRoot, reason: "Branch \"\(descriptor.branch)\" resolves to more than one registered worktree.")
            }
            try validateFolder(selected.path)
            let validation = await gitValidator.validate(folder: selected.path)
            guard !Task.isCancelled, generation == operationGeneration else { return false }
            try validateGitResult(validation, folder: selected.path)
            activeDescriptor = descriptor.resolved(to: selected.path)
            projectRoot = descriptor.projectRoot.standardizedFileURL
            descriptorBranch = descriptor.branch
            let reconciliation = WorktreeReconciliation.reconcile(previous: [], discovered: discovered, retainedPaths: [])
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
            preference.lastWorkspaceURL = url.standardizedFileURL
            state = .ready
            return true
        } catch let error as WorkspaceOpenError {
            state = .recovery(error)
            return false
        } catch {
            state = .recovery(map(error, projectRoot: url))
            return false
        }
    }

    func forgetOrphan(path: URL) {
        let normalized = path.standardizedFileURL
        orphanedWorktreePaths.removeAll { $0.standardizedFileURL == normalized }
    }

    func cancelRefresh() {
        refreshGeneration += 1
        isRefreshing = false
    }

    func refreshWorktrees(retainedPaths: [URL] = []) async {
        guard let projectRoot, !isRefreshing else { return }
        isRefreshing = true
        refreshError = nil
        refreshGeneration += 1
        operationGeneration += 1
        let generation = refreshGeneration
        defer {
            if generation == refreshGeneration { isRefreshing = false }
        }
        do {
            let discovered = try await worktreeLoader(projectRoot)
            guard !Task.isCancelled, generation == refreshGeneration else { return }
            let reconciliation = WorktreeReconciliation.reconcile(
                previous: worktrees,
                discovered: discovered,
                retainedPaths: retainedPaths,
                existingOrphanedPaths: orphanedWorktreePaths
            )
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
        } catch {
            guard !Task.isCancelled, generation == refreshGeneration else { return }
            refreshError = map(error, projectRoot: projectRoot)
        }
    }

    func createWorktree(
        request: WorktreeCreationRequest,
        selectedWorktreeBranch: String? = nil
    ) async -> Bool {
        guard let projectRoot, let workspaceBranch = descriptorBranch else {
            worktreeActionError = .invalidLocation(projectRoot ?? URL(fileURLWithPath: "/"))
            return false
        }
        worktreeActionError = nil
        do {
            let discovered = try await worktreeActionService.create(
                request: request,
                projectRoot: projectRoot,
                workspaceSelectedBranch: workspaceBranch,
                selectedWorktreeBranch: selectedWorktreeBranch
            )
            let reconciliation = WorktreeReconciliation.reconcile(
                previous: worktrees,
                discovered: discovered,
                retainedPaths: []
            )
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
            requestedSelectionPath = request.location.standardizedFileURL
            return true
        } catch let error as WorktreeActionError {
            worktreeActionError = error
            return false
        } catch {
            worktreeActionError = .gitFailed(projectRoot, reason: error.localizedDescription)
            return false
        }
    }

    func branchChoices() async -> [WorktreeBranchChoice] {
        guard let projectRoot else { return [] }
        do {
            worktreeActionError = nil
            return try await worktreeActionService.branchChoices(in: projectRoot)
        } catch let error as WorktreeActionError {
            worktreeActionError = error
            return []
        } catch {
            worktreeActionError = .gitFailed(projectRoot, reason: error.localizedDescription)
            return []
        }
    }

    func worktreeIsDirty(path: URL) async -> Bool? {
        guard let projectRoot else { return nil }
        do { return try await worktreeActionService.isDirty(path: path, projectRoot: projectRoot) }
        catch let error as WorktreeActionError { worktreeActionError = error; return nil }
        catch { worktreeActionError = .gitFailed(projectRoot, reason: error.localizedDescription); return nil }
    }

    func removeWorktree(path: URL, force: Bool, deleteLocalBranch: Bool, retainedPaths: [URL] = []) async -> Bool {
        guard let projectRoot else {
            worktreeActionError = .invalidLocation(URL(fileURLWithPath: "/"))
            return false
        }
        worktreeActionError = nil
        do {
            try await worktreeActionService.remove(
                path: path,
                projectRoot: projectRoot,
                force: force,
                deleteLocalBranch: deleteLocalBranch
            )
            let discovered = try await worktreeDiscovery.worktrees(in: projectRoot)
            let priorRemaining = worktrees.filter { $0.path.standardizedFileURL != path.standardizedFileURL }
            let reconciliation = WorktreeReconciliation.reconcile(
                previous: priorRemaining,
                discovered: discovered,
                retainedPaths: retainedPaths.filter { $0.standardizedFileURL != path.standardizedFileURL }
            )
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
            requestedSelectionPath = discovered.first?.path
            return true
        } catch let error as WorktreeActionError {
            worktreeActionError = error
            if case .localBranchDeletionFailed = error,
               let discovered = try? await worktreeDiscovery.worktrees(in: projectRoot) {
                let reconciliation = WorktreeReconciliation.reconcile(
                    previous: worktrees.filter { $0.path.standardizedFileURL != path.standardizedFileURL },
                    discovered: discovered,
                    retainedPaths: retainedPaths.filter { $0.standardizedFileURL != path.standardizedFileURL }
                )
                worktrees = reconciliation.worktrees
                orphanedWorktreePaths = reconciliation.orphanedPaths
                requestedSelectionPath = discovered.first?.path
            } else {
                await refreshWorktrees(retainedPaths: retainedPaths)
            }
            return false
        } catch {
            worktreeActionError = .gitFailed(projectRoot, reason: error.localizedDescription)
            await refreshWorktrees(retainedPaths: retainedPaths)
            return false
        }
    }

    func deleteLocalBranch(_ branch: String, force: Bool) async -> Bool {
        guard let projectRoot else { return false }
        do {
            try await worktreeActionService.deleteLocalBranch(branch, projectRoot: projectRoot, force: force)
            worktreeActionError = nil
            return true
        } catch let error as WorktreeActionError {
            worktreeActionError = error
            return false
        } catch {
            worktreeActionError = .gitFailed(projectRoot, reason: error.localizedDescription)
            return false
        }
    }

    func createWorkspace(
        name: String,
        projectRoot: URL,
        branch: String,
        descriptorURL: URL
    ) async -> Bool {
        state = .loading
        do {
            try validateFolder(projectRoot)
            let resolved = try await worktreeDiscovery.resolve(branch: branch, in: projectRoot)
            try validateFolder(resolved)
            let validation = await gitValidator.validate(folder: resolved)
            try validateGitResult(validation, folder: resolved)
            try WorkspaceDescriptor.write(name: name, projectRoot: projectRoot, branch: branch, to: descriptorURL)
            return await openWorkspace(at: descriptorURL)
        } catch let error as WorkspaceOpenError {
            state = .recovery(error)
            return false
        } catch {
            state = .recovery(.descriptorWriteFailed(descriptorURL))
            return false
        }
    }

    private func loadDescriptor(at url: URL) throws -> WorkspaceDescriptor {
        let data: Data
        do { data = try Data(contentsOf: url) } catch { throw WorkspaceOpenError.unreadableDescriptor(url) }
        do { return try WorkspaceDescriptor.decode(data: data, from: url) }
        catch { throw WorkspaceOpenError.malformedDescriptor(url) }
    }

    private func validateFolder(_ folder: URL) throws {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory),
              isDirectory.boolValue,
              access(folder.path, R_OK | X_OK) == 0
        else { throw WorkspaceOpenError.invalidFolder(folder) }
    }

    private func validateGitResult(_ result: GitRepositoryValidationResult, folder: URL) throws {
        switch result {
        case .valid: return
        case let .gitUnavailable(url): throw WorkspaceOpenError.gitUnavailable(url)
        case .notRepository: throw WorkspaceOpenError.repositoryNotFound(folder)
        case let .failed(_, _, stderr): throw WorkspaceOpenError.gitFailed(folder, reason: stderr)
        case .timedOut: throw WorkspaceOpenError.gitFailed(folder, reason: "Git validation timed out.")
        case .cancelled: throw WorkspaceOpenError.cancelled
        }
    }

    private func map(_ error: Error, projectRoot: URL) -> WorkspaceOpenError {
        switch error {
        case let error as GitWorktreeDiscoveryError:
            switch error {
            case let .projectRootUnavailable(url): return .invalidFolder(url)
            case let .repositoryNotFound(url): return .repositoryNotFound(url)
            case let .multipleRepositories(url): return .multipleRepositories(url)
            case let .branchNotFound(branch, url): return .branchNotFound(branch, url)
            case let .ambiguousBranch(branch, url): return .gitFailed(url, reason: "Branch \"\(branch)\" resolves to more than one registered worktree.")
            case let .detachedWorktree(url): return .gitFailed(url, reason: "The selected worktree is detached and has no branch name.")
            case let .gitUnavailable(url): return .gitUnavailable(url)
            case let .gitFailed(url, reason): return .gitFailed(url, reason: reason)
            }
        case let error as WorkspaceOpenError: return error
        default: return .gitFailed(projectRoot, reason: error.localizedDescription)
        }
    }
}
