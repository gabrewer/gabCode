import Darwin
import Foundation

// Ephemeral Git-derived presentation identities. Nothing here is persisted in the workspace.
enum ReviewGroup: String, Sendable { case staged, unstaged, commit, branch }
enum ReviewStatus: String, Sendable { case added, modified, deleted, renamed, untracked, conflicted, copied, changed }
enum ReviewDiffState: Sendable { case patch, conflict, binary, oversized, truncated, timeout, unavailable }

struct ReviewFile: Equatable, Sendable, Identifiable {
    let worktree: URL
    let group: ReviewGroup
    let status: ReviewStatus
    let path: String
    let oldPath: String?
    let commitSHA: String?
    let comparison: String

    var id: String { "\(worktree.standardizedFileURL.path):\(group.rawValue):\(commitSHA ?? ""):\(oldPath ?? "")→\(path)" }
}

struct ReviewCommit: Equatable, Sendable, Identifiable {
    let sha: String
    let subject: String
    let author: String
    let timestamp: Date
    let summary: String
    let parents: [String]
    var id: String { sha }
}

struct ReviewSnapshot: Sendable {
    let worktree: URL
    let head: String
    let main: String
    let working: [ReviewFile]
    let commits: [ReviewCommit]
    let hasMoreCommits: Bool
}

struct ReviewDiff: Sendable {
    let state: ReviewDiffState
    let text: String
}

struct ReviewTextFacts: Sendable, Equatable {
    let bom: Bool
    let eol: String
    let finalNewline: Bool
}

struct ReviewDocument: Sendable {
    let before: String
    let after: String
    let beforeFacts: ReviewTextFacts
    let afterFacts: ReviewTextFacts
    let beforeExists: Bool
    let afterExists: Bool
    let identity: String
    let comparison: String
    let rawFilter: Bool
}

enum ReviewHistoryPage: Sendable {
    case rows([ReviewCommit])
    case restart
}

enum ReviewGitError: Error, LocalizedError {
    case unavailable(String), failed(String), timeout, truncated, cancelled
    var errorDescription: String? {
        switch self {
        case let .unavailable(message), let .failed(message): message
        case .timeout: "Git review query timed out."
        case .truncated: "Git review output exceeded its safety limit."
        case .cancelled: "Git review query was cancelled."
        }
    }
}

/// Runs only read-only Git queries. A per-operation process is terminated on timeout/cancellation.
final class WorktreeChangeReviewService: @unchecked Sendable {
    let executable: URL
    let timeout: Duration
    let requestTimeout: Duration
    @TaskLocal static var requestDeadline: ContinuousClock.Instant?
    let outputLimit: Int
    let pageSize: Int

    nonisolated init(executable: URL = URL(fileURLWithPath: "/usr/bin/git"), timeout: Duration = .seconds(5), requestTimeout: Duration = .seconds(8), outputLimit: Int = 2 * 1024 * 1024, pageSize: Int = 100) {
        self.executable = executable
        self.timeout = timeout
        self.requestTimeout = requestTimeout
        self.outputLimit = outputLimit
        self.pageSize = max(1, min(pageSize, 500))
    }

    func snapshot(worktree: URL, mainBranch: String) async throws -> ReviewSnapshot {
        if Self.requestDeadline != nil { return try await snapshotWithinBudget(worktree: worktree, mainBranch: mainBranch) }
        return try await Self.$requestDeadline.withValue(ContinuousClock.now.advanced(by: requestTimeout)) {
            try await snapshotWithinBudget(worktree: worktree, mainBranch: mainBranch)
        }
    }

