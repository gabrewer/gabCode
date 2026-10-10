import Combine
import Foundation

/// Window-local, selected-worktree presentation. Git remains the source of truth.
@MainActor
final class WorktreeChangeReviewCoordinator: ObservableObject {
    @Published private(set) var selectedWorktree: URL?
    @Published private(set) var snapshot: ReviewSnapshot?
    @Published private(set) var isLoading = false
    @Published private(set) var isStale = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var notice: String?
    @Published private(set) var observerWarning: String?
    @Published private(set) var expandedCommits: Set<String> = []
    @Published private(set) var commitFiles: [String: [ReviewFile]] = [:]
    @Published private(set) var selectedFile: ReviewFile?
    @Published private(set) var isLoadingMore = false

    private let service: WorktreeChangeReviewService
    private let snapshotLoader: @Sendable (URL, String) async throws -> ReviewSnapshot
    private var selectedMainBranch: String?
    private var generation = 0
    private var refreshTask: Task<Void, Never>?
    private var debounceTask: Task<Void, Never>?
    private var periodicTask: Task<Void, Never>?
    private var fileListTasks: [String: Task<Void, Never>] = [:]
    private var pageTask: Task<Void, Never>?
    private var observer: WorktreeReviewObserver?
    private weak var selectedPresentation: TerminalWorkspacePresentation?

    init(
        service: WorktreeChangeReviewService = WorktreeChangeReviewService(),
        snapshotLoader: (@Sendable (URL, String) async throws -> ReviewSnapshot)? = nil
    ) {
        self.service = service
        self.snapshotLoader = snapshotLoader ?? { path, branch in try await service.snapshot(worktree: path, mainBranch: branch) }
    }

    func activate(worktree: URL, mainBranch: String) {
        let path = worktree.standardizedFileURL
        guard selectedWorktree != path || selectedMainBranch != mainBranch else { return }
        cancelActiveWork()
        selectedPresentation?.closeReviewDocument(restoreFocus: false)
        selectedPresentation = nil
        selectedFile = nil
        snapshot = nil
        isStale = false
        errorMessage = nil
        notice = nil
        observerWarning = nil
        expandedCommits = []
        commitFiles = [:]
        selectedWorktree = path
        selectedMainBranch = mainBranch
    }

    func deactivate() {
        cancelActiveWork()
        selectedPresentation?.closeReviewDocument(restoreFocus: false)
        selectedPresentation = nil
        selectedWorktree = nil
        selectedMainBranch = nil
        selectedFile = nil
        snapshot = nil
        expandedCommits = []
        commitFiles = [:]
        notice = nil
        errorMessage = nil
        observerWarning = nil
        isStale = false
        isLoading = false
    }

    func refresh() async {
        guard let path = selectedWorktree, let branch = selectedMainBranch else { return }
        generation += 1
        let requestedGeneration = generation
        pageTask?.cancel()
        pageTask = nil
        isLoadingMore = false
        isLoading = true
        defer { if generation == requestedGeneration { isLoading = false } }
        do {
            let next = try await snapshotLoader(path, branch)
            guard !Task.isCancelled, generation == requestedGeneration, selectedWorktree == path else { return }
            let previous = snapshot
            let sameHistory = previous?.worktree == next.worktree && previous?.head == next.head && previous?.main == next.main
            // A fresh snapshot reads only the first page. Preserve previously loaded pages
            // while the Git refs and the first-page prefix still identify the same history.
            let keepPages = sameHistory && !next.commits.isEmpty &&
                (previous?.commits.count ?? 0) > next.commits.count &&
                Array(previous!.commits.prefix(next.commits.count)).map(\.sha) == next.commits.map(\.sha)
            let reconciled: ReviewSnapshot
            if keepPages {
                reconciled = ReviewSnapshot(worktree: next.worktree, head: next.head, main: next.main,
                                            working: next.working, commits: previous!.commits,
                                            hasMoreCommits: previous!.hasMoreCommits)
            } else if let previous, previous.worktree == next.worktree,
                      !previous.commits.isEmpty, next.hasMoreCommits,
                      !sameHistory || previous.commits.count > next.commits.count {
                reconciled = try await service.reconciledHistory(next, retaining: previous, mainBranch: branch)
            } else {
                reconciled = next
            }
            guard !Task.isCancelled, generation == requestedGeneration, selectedWorktree == path else { return }
            let nextSHAs = Set(reconciled.commits.map(\.sha))
            if !sameHistory || !keepPages && (previous?.commits.count ?? 0) > next.commits.count {
                commitFiles = commitFiles.filter { nextSHAs.contains($0.key) }
                fileListTasks.values.forEach { $0.cancel() }
                fileListTasks.removeAll()
            }
            expandedCommits.formIntersection(nextSHAs)
            if let selectedFile {
                if !isValid(selectedFile, in: reconciled) {
                    selectedPresentation?.closeReviewDocument(restoreFocus: true)
                    self.selectedFile = nil
                    notice = "The selected diff is no longer available after Git changed."
                } else if selectedFile.group != .commit || selectedPresentation?.reviewDocument.hasLoadedContent != true {
                    // Historical patches are immutable, but a failed/unavailable load is retryable.
                    // Working-file diffs can change without their row identity changing.
                    selectedPresentation?.reviewDocument.reload()
                }
            }
            snapshot = reconciled
            for commit in reconciled.commits where expandedCommits.contains(commit.sha) && commitFiles[commit.sha] == nil {
                loadFiles(for: commit)
            }
            isStale = false
            errorMessage = nil
        } catch {
            guard !Task.isCancelled, generation == requestedGeneration, selectedWorktree == path else { return }
            isStale = true
            errorMessage = error.localizedDescription
        }
    }

