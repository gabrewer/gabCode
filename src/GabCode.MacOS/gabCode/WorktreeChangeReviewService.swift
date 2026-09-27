import Darwin
import Foundation

// Ephemeral Git-derived presentation identities. Nothing here is persisted in the workspace.
enum ReviewGroup: String, Sendable { case staged, unstaged, commit }
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
    let outputLimit: Int
    let pageSize: Int

    nonisolated init(executable: URL = URL(fileURLWithPath: "/usr/bin/git"), timeout: Duration = .seconds(5), outputLimit: Int = 2 * 1024 * 1024, pageSize: Int = 100) {
        self.executable = executable
        self.timeout = timeout
        self.outputLimit = outputLimit
        self.pageSize = max(1, min(pageSize, 500))
    }

    func snapshot(worktree: URL, mainBranch: String) async throws -> ReviewSnapshot {
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
        for file in working where file.group == .unstaged && file.status != .untracked && file.status != .conflicted {
            if try await hasContentFilter(path: file.path, in: root) {
                throw ReviewGitError.unavailable("A configured Git content filter prevents safe read-only review of \(file.path).")
            }
        }
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
        let basis = commit.parents.first ?? "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
        let result = try await run(["diff", "--no-textconv", "--no-ext-diff", "--name-status", "-z", "--find-renames", basis, commit.sha, "--"], in: worktree)
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
            files.append(ReviewFile(worktree: worktree.standardizedFileURL, group: .commit, status: Self.status(flag.first ?? "M"), path: path, oldPath: rename ? first : nil, commitSHA: commit.sha, comparison: commit.parents.isEmpty ? "empty tree → \(commit.sha)" : "\(basis) → \(commit.sha)"))
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
        case .commit:
            guard let sha = file.commitSHA else { throw ReviewGitError.unavailable("Commit is unavailable.") }
            let parentResult = try await run(["rev-list", "--parents", "-n", "1", sha], in: worktree)
            let parents = parentResult.text.split(whereSeparator: \.isWhitespace)
            let basis = parents.count > 1 ? String(parents[1]) : "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
            args = ["diff", "--no-textconv", "--no-ext-diff", basis, sha, "--", ":(literal)\(file.oldPath ?? path)", ":(literal)\(path)"]
        }
        return try await readPatch(args, in: worktree)
    }

    private func readPatch(_ args: [String], in root: URL, allowDifference: Bool = false) async throws -> ReviewDiff {
        do {
            let output = try await run(args, in: root, accepted: allowDifference ? [0, 1] : [0])
            if allowDifference && !output.text.hasPrefix("diff --git ") { return ReviewDiff(state: .unavailable, text: "Git could not read the current file.") }
            if output.text.contains("Binary files ") || output.text.contains("GIT binary patch") { return ReviewDiff(state: .binary, text: "Binary file — no text diff available.") }
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
        return try await withTaskCancellationHandler {
            let operationTask = Task.detached(priority: .utility) { try operation.execute(accepted: accepted) }
            return try await withThrowingTaskGroup(of: GitOutput.self) { group in
                group.addTask { try await operationTask.value }
                group.addTask {
                    try await Task.sleep(for: self.timeout)
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