    private func snapshotWithinBudget(worktree: URL, mainBranch: String) async throws -> ReviewSnapshot {
        let root = worktree.standardizedFileURL
        guard !mainBranch.isEmpty, !mainBranch.contains("\0"), !mainBranch.hasPrefix("-") else {
            throw ReviewGitError.unavailable("Invalid configured main branch.")
        }
        let mainRef = "refs/heads/\(mainBranch)"
        let head = try await oid("HEAD", in: root)
        let main = try await oid(mainRef, in: root)
        let status = try await run(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no"], in: root)
        let working = try Self.parseStatus(status.data, worktree: root)
        guard working.count <= 512 else { throw ReviewGitError.unavailable("Too many changed files for bounded safe review.") }
        let allRows = try await queryCommits(worktree: root, head: head, main: main, skip: 0, count: pageSize + 1)
        return ReviewSnapshot(worktree: root, head: head, main: main, working: working, commits: Array(allRows.prefix(pageSize)), hasMoreCommits: allRows.count > pageSize)
    }

    func commits(worktree: URL, head: String, main: String, skip: Int = 0) async throws -> [ReviewCommit] {
        try await queryCommits(worktree: worktree, head: head, main: main, skip: skip, count: pageSize)
    }

    private func queryCommits(worktree: URL, head: String, main: String, skip: Int, count: Int) async throws -> [ReviewCommit] {
        guard skip >= 0, head.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil,
              main.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil else { throw ReviewGitError.unavailable("History references are unavailable.") }
        let result = try await run(["log", "--no-textconv", "--no-ext-diff", "--diff-merges=first-parent", "--topo-order", "--format=%x1e%H%x00%s%x00%an%x00%at%x00%P%x00", "--shortstat", "--skip=\(skip)", "-n", "\(count)", head, "^\(main)"], in: worktree)
        return try result.data.split(separator: 0x1e, omittingEmptySubsequences: true).map { record in
            let fields = record.split(separator: 0, omittingEmptySubsequences: false)
            guard fields.count >= 6, let seconds = Double(String(decoding: fields[3], as: UTF8.self)) else {
                throw ReviewGitError.failed("Malformed Git history record.")
            }
            let summary = String(decoding: fields[5], as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            return ReviewCommit(
                sha: String(decoding: fields[0], as: UTF8.self),
                subject: String(decoding: fields[1], as: UTF8.self),
                author: String(decoding: fields[2], as: UTF8.self),
                timestamp: Date(timeIntervalSince1970: seconds),
                summary: summary.isEmpty ? "No files changed" : summary,
                parents: String(decoding: fields[4], as: UTF8.self).split(separator: " ").map(String.init)
            )
        }
    }

    /// Re-read the current unique range until every previously visible identity is
    /// accounted for or the range ends. Merges can reorder the previous page's tail.
    /// Never infer membership from an old SHA or splice stale pages:
    /// main may have advanced, HEAD may have been rebased, and pagination uses an offset.
    func reconciledHistory(_ next: ReviewSnapshot, retaining previous: ReviewSnapshot, mainBranch: String) async throws -> ReviewSnapshot {
        if Self.requestDeadline != nil { return try await reconcileWithinBudget(next, retaining: previous, mainBranch: mainBranch) }
        return try await Self.$requestDeadline.withValue(ContinuousClock.now.advanced(by: requestTimeout)) {
            try await reconcileWithinBudget(next, retaining: previous, mainBranch: mainBranch)
        }
    }

    private func reconcileWithinBudget(_ next: ReviewSnapshot, retaining previous: ReviewSnapshot, mainBranch: String) async throws -> ReviewSnapshot {
        var rows = next.commits
        var hasMore = next.hasMoreCommits
        var remaining = Set(previous.commits.map(\.sha))
        remaining.subtract(rows.map(\.sha))
        // A bounded query failure leaves the last good snapshot marked stale rather
        // than incorrectly closing an open diff when history is unusually large.
        let limit = max(2_500, previous.commits.count + pageSize)
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while hasMore && (rows.count < previous.commits.count || !remaining.isEmpty) {
            try Task.checkCancellation()
            guard rows.count < limit, ContinuousClock.now < deadline else {
                throw ReviewGitError.unavailable("History changed beyond the review reconciliation limit. Refresh later or reopen the review.")
            }
            let page = try await queryCommits(worktree: next.worktree, head: next.head, main: next.main,
                                              skip: rows.count, count: min(pageSize, limit - rows.count) + 1)
            let appended = Array(page.prefix(min(pageSize, limit - rows.count)))
            guard !appended.isEmpty else { hasMore = false; break }
            rows.append(contentsOf: appended)
            remaining.subtract(appended.map(\.sha))
            hasMore = page.count > appended.count
        }
        let mainRef = "refs/heads/\(mainBranch)"
        guard (try await oid("HEAD", in: next.worktree)) == next.head,
              (try await oid(mainRef, in: next.worktree)) == next.main else {
            throw ReviewGitError.failed("Git history changed during review refresh. Refresh again.")
        }
        return ReviewSnapshot(worktree: next.worktree, head: next.head, main: next.main,
                              working: next.working, commits: rows, hasMoreCommits: hasMore)
    }

    func page(after snapshot: ReviewSnapshot, mainBranch: String, skip: Int) async throws -> ReviewHistoryPage {
        guard skip > 0, !mainBranch.isEmpty else { return .restart }
        let mainRef = "refs/heads/\(mainBranch)"
        guard (try await oid("HEAD", in: snapshot.worktree)) == snapshot.head,
              (try await oid(mainRef, in: snapshot.worktree)) == snapshot.main else { return .restart }
        let rows = try await commits(worktree: snapshot.worktree, head: snapshot.head, main: snapshot.main, skip: skip)
        guard (try await oid("HEAD", in: snapshot.worktree)) == snapshot.head,
              (try await oid(mainRef, in: snapshot.worktree)) == snapshot.main else { return .restart }
        return .rows(rows)
    }

    func files(in commit: ReviewCommit, worktree: URL) async throws -> [ReviewFile] {
        guard commit.sha.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil else {
            throw ReviewGitError.unavailable("Commit is unavailable.")
        }
        let basis = commit.parents.first
        let arguments = basis.map { ["diff", "--no-textconv", "--no-ext-diff", "--name-status", "-z", "--find-renames", $0, commit.sha, "--"] }
            ?? ["diff-tree", "--root", "--no-commit-id", "-r", "--no-textconv", "--no-ext-diff", "--name-status", "-z", commit.sha, "--"]
        let result = try await run(arguments, in: worktree)
        let tokens = try result.data.split(separator: 0, omittingEmptySubsequences: true).map { token -> String in
            guard let name = String(data: Data(token), encoding: .utf8) else { throw ReviewGitError.unavailable("Git returned an unsupported file path encoding.") }
            return name
        }
        var files: [ReviewFile] = []
        var i = 0
        while i < tokens.count {
            let flag = tokens[i]; i += 1
            guard i < tokens.count else { throw ReviewGitError.failed("Malformed Git file list.") }
            let first = tokens[i]; i += 1
            let rename = flag.hasPrefix("R") || flag.hasPrefix("C")
            guard !rename || i < tokens.count else { throw ReviewGitError.failed("Malformed Git rename.") }
            let path = rename ? tokens[i] : first
            if rename { i += 1 }
            files.append(ReviewFile(worktree: worktree.standardizedFileURL, group: .commit, status: Self.status(flag.first ?? "M"), path: path, oldPath: rename ? first : nil, commitSHA: commit.sha, comparison: basis.map { "\($0) → \(commit.sha)" } ?? "empty tree → \(commit.sha)"))
        }
        return files.sorted { $0.path < $1.path }
    }

    func diff(for file: ReviewFile, in worktree: URL) async throws -> ReviewDiff {
        let selected = worktree.standardizedFileURL
        guard file.worktree.standardizedFileURL == selected,
              !file.path.isEmpty, !file.path.hasPrefix("/"), !file.path.contains("\0"),
              !file.path.split(separator: "/").contains(".."),
              file.oldPath == nil || (!file.oldPath!.hasPrefix("/") && !file.oldPath!.split(separator: "/").contains(".."))
        else { throw ReviewGitError.unavailable("Review file is outside the selected worktree.") }
        if file.status == .conflicted { return ReviewDiff(state: .conflict, text: "Unmerged stages — resolve the conflict outside gabCode.") }
        if file.group == .unstaged && file.status != .untracked {
            if try await hasContentFilter(path: file.path, in: worktree) {
                return ReviewDiff(state: .unavailable, text: "A configured Git content filter prevents safe read-only review of this file.")
            }
        }
        let path = file.path
        let args: [String]
        switch file.group {
        case .staged: args = ["diff", "--cached", "--no-textconv", "--no-ext-diff", "--", ":(literal)\(file.oldPath ?? path)", ":(literal)\(path)"]
        case .unstaged:
            if file.status == .untracked {
                let empty = FileManager.default.temporaryDirectory.appendingPathComponent("gabcode-empty-\(UUID())")
                try Data().write(to: empty)
                defer { try? FileManager.default.removeItem(at: empty) }
                // git diff --no-index returns 1 when differences are present.
                return try await readPatch(["diff", "--no-index", "--no-textconv", "--no-ext-diff", "--", empty.path, path], in: worktree, allowDifference: true)
            }
            args = ["diff", "--no-textconv", "--no-ext-diff", "--", ":(literal)\(file.oldPath ?? path)", ":(literal)\(path)"]
        case .branch: throw ReviewGitError.unavailable("Branch review requires an exact snapshot.")
        case .commit:
            guard let sha = file.commitSHA else { throw ReviewGitError.unavailable("Commit is unavailable.") }
            let parentResult = try await run(["rev-list", "--parents", "-n", "1", sha], in: worktree)
            let parents = parentResult.text.split(whereSeparator: \.isWhitespace)
            if parents.count > 1 {
                args = ["diff", "--no-textconv", "--no-ext-diff", String(parents[1]), sha, "--", ":(literal)\(file.oldPath ?? path)", ":(literal)\(path)"]
            } else {
                args = ["diff-tree", "--root", "--no-commit-id", "-p", "-r", "--no-textconv", "--no-ext-diff", sha, "--", ":(literal)\(file.oldPath ?? path)", ":(literal)\(path)"]
            }
        }
        return try await readPatch(args, in: worktree)
    }

    /// Captures both sides from Git objects/index and bounded on-disk bytes. No diff driver,
    /// smudge/clean/textconv, shell, or worktree substitution is involved.
    func document(for file: ReviewFile, in worktree: URL, mainBranch: String) async throws -> ReviewDocument {
        if Self.requestDeadline != nil { return try await documentWithinBudget(for: file, in: worktree, mainBranch: mainBranch) }
        return try await Self.$requestDeadline.withValue(ContinuousClock.now.advanced(by: .seconds(12))) {
            try await documentWithinBudget(for: file, in: worktree, mainBranch: mainBranch)
        }
    }

    private func documentWithinBudget(for file: ReviewFile, in worktree: URL, mainBranch: String) async throws -> ReviewDocument {
        let root = worktree.standardizedFileURL
        guard root == file.worktree.standardizedFileURL,
              Self.safePath(file.path), file.oldPath.map(Self.safePath) ?? true,
              file.status != .conflicted else { throw ReviewGitError.unavailable("Unsafe or conflicted file cannot be compared as text.") }
        let needsHead = file.group != .commit
        let needsIndex = file.group == .staged || file.group == .unstaged
        let head: String? = needsHead ? try await oid("HEAD", in: root) : nil
        let previousIndex: (String, String)? = needsIndex ? try await indexEntry(file.path, in: root) : nil
        if file.group == .unstaged && file.status == .untracked && previousIndex != nil {
            throw ReviewGitError.unavailable("Untracked file entered the index. Refresh review.")
        }
        let startMain: String? = file.group == .branch ? try await oid("refs/heads/\(mainBranch)", in: root) : nil
        let firstParent: String?
        if file.group == .commit {
            guard let sha = file.commitSHA, Self.isOID(sha) else { throw ReviewGitError.unavailable("Historical commit is unavailable.") }
            let output = try await run(["rev-list", "--parents", "-n", "1", sha], in: root)
            let rows = output.text.split(whereSeparator: \.isWhitespace).map(String.init)
            guard rows.first == sha else { throw ReviewGitError.unavailable("Historical commit changed.") }
            firstParent = rows.dropFirst().first
        } else { firstParent = nil }
        let target: String
        let basis: String?
        switch file.group {
        case .staged, .unstaged: target = head!; basis = head
        case .commit: target = file.commitSHA!; basis = firstParent
        case .branch:
            target = head!
            let result = try await run(["merge-base", "--all", startMain!, target], in: root)
            let bases = result.text.split(whereSeparator: \.isWhitespace).map(String.init)
            guard bases.count == 1, Self.isOID(bases[0]) else { throw ReviewGitError.unavailable("Branch comparison has no unique merge base.") }
            basis = bases[0]
            guard file.commitSHA == nil || file.commitSHA == target else { throw ReviewGitError.unavailable("Branch selection changed. Refresh review.") }
        }
        let oldPath = file.oldPath ?? file.path
        let beforeEntry: (String, String)?
        let afterEntry: (String, String)?
        switch file.group {
        case .staged: beforeEntry = try await treeEntry(oldPath, at: basis, in: root); afterEntry = previousIndex
        case .unstaged:
            beforeEntry = file.status == .untracked ? nil : previousIndex
            afterEntry = nil
        case .commit, .branch:
            beforeEntry = try await treeEntry(oldPath, at: basis, in: root)
            afterEntry = try await treeEntry(file.path, at: target, in: root)
        }
        let beforeBytes: Data
        if let beforeEntry { beforeBytes = try await blob(beforeEntry, in: root) } else { beforeBytes = Data() }
        let afterBytes: Data
        if file.group == .unstaged && file.status != .deleted {
            afterBytes = try Self.readWorktree(path: file.path, root: root)
        } else if file.group == .unstaged {
            try Self.confirmAbsent(path: file.path, root: root)
            afterBytes = Data()
        } else if let afterEntry { afterBytes = try await blob(afterEntry, in: root) }
        else { afterBytes = Data() }
        let before = try Self.decode(beforeBytes)
        let after = try Self.decode(afterBytes)
        let endHead: String? = needsHead ? try await oid("HEAD", in: root) : nil
        let endIndex: (String, String)? = needsIndex ? try await indexEntry(file.path, in: root) : nil
        let endMain: String? = startMain != nil ? try await oid("refs/heads/\(mainBranch)", in: root) : nil
        guard endHead == head, endIndex?.0 == previousIndex?.0, endIndex?.1 == previousIndex?.1,
              endMain == startMain else {
            throw ReviewGitError.unavailable("Comparison changed during capture. Retry.")
        }
        if file.group == .unstaged && file.status != .deleted {
            guard try Self.readWorktree(path: file.path, root: root) == afterBytes else {
                throw ReviewGitError.unavailable("Worktree file changed during capture. Retry.")
            }
        } else if file.group == .unstaged { try Self.confirmAbsent(path: file.path, root: root) }
        let rawFilter = try await hasContentFilter(path: file.path, in: root)
        let label: String
        switch file.group {
        case .staged: label = "HEAD → index"
        case .unstaged: label = file.status == .untracked ? "empty → worktree" : "index → worktree"
        case .commit: label = basis.map { "\($0) → \(target)" } ?? "empty tree → \(target)"
        case .branch: label = "\(basis!) → \(target)"
        }
        return ReviewDocument(before: before.0, after: after.0, beforeFacts: before.1, afterFacts: after.1,
                              beforeExists: beforeEntry != nil, afterExists: file.group == .unstaged ? file.status != .deleted : afterEntry != nil,
                              identity: "\(file.id):\(basis ?? "empty"):\(target):\(beforeEntry?.1 ?? "empty"): \(afterEntry?.1 ?? "empty")",
                              comparison: label, rawFilter: rawFilter)
    }

    private static func safePath(_ path: String) -> Bool {
        !path.isEmpty && !path.hasPrefix("/") && !path.contains("\\") && !path.contains("\0") &&
        !path.split(separator: "/").contains("..") && !path.split(separator: "/").contains(".")
    }
    private static func isOID(_ value: String) -> Bool {
        value.range(of: "^[a-f0-9]{40,64}$", options: .regularExpression) != nil
    }

    private func treeEntry(_ path: String, at revision: String?, in root: URL) async throws -> (String, String)? {
        guard let revision else { return nil }
        let output = try await run(["ls-tree", "--full-tree", "-z", revision, "--", ":(literal)\(path)"], in: root)
        let records = output.data.split(separator: 0, omittingEmptySubsequences: true)
        guard records.count <= 1 else { throw ReviewGitError.unavailable("Ambiguous Git tree entry.") }
        guard let record = records.first else { return nil }
        let parts = record.split(separator: 9, maxSplits: 1)
        guard parts.count == 2, String(data: Data(parts[1]), encoding: .utf8) == path else {
            throw ReviewGitError.unavailable("Git tree entry does not match selected path.")
        }
        let header = String(decoding: parts[0], as: UTF8.self).split(separator: " ")
        guard header.count == 3, header[1] == "blob", ["100644", "100755"].contains(String(header[0])), Self.isOID(String(header[2])) else {
            throw ReviewGitError.unavailable("Unsupported Git file kind.")
        }
        return (String(header[0]), String(header[2]))
    }

    private func indexEntry(_ path: String, in root: URL) async throws -> (String, String)? {
        let output = try await run(["ls-files", "--stage", "-z", "--", ":(literal)\(path)"], in: root)
        let records = output.data.split(separator: 0, omittingEmptySubsequences: true)
        guard records.count <= 1 else { throw ReviewGitError.unavailable("Unmerged or ambiguous index entry.") }
        guard let record = records.first else { return nil }
        let parts = record.split(separator: 9, maxSplits: 1)
        let header = parts.first.map { String(decoding: $0, as: UTF8.self).split(separator: " ") } ?? []
        guard parts.count == 2, String(data: Data(parts[1]), encoding: .utf8) == path,
              header.count == 3, header[2] == "0", ["100644", "100755"].contains(String(header[0])),
              Self.isOID(String(header[1])) else { throw ReviewGitError.unavailable("Unsupported index file kind.") }
        return (String(header[0]), String(header[1]))
    }

    private func blob(_ entry: (String, String), in root: URL) async throws -> Data {
        let result = try await run(["cat-file", "blob", entry.1], in: root)
        guard result.data.count <= 1_000_000 else { throw ReviewGitError.unavailable("Diff unavailable: file exceeds text size limit.") }
        return result.data
    }

    private static func decode(_ bytes: Data) throws -> (String, ReviewTextFacts) {
        guard bytes.count <= 1_000_000 else { throw ReviewGitError.unavailable("Diff unavailable: file exceeds text size limit.") }
        let data: Data
        let bom = bytes.starts(with: [0xef, 0xbb, 0xbf])
        if bom { data = Data(bytes.dropFirst(3)) } else { data = bytes }
        if bytes.starts(with: [0xff, 0xfe]) || bytes.starts(with: [0xfe, 0xff]) ||
            bytes.starts(with: [0, 0, 0xfe, 0xff]) || bytes.starts(with: [0xff, 0xfe, 0, 0]) {
            throw ReviewGitError.unavailable("Diff unavailable: unsupported text encoding.")
        }
        guard !data.contains(0), let text = String(data: data, encoding: .utf8) else {
            throw ReviewGitError.unavailable(data.contains(0) ? "Binary content cannot be reviewed as text." : "Diff unavailable: unsupported text encoding.")
        }
        let lf = data.filter { $0 == 10 }.count
        let cr = data.filter { $0 == 13 }.count
        let crlf = zip(data, data.dropFirst()).filter { $0 == 13 && $1 == 10 }.count
        let eol = lf == 0 && cr == 0 ? "none" : crlf == lf && crlf == cr ? "CRLF" : lf > 0 && cr == 0 ? "LF" : cr > 0 && lf == 0 ? "CR" : "mixed"
        return (text, ReviewTextFacts(bom: bom, eol: eol, finalNewline: data.last == 10 || data.last == 13))
    }

    private static func confirmAbsent(path: String, root: URL) throws {
        let base = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard base >= 0 else { throw ReviewGitError.unavailable("Worktree is unavailable.") }
        defer { close(base) }
        var parent = base
        defer { if parent != base { close(parent) } }
        let components = path.split(separator: "/").map(String.init)
        for component in components.dropLast() {
            let next = openat(parent, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard next >= 0 else { throw ReviewGitError.unavailable("Unsafe worktree path.") }
            if parent != base { close(parent) }
            parent = next
        }
        var info = stat()
        if fstatat(parent, components.last!, &info, AT_SYMLINK_NOFOLLOW) == 0 || errno != ENOENT {
            throw ReviewGitError.unavailable("Deleted file changed or cannot be verified absent.")
        }
    }

    private static func readWorktree(path: String, root: URL) throws -> Data {
        let rootFD = open(root.path, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
        guard rootFD >= 0 else { throw ReviewGitError.unavailable("Worktree is unavailable.") }
        defer { close(rootFD) }
        let components = path.split(separator: "/").map(String.init)
        var parent = rootFD
        defer { if parent != rootFD { close(parent) } }
        for component in components.dropLast() {
            let next = openat(parent, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard next >= 0 else { throw ReviewGitError.unavailable("Unsafe worktree path.") }
            if parent != rootFD { close(parent) }
            parent = next
        }
        let fd = openat(parent, components.last!, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw ReviewGitError.unavailable("Selected worktree file is unavailable.") }
        defer { close(fd) }
        var initial = stat(), finish = stat()
        var rootPath = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        var resolved = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(rootFD, F_GETPATH, &rootPath) == 0,
              fcntl(fd, F_GETPATH, &resolved) == 0,
              String(cString: resolved).hasPrefix(String(cString: rootPath) + "/"),
              fstat(fd, &initial) == 0, initial.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              initial.st_size <= 1_000_000, initial.st_size >= 0 else {
            throw ReviewGitError.unavailable("Unsupported or oversized worktree file.")
        }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 8192)
        while true {
            let count = read(fd, &buffer, buffer.count)
            guard count >= 0 else { throw ReviewGitError.unavailable("Worktree file read failed.") }
            if count == 0 { break }
            guard data.count + count <= 1_000_000 else { throw ReviewGitError.unavailable("Diff unavailable: file exceeds text size limit.") }
            data.append(contentsOf: buffer.prefix(count))
        }
        resolved = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard fcntl(fd, F_GETPATH, &resolved) == 0,
              String(cString: resolved).hasPrefix(String(cString: rootPath) + "/"),
              fstat(fd, &finish) == 0, initial.st_dev == finish.st_dev, initial.st_ino == finish.st_ino,
              initial.st_size == finish.st_size,
              initial.st_mtimespec.tv_sec == finish.st_mtimespec.tv_sec,
              initial.st_mtimespec.tv_nsec == finish.st_mtimespec.tv_nsec,
              initial.st_ctimespec.tv_sec == finish.st_ctimespec.tv_sec,
              initial.st_ctimespec.tv_nsec == finish.st_ctimespec.tv_nsec,
              data.count == Int(initial.st_size) else {
            throw ReviewGitError.unavailable("Worktree file changed during capture.")
        }
        return data
    }

    private func readPatch(_ args: [String], in root: URL, allowDifference: Bool = false) async throws -> ReviewDiff {
        do {
            let output = try await run(args, in: root, accepted: allowDifference ? [0, 1] : [0])
            if allowDifference && !output.text.hasPrefix("diff --git ") { return ReviewDiff(state: .unavailable, text: "Git could not read the current file.") }
            // Git's binary markers are whole metadata lines. Hunk text is prefixed
            // with +, -, or space, so marker-like file content stays readable.
            let binary = output.text.split(separator: "\n", omittingEmptySubsequences: false).contains { line in
                line == "GIT binary patch" || line.hasPrefix("Binary files ") && line.hasSuffix(" differ")
            }
            if binary { return ReviewDiff(state: .binary, text: "Binary file — no text diff available.") }
            if output.data.count > min(outputLimit, 1024 * 1024) { return ReviewDiff(state: .oversized, text: "Diff exceeds the document size limit.") }
            return ReviewDiff(state: .patch, text: output.text)
        } catch ReviewGitError.truncated { return ReviewDiff(state: .truncated, text: "Diff exceeds the output limit.") }
        catch ReviewGitError.timeout { return ReviewDiff(state: .timeout, text: "Diff timed out.") }
        catch let error as ReviewGitError {
            if case .cancelled = error { throw error }
            return ReviewDiff(state: .unavailable, text: error.localizedDescription)
        }
    }

    private func hasContentFilter(path: String, in root: URL) async throws -> Bool {
        let attributes = try await run(["check-attr", "-z", "filter", "--", path], in: root)
        let values = attributes.data.split(separator: 0, omittingEmptySubsequences: true)
        guard values.count >= 3 else { throw ReviewGitError.failed("Malformed Git attributes result.") }
        return !["unspecified", "unset"].contains(String(decoding: values[2], as: UTF8.self))
    }

    private func oid(_ ref: String, in root: URL) async throws -> String {
        let output: String
        do {
            output = try await run(["rev-parse", "--verify", "--end-of-options", ref], in: root).text.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch let error as ReviewGitError {
            if case .failed = error { throw ReviewGitError.unavailable("Reference \(ref) is unavailable.") }
            throw error
        }
        guard output.range(of: "^[0-9a-f]{40,64}$", options: .regularExpression) != nil else { throw ReviewGitError.unavailable("Reference \(ref) is unavailable.") }
        return output
    }

    private static func status(_ flag: Character) -> ReviewStatus {
        switch flag { case "A": .added; case "D": .deleted; case "R": .renamed; case "C": .copied; case "?": .untracked; default: .modified }
    }

    static func parseStatus(_ data: Data, worktree: URL) throws -> [ReviewFile] {
        let tokens = data.split(separator: 0, omittingEmptySubsequences: true)
        var rows: [ReviewFile] = []
        var i = 0
        while i < tokens.count {
            let entry = tokens[i]; i += 1
            guard entry.count >= 4, entry[entry.startIndex.advanced(by: 2)] == 32 else { throw ReviewGitError.failed("Malformed Git status record.") }
            let x = Character(UnicodeScalar(entry[entry.startIndex]))
            let y = Character(UnicodeScalar(entry[entry.startIndex.advanced(by: 1)]))
            guard let path = String(data: Data(entry.dropFirst(3)), encoding: .utf8), !path.isEmpty else {
                throw ReviewGitError.unavailable("Git returned an unsupported file path encoding.")
            }
            if x == "!" { continue }
            let rename = x == "R" || x == "C" || y == "R" || y == "C"
            guard !rename || i < tokens.count else { throw ReviewGitError.failed("Malformed Git rename record.") }
            // Porcelain v1 -z emits new path first, old path second.
            let old: String?
            if rename {
                guard let name = String(data: Data(tokens[i]), encoding: .utf8), !name.isEmpty else {
                    throw ReviewGitError.unavailable("Git returned an unsupported rename path encoding.")
                }
                old = name
                i += 1
            } else { old = nil }
            let conflict = x == "U" || y == "U" || (x == "A" && y == "A") || (x == "D" && y == "D")
            if conflict {
                rows.append(ReviewFile(worktree: worktree, group: .unstaged, status: .conflicted, path: path, oldPath: old, commitSHA: nil, comparison: "unmerged stages"))
            } else if x == "?" {
                rows.append(ReviewFile(worktree: worktree, group: .unstaged, status: .untracked, path: path, oldPath: nil, commitSHA: nil, comparison: "empty → worktree"))
            } else {
                if x != " " { rows.append(ReviewFile(worktree: worktree, group: .staged, status: status(x), path: path, oldPath: x == "R" || x == "C" ? old : nil, commitSHA: nil, comparison: "HEAD → index")) }
                if y != " " { rows.append(ReviewFile(worktree: worktree, group: .unstaged, status: status(y), path: path, oldPath: y == "R" || y == "C" ? old : nil, commitSHA: nil, comparison: "index → worktree")) }
            }
            guard rows.count <= 512 else {
                throw ReviewGitError.unavailable("Too many changed files for bounded safe review.")
            }
        }
        rows.sort { a, b in a.group == b.group ? a.path < b.path : a.group == .staged }
        return rows
    }

    struct GitOutput { let data: Data; var text: String { String(decoding: data, as: UTF8.self) } }

    private func run(_ args: [String], in root: URL, accepted: Set<Int32> = [0]) async throws -> GitOutput {
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw ReviewGitError.unavailable("Git executable is unavailable.") }
        let base = ["-C", root.path, "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "gc.auto=0"]
        let configOperation = ReviewGitOperation(
            executable: executable,
            arguments: base + ["config", "--null", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|process|smudge)$"],
            root: root,
            limit: 64 * 1024
        )
        let configOutput = try await execute(configOperation, accepted: [0, 1])
        let names = configOutput.data.split(separator: 0, omittingEmptySubsequences: true).compactMap { String(data: Data($0), encoding: .utf8) }
        guard names.count <= 128 else { throw ReviewGitError.unavailable("Too many configured Git content filters for safe review.") }
        let sanitized = names.flatMap { ["-c", "\($0)="] }
        let operation = ReviewGitOperation(executable: executable, arguments: base + sanitized + args, root: root, limit: outputLimit)
        return try await execute(operation, accepted: accepted)
    }

    private func execute(_ operation: ReviewGitOperation, accepted: Set<Int32>) async throws -> GitOutput {
        let budget: Duration
        if let deadline = Self.requestDeadline {
            budget = ContinuousClock.now.duration(to: deadline)
            guard budget > .zero else { throw ReviewGitError.timeout }
        } else { budget = timeout }
        return try await withTaskCancellationHandler {
            let operationTask = Task.detached(priority: .utility) { try operation.execute(accepted: accepted) }
            return try await withThrowingTaskGroup(of: GitOutput.self) { group in
                group.addTask { try await operationTask.value }
                group.addTask {
                    try await Task.sleep(for: min(self.timeout, budget))
                    operation.cancel()
                    throw ReviewGitError.timeout
                }
                defer { group.cancelAll() }
                do { return try await group.next()! }
                catch is CancellationError { operation.cancel(); throw ReviewGitError.cancelled }
                catch { operation.cancel(); throw error }
            }
        } onCancel: { operation.cancel() }
    }
}

private final class ReviewGitOperation: @unchecked Sendable {
    let process = Process()
    let limit: Int
    private let lock = NSLock()
    private nonisolated(unsafe) var cancelled = false
    nonisolated init(executable: URL, arguments: [String], root: URL, limit: Int) {
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = root
        // -C alone does not override GIT_DIR, GIT_WORK_TREE, or GIT_CONFIG_* from the parent shell.
        process.environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        self.limit = limit
    }
    nonisolated func cancel() {
        lock.lock()
        defer { lock.unlock() }
        guard !cancelled else { return }
        cancelled = true
        guard process.isRunning else { return }
        process.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + .milliseconds(250)) { [self] in
            lock.lock()
            defer { lock.unlock() }
            if process.isRunning { _ = Darwin.kill(process.processIdentifier, SIGKILL) }
        }
    }
    nonisolated func execute(accepted: Set<Int32>) throws -> WorktreeChangeReviewService.GitOutput {
        lock.lock()
        if cancelled { lock.unlock(); throw ReviewGitError.cancelled }
        let out = Pipe(), err = Pipe()
        process.standardOutput = out; process.standardError = err
        do { try process.run() } catch { lock.unlock(); throw ReviewGitError.unavailable(error.localizedDescription) }
        lock.unlock()
        let readers = DispatchGroup()
        let outputBox = CaptureBox()
        let errorBox = CaptureBox()
        readers.enter()
        DispatchQueue.global(qos: .utility).async {
            outputBox.value = Self.capture(out.fileHandleForReading, limit: self.limit)
            readers.leave()
        }
        readers.enter()
        DispatchQueue.global(qos: .utility).async {
            errorBox.value = Self.capture(err.fileHandleForReading, limit: 64 * 1024)
            readers.leave()
        }
        process.waitUntilExit()
        readers.wait()
        let output = outputBox.value, error = errorBox.value
        lock.lock(); let wasCancelled = cancelled; lock.unlock()
        if wasCancelled { throw ReviewGitError.cancelled }
        if output.1 { throw ReviewGitError.truncated }
        if !accepted.contains(process.terminationStatus) { throw ReviewGitError.failed(String(decoding: error.0, as: UTF8.self)) }
        return WorktreeChangeReviewService.GitOutput(data: output.0)
    }
    nonisolated private static func capture(_ handle: FileHandle, limit: Int) -> (Data, Bool) {
        var data = Data(), overflow = false
        while true {
            let chunk = handle.readData(ofLength: 8192)
            if chunk.isEmpty { break }
            if data.count + chunk.count > limit { overflow = true }
            if data.count < limit { data.append(chunk.prefix(limit - data.count)) }
        }
        return (data, overflow)
    }
    private final class CaptureBox: @unchecked Sendable {
        nonisolated init() {}
        nonisolated(unsafe) var value: (Data, Bool) = (Data(), false)
    }
}