    func requestRefresh() {
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in await self?.refresh() }
    }

    func invalidate(overflow: Bool = false) {
        if overflow {
            isStale = true
            errorMessage = "Filesystem observation missed events. Refreshing from Git."
        }
        debounceTask?.cancel()
        debounceTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled else { return }
            self?.requestRefresh()
        }
    }

    func open(file: ReviewFile, in presentation: TerminalWorkspacePresentation) {
        guard file.worktree.standardizedFileURL == selectedWorktree,
              let snapshot, isValid(file, in: snapshot) else {
            notice = "The selected file changed in Git. Refresh review and try again."
            return
        }
        if let selectedPresentation, selectedPresentation !== presentation {
            selectedPresentation.closeReviewDocument(restoreFocus: false)
        }
        selectedPresentation = presentation
        selectedFile = file
        notice = nil
        presentation.openReviewDocument(file)
    }

    func reportUnavailableSelection() {
        notice = "The selected worktree is unavailable. Select a recognized worktree and retry."
    }

    func closeDocument() {
        selectedPresentation?.closeReviewDocument()
        selectedFile = nil
    }

    func toggleCommit(_ sha: String) {
        guard snapshot?.commits.contains(where: { $0.sha == sha }) == true else { return }
        if expandedCommits.contains(sha) { expandedCommits.remove(sha); return }
        expandedCommits.insert(sha)
        guard let commit = snapshot?.commits.first(where: { $0.sha == sha }) else { return }
        loadFiles(for: commit)
    }

    private func loadFiles(for commit: ReviewCommit) {
        guard commitFiles[commit.sha] == nil, let path = selectedWorktree else { return }
        let sha = commit.sha
        let expectedGeneration = generation
        fileListTasks[sha]?.cancel()
        fileListTasks[sha] = Task { [weak self, service] in
            do {
                let files = try await service.files(in: commit, worktree: path)
                guard let self, !Task.isCancelled, self.generation == expectedGeneration,
                      self.selectedWorktree == path else { return }
                self.commitFiles[sha] = files
            } catch {
                guard let self, !Task.isCancelled, self.generation == expectedGeneration else { return }
                self.errorMessage = error.localizedDescription
            }
            if self?.generation == expectedGeneration { self?.fileListTasks.removeValue(forKey: sha) }
        }
    }

    func loadMore() {
        guard let snapshot, snapshot.hasMoreCommits, !isLoadingMore,
              let branch = selectedMainBranch else { return }
        let expectedGeneration = generation
        isLoadingMore = true
        pageTask?.cancel()
        pageTask = Task { [weak self, service] in
            defer {
                if self?.generation == expectedGeneration { self?.isLoadingMore = false }
            }
            do {
                switch try await service.page(after: snapshot, mainBranch: branch, skip: snapshot.commits.count) {
                case .restart:
                    guard let self, !Task.isCancelled, self.generation == expectedGeneration,
                          self.selectedWorktree == snapshot.worktree else { return }
                    self.requestRefresh()
                case let .rows(rows):
                    guard let self, !Task.isCancelled, self.generation == expectedGeneration,
                          self.selectedWorktree == snapshot.worktree else { return }
                    self.snapshot = ReviewSnapshot(worktree: snapshot.worktree, head: snapshot.head, main: snapshot.main,
                        working: snapshot.working, commits: snapshot.commits + rows, hasMoreCommits: rows.count == service.pageSize)
                }
            } catch {
                guard let self, !Task.isCancelled, self.generation == expectedGeneration else { return }
                self.errorMessage = error.localizedDescription
            }
        }
    }

    func observeSelectedWorktree() {
        observer?.stop()
        observer = nil
        periodicTask?.cancel()
        guard let path = selectedWorktree else { return }
        observer = WorktreeReviewObserver(worktree: path) { [weak self] overflow in
            Task { @MainActor [weak self] in
                guard let self, self.selectedWorktree == path else { return }
                self.invalidate(overflow: overflow)
            }
        }
        if observer?.start() == false {
            observerWarning = "Filesystem observation is unavailable. Use Refresh; periodic Git reconciliation remains active."
        } else {
            observerWarning = nil
        }
        periodicTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard !Task.isCancelled else { break }
                self?.requestRefresh()
            }
        }
    }

    private func isValid(_ file: ReviewFile, in next: ReviewSnapshot) -> Bool {
        guard file.worktree.standardizedFileURL == next.worktree.standardizedFileURL else { return false }
        if file.group == .commit {
            return next.commits.contains(where: { $0.sha == file.commitSHA }) &&
                commitFiles[file.commitSHA ?? ""]?.contains(where: { $0.id == file.id }) == true
        }
        return next.working.contains(file)
    }

    private func cancelActiveWork() {
        generation += 1
        refreshTask?.cancel(); refreshTask = nil
        debounceTask?.cancel(); debounceTask = nil
        periodicTask?.cancel(); periodicTask = nil
        pageTask?.cancel(); pageTask = nil
        isLoadingMore = false
        isLoading = false
        fileListTasks.values.forEach { $0.cancel() }
        fileListTasks.removeAll()
        observer?.stop(); observer = nil
    }

    deinit {
        // WorktreeReviewObserver stops its FSEvents stream when released; tasks cancel in the owner lifecycle.
        refreshTask?.cancel()
        debounceTask?.cancel()
        periodicTask?.cancel()
        pageTask?.cancel()
        fileListTasks.values.forEach { $0.cancel() }
        observer?.stop()
    }
}
