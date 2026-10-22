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
    case malformedDescriptor(URL, reason: String)
    case invalidFolder(URL)
    case repositoryNotFound(URL)
    case multipleRepositories(URL)
    case branchNotFound(String, URL)
    case gitUnavailable(URL)
    case gitFailed(URL, reason: String)
    case descriptorWriteFailed(URL)
    case instancePresenceUnavailable(String)

    var message: String {
        switch self {
        case .cancelled: "Workspace opening was cancelled."
        case let .unreadableDescriptor(url): "Could not read workspace file: \(url.path)"
        case let .malformedDescriptor(url, reason): "\(reason) Workspace file: \(url.path)"
        case let .invalidFolder(url): "The project folder is missing or inaccessible: \(url.path)"
        case let .repositoryNotFound(url): "No Git worktree set was found beneath: \(url.path)"
        case let .multipleRepositories(url): "More than one Git repository was found beneath: \(url.path)"
        case let .branchNotFound(branch, url): "Branch \"\(branch)\" could not be resolved beneath: \(url.path)"
        case let .gitUnavailable(url): "Git could not be found at \(url.path)."
        case let .gitFailed(url, reason): "Git validation failed for \(url.path).\n\(reason)"
        case let .descriptorWriteFailed(url): "Could not write workspace file: \(url.path)"
        case let .instancePresenceUnavailable(reason): "Could not determine whether another gabCode window is running. Open a workspace explicitly. \(reason)"
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
    @Published private(set) var mainBranch: String?
    @Published private(set) var worktrees: [WorktreeNavigationEntry] = []
    @Published private(set) var orphanedWorktreePaths: [URL] = []
    @Published private(set) var isRefreshing = false
    @Published private(set) var isCancellingRefresh = false
    @Published private(set) var refreshCleanupFailed = false
    @Published private(set) var refreshError: WorkspaceOpenError?
    @Published private(set) var fallbackNotice: String?
    @Published private(set) var worktreeActionError: WorktreeActionError?
    @Published private(set) var requestedSelectionPath: URL?
    @Published private(set) var recoveryDescriptorURL: URL?
    private let worktreeActionService = GitWorktreeActionService()

    let preference: WorkspacePreference
    private var refreshGeneration = 0
    private var pendingRefresh: [URL]?
    private var activeRefresh: Task<[GitWorktreeEntry], Error>?
    private var openingDiscovery: Task<[GitWorktreeEntry], Error>?
    private var openingValidation: Task<GitRepositoryValidationResult, Never>?
    private var operationGeneration = 0
    private let gitValidator: GitRepositoryValidator
    private let worktreeDiscovery: GitWorktreeDiscovery
    private let worktreeLoader: @Sendable (URL) async throws -> [GitWorktreeEntry]
    private let localBranchValidator: @Sendable (String, URL) async throws -> Void

    init(
        defaults: UserDefaults = .standard,
        gitValidator: GitRepositoryValidator = GitRepositoryValidator(),
        worktreeDiscovery: GitWorktreeDiscovery = GitWorktreeDiscovery(),
        worktreeLoader: (@Sendable (URL) async throws -> [GitWorktreeEntry])? = nil,
        localBranchValidator: (@Sendable (String, URL) async throws -> Void)? = nil
    ) {
        preference = WorkspacePreference(defaults: defaults)
        self.gitValidator = gitValidator
        self.worktreeDiscovery = worktreeDiscovery
        self.worktreeLoader = worktreeLoader ?? { root in
            try await worktreeDiscovery.worktrees(in: root)
        }
        self.localBranchValidator = localBranchValidator ?? { branch, root in
            try await worktreeDiscovery.validateLocalBranch(branch, in: root)
        }
    }

    deinit {
        activeRefresh?.cancel()
        openingDiscovery?.cancel()
        openingValidation?.cancel()
    }

    var descriptorBranch: String? { mainBranch }

    func showStartupRecovery(_ error: WorkspaceOpenError) {
        state = .recovery(error)
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
        let drainingOpening = openingDiscovery
        drainingOpening?.cancel()
        let drainingValidation = openingValidation
        drainingValidation?.cancel()
        let drainingRefresh = activeRefresh
        cancelRefresh()
        if let drainingOpening { _ = await drainingOpening.result }
        if let drainingValidation { _ = await drainingValidation.value }
        if let drainingRefresh { _ = await drainingRefresh.result }
        guard generation == operationGeneration, !Task.isCancelled else { return false }
        guard !refreshCleanupFailed else {
            state = .recovery(.gitFailed(url, reason: "Owned Git cleanup could not be confirmed. This repository is blocked for this app session; inspect owned Git before quitting and reopening gabCode."))
            return false
        }
        state = .loading
        fallbackNotice = nil
        recoveryDescriptorURL = url.standardizedFileURL
        do {
            let descriptor = try loadDescriptor(at: url)
            try validateFolder(descriptor.projectRoot)
            let discovery = Task { try await worktreeLoader(descriptor.projectRoot) }
            openingDiscovery = discovery
            let discovered: [GitWorktreeEntry]
            do { discovered = try await discovery.value }
            catch {
                if generation == operationGeneration { openingDiscovery = nil }
                throw error
            }
            if generation == operationGeneration { openingDiscovery = nil }
            guard !discovery.isCancelled, !Task.isCancelled, generation == operationGeneration else { return false }
            do {
                try await localBranchValidator(descriptor.mainBranch, descriptor.projectRoot)
            } catch let error as GitWorktreeDiscoveryError {
                if case let .branchNotFound(branch, _) = error {
                    throw WorkspaceOpenError.branchNotFound(branch, url.standardizedFileURL)
                }
                throw error
            }
            guard !Task.isCancelled, generation == operationGeneration else { return false }
            let primary = discovered.first(where: { $0.isPrimary })
            let remembered = preference.selectedWorktreeURL(for: url)
            let rememberedEntry = remembered.flatMap { remembered in
                discovered.first { $0.path.standardizedFileURL == remembered.standardizedFileURL }
            }
            guard let selected = rememberedEntry ?? primary else {
                throw WorkspaceOpenError.gitFailed(descriptor.projectRoot, reason: "No registered primary worktree is available.")
            }
            var activeSelection = selected
            var stagedFallbackNotice: String?
            if remembered != nil, rememberedEntry == nil {
                guard let primary else {
                    throw WorkspaceOpenError.gitFailed(descriptor.projectRoot, reason: "No accessible primary worktree is available.")
                }
                try validateFolder(primary.path)
                activeSelection = primary
                stagedFallbackNotice = "The previously selected worktree is no longer available. Opened \(primary.path.lastPathComponent) instead."
            } else {
                do {
                    try validateFolder(selected.path)
                } catch where rememberedEntry != nil {
                    guard let primary else {
                        throw WorkspaceOpenError.gitFailed(descriptor.projectRoot, reason: "No accessible primary worktree is available.")
                    }
                    try validateFolder(primary.path)
                    activeSelection = primary
                    stagedFallbackNotice = "The previously selected worktree is no longer available. Opened \(primary.path.lastPathComponent) instead."
                }
            }
            let validationTask = Task { await gitValidator.validate(folder: activeSelection.path) }
            openingValidation = validationTask
            let validation = await validationTask.value
            if generation == operationGeneration { openingValidation = nil }
            guard !validationTask.isCancelled, !Task.isCancelled, generation == operationGeneration else { return false }
            try validateGitResult(validation, folder: activeSelection.path)
            activeDescriptor = descriptor.resolved(to: activeSelection.path)
            projectRoot = descriptor.projectRoot.standardizedFileURL
            mainBranch = descriptor.mainBranch
            let reconciliation = WorktreeReconciliation.reconcile(previous: [], discovered: discovered, retainedPaths: [])
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
            preference.lastWorkspaceURL = url.standardizedFileURL
            fallbackNotice = stagedFallbackNotice
            recoveryDescriptorURL = nil
            state = .ready
            return true
        } catch let error as WorkspaceOpenError {
            guard generation == operationGeneration else { return false }
            state = .recovery(error)
            return false
        } catch {
            guard generation == operationGeneration else { return false }
            state = .recovery(map(error, projectRoot: url))
            return false
        }
    }

    func persistSelectedWorktree(path: URL) {
        guard let workspaceURL = preference.lastWorkspaceURL else { return }
        let available = worktrees
            .filter { $0.availability == .available }
            .map(\.path)
        preference.setSelectedWorktreeURL(path, for: workspaceURL, availableWorktrees: available)
    }

    func forgetOrphan(path: URL) {
        let normalized = path.standardizedFileURL
        orphanedWorktreePaths.removeAll { $0.standardizedFileURL == normalized }
    }

    func cancelOwnedWorkForWindowClose() {
        operationGeneration += 1
        openingDiscovery?.cancel()
        openingValidation?.cancel()
        cancelRefresh()
    }

    func cancelRefresh() {
        pendingRefresh = nil
        guard isRefreshing, !refreshCleanupFailed else { return }
        refreshGeneration += 1
        isCancellingRefresh = true
        activeRefresh?.cancel()
    }

    @discardableResult
    func refreshWorktrees(retainedPaths: [URL] = []) async -> Bool {
        guard let projectRoot, !refreshCleanupFailed else { return false }
        guard !isRefreshing else {
            pendingRefresh = retainedPaths
            return false
        }
        isRefreshing = true
        isCancellingRefresh = false
        refreshError = nil
        refreshGeneration += 1
        operationGeneration += 1
        let generation = refreshGeneration
        let task = Task { try await worktreeLoader(projectRoot) }
        activeRefresh = task
        defer {
            activeRefresh = nil
            if !refreshCleanupFailed { isRefreshing = false; isCancellingRefresh = false }
            if !refreshCleanupFailed, let pending = pendingRefresh {
                pendingRefresh = nil
                Task { [weak self] in _ = await self?.refreshWorktrees(retainedPaths: pending) }
            }
        }
        do {
            let discovered = try await task.value
            guard !task.isCancelled, !Task.isCancelled, generation == refreshGeneration,
                  self.projectRoot == projectRoot else { return false }
            let reconciliation = WorktreeReconciliation.reconcile(
                previous: worktrees,
                discovered: discovered,
                retainedPaths: retainedPaths,
                existingOrphanedPaths: orphanedWorktreePaths
            )
            worktrees = reconciliation.worktrees
            orphanedWorktreePaths = reconciliation.orphanedPaths
            return true
        } catch {
            if case .cleanupFailed = error as? GitWorktreeDiscoveryError {
                refreshCleanupFailed = true
                pendingRefresh = nil
                refreshError = .gitFailed(projectRoot, reason: "Owned Git cleanup could not be confirmed. This repository is blocked for this app session; inspect owned Git before quitting and reopening gabCode.")
                return false
            }
            guard !task.isCancelled, !Task.isCancelled, generation == refreshGeneration else { return false }
            refreshError = map(error, projectRoot: projectRoot)
            return false
        }
    }

    func createWorktree(
        request: WorktreeCreationRequest,
        selectedWorktreeBranch: String? = nil
    ) async -> Bool {
        guard let projectRoot, let workspaceBranch = mainBranch else {
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
            try await worktreeDiscovery.validateLocalBranch(branch, in: projectRoot)
            let discovered = try await worktreeDiscovery.worktrees(in: projectRoot)
            guard let primary = discovered.first(where: { $0.isPrimary }) else {
                throw WorkspaceOpenError.gitFailed(projectRoot, reason: "No registered primary worktree is available.")
            }
            try validateFolder(primary.path)
            let validation = await gitValidator.validate(folder: primary.path)
            try validateGitResult(validation, folder: primary.path)
            try WorkspaceDescriptor.write(name: name, projectRoot: projectRoot, mainBranch: branch, to: descriptorURL)
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
        catch let error as WorkspaceDescriptorError {
            throw WorkspaceOpenError.malformedDescriptor(url, reason: descriptorReason(error))
        }
        catch { throw WorkspaceOpenError.malformedDescriptor(url, reason: "The workspace file is malformed or unsupported.") }
    }

    private func descriptorReason(_ error: WorkspaceDescriptorError) -> String {
        switch error {
        case .malformed: "The workspace file contains malformed JSON."
        case let .unknownProperty(name): "The workspace file contains unknown property \"\(name)\"."
        case let .unsupportedVersion(version): "The workspace file uses unsupported version \(version)."
        case .missingVersion: "The workspace file is missing version."
        case .missingName: "The workspace file is missing name."
        case .emptyName: "The workspace file has an empty name."
        case .missingProject: "The workspace file is missing project."
        case let .unknownProjectProperty(name): "The workspace file contains unknown project property \"\(name)\"."
        case .missingProjectPath: "The workspace file is missing project.path."
        case .emptyProjectPath: "The workspace file has an empty project.path."
        case .missingMainBranch: "The workspace file is missing project.mainBranch."
        case .emptyMainBranch: "The workspace file has an empty project.mainBranch."
        case .destinationExists, .writeFailed: "The workspace file could not be written."
        }
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
            case let .cleanupFailed(url): return .gitFailed(url, reason: "Owned Git cleanup could not be confirmed. This repository is blocked for this app session; inspect owned Git before quitting and reopening gabCode.")
            case let .gitFailed(url, reason): return .gitFailed(url, reason: reason)
            }
        case let error as WorkspaceOpenError: return error
        default: return .gitFailed(projectRoot, reason: error.localizedDescription)
        }
    }
}
