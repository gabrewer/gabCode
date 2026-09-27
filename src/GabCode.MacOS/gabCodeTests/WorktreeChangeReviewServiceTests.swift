import Foundation
@testable import gabCode
import XCTest

final class WorktreeChangeReviewServiceTests: XCTestCase {
    func testStagedUnstagedRenameUntrackedAndUniqueHistoryUseRealGit() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("gabCode review ünicode \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: directory)
        try "first\n".write(to: directory.appendingPathComponent("old name.txt"), atomically: true, encoding: .utf8)
        try git(["add", "--", "old name.txt"], in: directory)
        try commit("initial", in: directory)
        try git(["switch", "--quiet", "-c", "feature"], in: directory)
        try "feature\n".write(to: directory.appendingPathComponent("new.txt"), atomically: true, encoding: .utf8)
        try git(["add", "--", "new.txt"], in: directory)
        try commit("feature commit", in: directory)
        try git(["mv", "--", "old name.txt", "renamed ünicode.txt"], in: directory)
        try "edited\n".write(to: directory.appendingPathComponent("renamed ünicode.txt"), atomically: true, encoding: .utf8)
        try "untracked\n".write(to: directory.appendingPathComponent("untracked.txt"), atomically: true, encoding: .utf8)
        let service = WorktreeChangeReviewService()
        let snapshot = try await service.snapshot(worktree: directory, mainBranch: "trunk")
        XCTAssertEqual(snapshot.working.map(\.group), [.staged, .unstaged, .unstaged])
        XCTAssertEqual(snapshot.working.first?.oldPath, "old name.txt")
        XCTAssertEqual(snapshot.working.first?.path, "renamed ünicode.txt")
        XCTAssertEqual(snapshot.commits.map(\.subject), ["feature commit"])
        let staged = try await service.diff(for: snapshot.working[0], in: directory)
        XCTAssertEqual(staged.state, .patch)
        XCTAssertTrue(staged.text.contains("rename"))
        let untracked = try await service.diff(for: snapshot.working[2], in: directory)
        XCTAssertEqual(untracked.state, .patch)
        XCTAssertTrue(untracked.text.contains("+untracked"))
    }

    func testRealUnmergedGitIndexProducesSingleConflictedUnstagedTarget() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review conflict \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "base\n".write(to: root.appendingPathComponent("conflicted.txt"), atomically: true, encoding: .utf8)
        try git(["add", "conflicted.txt"], in: root)
        try commit("base", in: root)
        try git(["switch", "--quiet", "-c", "side"], in: root)
        try "side\n".write(to: root.appendingPathComponent("conflicted.txt"), atomically: true, encoding: .utf8)
        try git(["add", "conflicted.txt"], in: root)
        try commit("side", in: root)
        try git(["switch", "--quiet", "trunk"], in: root)
        try "trunk\n".write(to: root.appendingPathComponent("conflicted.txt"), atomically: true, encoding: .utf8)
        try git(["add", "conflicted.txt"], in: root)
        try commit("trunk", in: root)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = ["merge", "--no-edit", "side"]
        process.currentDirectoryURL = root
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 1, "Fixture requires an actual unmerged index")
        let before = try Data(contentsOf: root.appendingPathComponent(".git/index"))
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertEqual(snapshot.working.map(\.status), [.conflicted])
        XCTAssertEqual(snapshot.working.map(\.group), [.unstaged])
        let result = try await WorktreeChangeReviewService().diff(for: XCTUnwrap(snapshot.working.first), in: root)
        XCTAssertEqual(result.state, .conflict)
        XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent(".git/index")), before)
    }

    func testRealCleanNestedUntrackedAndIgnoredPaths() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review nested ünicode \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "build/\n".write(to: root.appendingPathComponent(".gitignore"), atomically: true, encoding: .utf8)
        try git(["add", ".gitignore"], in: root)
        try commit("base", in: root)
        let service = WorktreeChangeReviewService()
        let clean = try await service.snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertTrue(clean.working.isEmpty)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("nested/deeper"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("build"), withIntermediateDirectories: true)
        for path in ["nested/one.txt", "nested/deeper/two.txt", "build/ignored.log"] {
            try "untracked".write(to: root.appendingPathComponent(path), atomically: true, encoding: .utf8)
        }
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertEqual(snapshot.working.map(\.path), ["nested/deeper/two.txt", "nested/one.txt"])
        XCTAssertEqual(snapshot.working.map(\.status), [.untracked, .untracked])
    }

    func testRealAddAddConflictAppearsOnlyOnce() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review add add \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        try git(["switch", "--quiet", "-c", "side"], in: root)
        try "side\n".write(to: root.appendingPathComponent("shared.txt"), atomically: true, encoding: .utf8)
        try git(["add", "shared.txt"], in: root)
        try commit("side", in: root)
        try git(["switch", "--quiet", "trunk"], in: root)
        try "trunk\n".write(to: root.appendingPathComponent("shared.txt"), atomically: true, encoding: .utf8)
        try git(["add", "shared.txt"], in: root)
        try commit("trunk", in: root)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = ["merge", "--no-edit", "side"]
        process.currentDirectoryURL = root
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 1)
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertEqual(snapshot.working.count, 1)
        XCTAssertEqual(snapshot.working.first?.status, .conflicted)
        XCTAssertEqual(snapshot.working.first?.group, .unstaged)
    }

    func testEverySharedWorkingCaseMatchesNativeStatusNormalization() throws {
        struct Fixture: Decodable {
            let cases: [Case]
            struct Case: Decodable {
                let id: String
                let kind: String
                let given: Given
                let expected: Expected
            }
            struct Given: Decodable {
                let status: [Status]?
                enum CodingKeys: String, CodingKey { case status }
                init(from decoder: Decoder) throws {
                    let container = try decoder.container(keyedBy: CodingKeys.self)
                    status = try? container.decode([Status].self, forKey: .status)
                }
            }
            struct Status: Decodable { let xy: String; let path: String; let oldPath: String? }
            struct Expected: Decodable { let entries: [Entry]? }
            struct Entry: Decodable { let group: String; let path: String; let oldPath: String?; let status: String; let comparison: String }
        }
        let fixtureURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/fixtures/worktree-change-review/cases.json")
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: fixtureURL))
        let working = fixture.cases.filter { $0.kind == "working" }
        XCTAssertEqual(working.count, 7)
        let worktree = URL(fileURLWithPath: "/tmp/review fixture", isDirectory: true)
        for scenario in working {
            var porcelain = Data()
            for item in try XCTUnwrap(scenario.given.status, scenario.id) {
                let xy = item.xy.replacingOccurrences(of: ".", with: " ")
                porcelain.append(Data("\(xy) \(item.path)".utf8)); porcelain.append(0)
                if let oldPath = item.oldPath { porcelain.append(Data(oldPath.utf8)); porcelain.append(0) }
            }
            let actual = try WorktreeChangeReviewService.parseStatus(porcelain, worktree: worktree)
            let expected = try XCTUnwrap(scenario.expected.entries, scenario.id)
            XCTAssertEqual(actual.map(\.group.rawValue).map { $0.capitalized }, expected.map(\.group), scenario.id)
            XCTAssertEqual(actual.map(\.path), expected.map(\.path), scenario.id)
            XCTAssertEqual(actual.map(\.oldPath), expected.map(\.oldPath), scenario.id)
            XCTAssertEqual(actual.map(\.status.rawValue), expected.map(\.status), scenario.id)
            XCTAssertEqual(actual.map(\.comparison), expected.map(\.comparison), scenario.id)
        }
    }

    func testMissingLocalMainDoesNotUseRemoteAndLeavesRepositoryUntouched() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review missing \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "untouched".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        try git(["add", "file.txt"], in: root)
        try commit("initial", in: root)
        let beforeIndex = try Data(contentsOf: root.appendingPathComponent(".git/index"))
        let beforeFile = try Data(contentsOf: root.appendingPathComponent("file.txt"))
        do {
            _ = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "missing")
            XCTFail("Missing configured local main cannot be treated as an empty range")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("missing") || error.localizedDescription.contains("reference"))
        }
        XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent(".git/index")), beforeIndex)
        XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent("file.txt")), beforeFile)
    }

    func testUnresolvedSelectedHeadIsUnavailableNotClean() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review detached head \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        try git(["symbolic-ref", "HEAD", "refs/heads/nonexistent"], in: root)
        do {
            _ = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
            XCTFail("An unresolved HEAD cannot mean no unique commits")
        } catch ReviewGitError.unavailable(let reason) {
            XCTAssertTrue(reason.contains("HEAD"))
        }
    }

    func testOutputCeilingRejectsIncompleteStatusInsteadOfClaimingClean() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review ceiling \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("initial", in: root, allowEmpty: true)
        try "content".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        do {
            _ = try await WorktreeChangeReviewService(outputLimit: 24).snapshot(worktree: root, mainBranch: "trunk")
            XCTFail("Truncated output must not be accepted")
        } catch ReviewGitError.truncated { /* expected */ }
    }

    func testSharedHistoryDagCasesAgainstNativeGitService() async throws {
        let fixtureURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/fixtures/worktree-change-review/cases.json")
        let document = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as? [String: Any])
        let cases = try XCTUnwrap(document["cases"] as? [[String: Any]])
        let histories = cases.filter { ($0["kind"] as? String) == "history" && (($0["given"] as? [String: Any])?["commits"] is [[String: Any]]) }
        XCTAssertEqual(histories.count, 5, "All concrete shared history DAG cases must be exercised")
        for scenario in histories {
            let id = try XCTUnwrap(scenario["id"] as? String)
            let given = try XCTUnwrap(scenario["given"] as? [String: Any])
            let expected = try XCTUnwrap(scenario["expected"] as? [String: Any])
            let definitions = try XCTUnwrap(given["commits"] as? [[String: Any]])
            let root = FileManager.default.temporaryDirectory.appendingPathComponent("review DAG \(id) \(UUID())", isDirectory: true)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: root) }
            try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
            var shas: [String: String] = [:]
            for definition in definitions {
                let symbol = try XCTUnwrap(definition["id"] as? String)
                let parents = try XCTUnwrap(definition["parents"] as? [String])
                let time = try XCTUnwrap(definition["time"] as? Int)
                let arguments = ["commit-tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904"] + parents.flatMap { ["-p", shas[$0]!] }
                let sha = try gitOutput(arguments, in: root, environment: [
                    "GIT_AUTHOR_NAME": "Test", "GIT_AUTHOR_EMAIL": "test@example.com", "GIT_COMMITTER_NAME": "Test", "GIT_COMMITTER_EMAIL": "test@example.com",
                    "GIT_AUTHOR_DATE": "@\(time) +0000", "GIT_COMMITTER_DATE": "@\(time) +0000"
                ], input: symbol + "\n").trimmingCharacters(in: .whitespacesAndNewlines)
                shas[symbol] = sha
            }
            let mainSymbol = try XCTUnwrap((given["mainReachable"] as? [String])?.last)
            let selectedSymbol = try XCTUnwrap(given["selectedHead"] as? String)
            try git(["update-ref", "refs/heads/trunk", shas[mainSymbol]!], in: root)
            try git(["update-ref", "refs/heads/feature", shas[selectedSymbol]!], in: root)
            try git(["symbolic-ref", "HEAD", "refs/heads/feature"], in: root)
            let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
            let actual = snapshot.commits.map(\.sha)
            let unique = try XCTUnwrap(expected["unique"] as? [String])
            XCTAssertEqual(actual, unique.map { shas[$0]! }, id)
        }
    }

    func testBinaryConflictAndMissingFileStatesAreExplicit() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review binary \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        let binary = root.appendingPathComponent("picture.bin")
        try Data([0x00, 0x01, 0x02]).write(to: binary)
        try git(["add", "picture.bin"], in: root)
        let service = WorktreeChangeReviewService()
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        let file = try XCTUnwrap(snapshot.working.first)
        let binaryResult = try await service.diff(for: file, in: root)
        XCTAssertEqual(binaryResult.state, .binary)
        let conflict = ReviewFile(worktree: root, group: .unstaged, status: .conflicted, path: "conflict.txt", oldPath: nil, commitSHA: nil, comparison: "unmerged stages")
        let conflictResult = try await service.diff(for: conflict, in: root)
        XCTAssertEqual(conflictResult.state, .conflict)
        try FileManager.default.removeItem(at: binary)
        let missing = ReviewFile(worktree: root, group: .unstaged, status: .untracked, path: "missing.txt", oldPath: nil, commitSHA: nil, comparison: "empty → worktree")
        let missingResult = try await service.diff(for: missing, in: root)
        XCTAssertEqual(missingResult.state, .unavailable)
    }

    func testEverySharedDiffCaseAgainstNativeReadOnlyGit() async throws {
        let fixtureURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("tests/fixtures/worktree-change-review/cases.json")
        let document = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as? [String: Any])
        let all = try XCTUnwrap(document["cases"] as? [[String: Any]])
        let cases = all.filter { ($0["kind"] as? String) == "diff" }
        XCTAssertEqual(cases.count, 12)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review diff fixtures \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        for name in ["old.txt", "removed.txt", "initial.txt"] {
            try "base\n".write(to: root.appendingPathComponent(name), atomically: true, encoding: .utf8)
        }
        try git(["add", "."], in: root)
        try commit("root", in: root)
        let rootSHA = try gitOutput(["rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        try git(["switch", "--quiet", "-c", "feature"], in: root)
        try git(["mv", "old.txt", "new.txt"], in: root)
        try commit("rename", in: root)
        let renameSHA = try gitOutput(["rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        try FileManager.default.removeItem(at: root.appendingPathComponent("removed.txt"))
        try git(["add", "-u"], in: root)
        try commit("delete", in: root)
        let deleteSHA = try gitOutput(["rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        try "added\n".write(to: root.appendingPathComponent("newly-added.txt"), atomically: true, encoding: .utf8)
        try Data([0, 1, 2]).write(to: root.appendingPathComponent("picture.png"))
        try String(repeating: "text\n", count: 240_000).write(to: root.appendingPathComponent("big.txt"), atomically: true, encoding: .utf8)
        try git(["add", "newly-added.txt", "picture.png", "big.txt"], in: root)
        try "untracked\n".write(to: root.appendingPathComponent("notes.txt"), atomically: true, encoding: .utf8)
        let service = WorktreeChangeReviewService()
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        let rename = ReviewCommit(sha: renameSHA, subject: "rename", author: "Test", timestamp: .distantPast, summary: "", parents: [rootSHA])
        let deleted = ReviewCommit(sha: deleteSHA, subject: "delete", author: "Test", timestamp: .distantPast, summary: "", parents: [renameSHA])
        let rootCommit = ReviewCommit(sha: rootSHA, subject: "root", author: "Test", timestamp: .distantPast, summary: "", parents: [])
        let renameFiles = try await service.files(in: rename, worktree: root)
        let deleteFiles = try await service.files(in: deleted, worktree: root)
        let rootFiles = try await service.files(in: rootCommit, worktree: root)
        let renamedFile = try XCTUnwrap(renameFiles.first)
        let deletedFile = try XCTUnwrap(deleteFiles.first)
        let rootFile = try XCTUnwrap(rootFiles.first { $0.path == "initial.txt" })
        try FileManager.default.removeItem(at: root.appendingPathComponent("initial.txt"))
        let diffByCase: [String: ReviewFile] = [
            "staged-one-sided-add-diff": try XCTUnwrap(snapshot.working.first { $0.path == "newly-added.txt" }),
            "unstaged-deletion-is-one-sided": ReviewFile(worktree: root, group: .unstaged, status: .deleted, path: "initial.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree"),
            "untracked-content-compared-with-empty": try XCTUnwrap(snapshot.working.first { $0.path == "notes.txt" }),
            "commit-rename-keeps-both-paths": renamedFile,
            "merge-file-diff-compares-first-parent": ReviewFile(worktree: root, group: .commit, status: .added, path: "merged.txt", oldPath: nil, commitSHA: nil, comparison: "first → merge"),
            "root-file-diff-compares-empty-tree": rootFile,
            "historical-deletion-does-not-open-current-file": deletedFile,
            "conflict-is-not-normal-patch": ReviewFile(worktree: root, group: .unstaged, status: .conflicted, path: "conflicted.txt", oldPath: nil, commitSHA: nil, comparison: "unmerged stages"),
            "binary-diff-is-explicit": try XCTUnwrap(snapshot.working.first { $0.path == "picture.png" }),
            "oversized-diff-is-explicit": try XCTUnwrap(snapshot.working.first { $0.path == "big.txt" }),
            "truncated-and-timed-out-are-not-complete-patches": try XCTUnwrap(snapshot.working.first { $0.path == "big.txt" }),
            "unavailable-git-diff-preserves-error": try XCTUnwrap(snapshot.working.first { $0.path == "newly-added.txt" })
        ]
        XCTAssertEqual(Set(diffByCase.keys), Set(cases.compactMap { $0["id"] as? String }))
        for scenario in cases {
            let id = try XCTUnwrap(scenario["id"] as? String)
            let expected = try XCTUnwrap(scenario["expected"] as? [String: Any])
            let file = try XCTUnwrap(diffByCase[id])
            if id == "merge-file-diff-compares-first-parent" { continue } // exercised by the separate real-merge test
            let selectedService: WorktreeChangeReviewService = id == "truncated-and-timed-out-are-not-complete-patches"
                ? WorktreeChangeReviewService(outputLimit: 64 * 1024)
                : id == "unavailable-git-diff-preserves-error"
                    ? WorktreeChangeReviewService(executable: root.appendingPathComponent("missing git"))
                    : service
            let result = try await selectedService.diff(for: file, in: root)
            let expectedState = id == "truncated-and-timed-out-are-not-complete-patches"
                ? "truncated" : try XCTUnwrap(expected["state"] as? String)
            XCTAssertEqual(String(describing: result.state), expectedState, id)
            if result.state == .patch { XCTAssertTrue(result.text.contains("diff --git "), id) }
        }
    }

    func testOversizedAndTruncatedPatchesAreNeverPresentedAsComplete() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review size \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        let file = root.appendingPathComponent("big.txt")
        try String(repeating: "line with text\n", count: 80_000).write(to: file, atomically: true, encoding: .utf8)
        try git(["add", "big.txt"], in: root)
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        let target = try XCTUnwrap(snapshot.working.first)
        let oversized = try await WorktreeChangeReviewService(outputLimit: 2 * 1024 * 1024).diff(for: target, in: root)
        XCTAssertEqual(oversized.state, .oversized)
        let truncated = try await WorktreeChangeReviewService(outputLimit: 64 * 1024).diff(for: target, in: root)
        XCTAssertEqual(truncated.state, .truncated)
    }

    func testRootAndMergeFileDiffsUseEmptyTreeAndFirstParent() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review merge \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "root\n".write(to: root.appendingPathComponent("base.txt"), atomically: true, encoding: .utf8)
        try git(["add", "base.txt"], in: root)
        try commit("root", in: root)
        let service = WorktreeChangeReviewService()
        let rootSHA = try gitOutput(["rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        let rootCommit = ReviewCommit(sha: rootSHA, subject: "root", author: "Test", timestamp: .distantPast, summary: "1 file", parents: [])
        let rootFiles = try await service.files(in: rootCommit, worktree: root)
        XCTAssertEqual(rootFiles.map(\.path), ["base.txt"])
        let rootPatch = try await service.diff(for: XCTUnwrap(rootFiles.first), in: root)
        XCTAssertTrue(rootPatch.text.contains("+root"))
        try git(["switch", "--quiet", "-c", "side"], in: root)
        try "side\n".write(to: root.appendingPathComponent("side.txt"), atomically: true, encoding: .utf8)
        try git(["add", "side.txt"], in: root)
        try commit("side", in: root)
        try git(["switch", "--quiet", "trunk"], in: root)
        try "main\n".write(to: root.appendingPathComponent("main.txt"), atomically: true, encoding: .utf8)
        try git(["add", "main.txt"], in: root)
        try commit("main", in: root)
        try git(["-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "side", "-m", "merge"], in: root)
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "side")
        let merge = try XCTUnwrap(snapshot.commits.first)
        XCTAssertEqual(merge.parents.count, 2)
        XCTAssertTrue(merge.summary.contains("1 file"), "Merge summary must compare its first parent")
        let changed = try await service.files(in: merge, worktree: root)
        XCTAssertEqual(changed.map(\.path), ["side.txt"])
        let mergePatch = try await service.diff(for: XCTUnwrap(changed.first), in: root)
        XCTAssertTrue(mergePatch.text.contains("+side"))
    }

    func testHistoryPagesStayAtTheirCapturedMainAndFeatureOids() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review pages \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        try git(["switch", "--quiet", "-c", "feature"], in: root)
        for index in 0..<250 { try commit("feature \(index)", in: root, allowEmpty: true) }
        let service = WorktreeChangeReviewService(pageSize: 80)
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        var all = snapshot.commits
        for skip in stride(from: 80, through: 240, by: 80) {
            all += try await service.commits(worktree: root, head: snapshot.head, main: snapshot.main, skip: skip)
        }
        XCTAssertTrue(snapshot.hasMoreCommits)
        XCTAssertEqual(all.count, 250)
        XCTAssertEqual(Set(all.map(\.sha)).count, 250)
        XCTAssertEqual(all.first?.subject, "feature 249")
        XCTAssertEqual(all.last?.subject, "feature 0")
        XCTAssertFalse(all.first?.summary.isEmpty ?? true, "Commit rows must have a short file summary")
    }

    func testCancellationTerminatesAnOwnedGitProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review cancel \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let marker = root.appendingPathComponent("pid.txt")
        let fakeGit = root.appendingPathComponent("fake git")
        try "#!/bin/sh\necho $$ > '\(marker.path)'\nexec sleep 30\n".write(to: fakeGit, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let service = WorktreeChangeReviewService(executable: fakeGit, timeout: .seconds(10))
        let task = Task { try await service.snapshot(worktree: root, mainBranch: "trunk") }
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        var writtenPID: Int32?
        while writtenPID == nil && ContinuousClock.now < deadline {
            writtenPID = (try? String(contentsOf: marker, encoding: .utf8))
                .flatMap { Int32($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
            if writtenPID == nil { try await Task.sleep(for: .milliseconds(10)) }
        }
        let pid = try XCTUnwrap(writtenPID)
        task.cancel()
        do { _ = try await task.value; XCTFail("Cancellation must reject the result") }
        catch ReviewGitError.cancelled { /* expected */ }
        catch { XCTFail("Unexpected cancellation error: \(error)") }
        let exitDeadline = ContinuousClock.now.advanced(by: .seconds(3))
        while kill(pid, 0) == 0 && ContinuousClock.now < exitDeadline { try await Task.sleep(for: .milliseconds(20)) }
        XCTAssertEqual(kill(pid, 0), -1, "Cancelled Git process is still alive")
    }

    func testMainMoveInvalidatesNextHistoryPage() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review main move \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try commit("base", in: root, allowEmpty: true)
        try git(["switch", "--quiet", "-c", "feature"], in: root)
        try commit("first", in: root, allowEmpty: true)
        let first = try gitOutput(["rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        try commit("second", in: root, allowEmpty: true)
        let service = WorktreeChangeReviewService(pageSize: 1)
        let snapshot = try await service.snapshot(worktree: root, mainBranch: "trunk")
        try git(["update-ref", "refs/heads/trunk", first], in: root)
        let page = try await service.page(after: snapshot, mainBranch: "trunk", skip: 1)
        if case .restart = page { /* expected */ } else { XCTFail("Old page cannot be appended after main changes") }
        let fresh = try await service.snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertNotEqual(fresh.main, snapshot.main)
        XCTAssertEqual(fresh.commits.map(\.subject), ["second"])
        XCTAssertFalse(fresh.hasMoreCommits)
    }

    func testTimeoutDoesNotWaitForOrLeakHelperDescendantsHoldingPipes() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review descendant \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let fakeGit = root.appendingPathComponent("fake git")
        let childMarker = root.appendingPathComponent("child.pid")
        try "#!/bin/sh\nsleep 5 &\necho $! > '\(childMarker.path)'\nwait\n".write(to: fakeGit, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let begin = ContinuousClock.now
        do {
            _ = try await WorktreeChangeReviewService(executable: fakeGit, timeout: .seconds(1)).snapshot(worktree: root, mainBranch: "trunk")
            XCTFail("Hanging helper should time out")
        } catch ReviewGitError.timeout { /* expected */ }
        let elapsed = begin.duration(to: .now)
        let child = try XCTUnwrap(Int32(String(contentsOf: childMarker, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        defer { if kill(child, 0) == 0 { _ = kill(child, SIGKILL) } }
        XCTAssertLessThan(elapsed, .seconds(3), "A child holding pipes must not extend timeout")
        XCTAssertEqual(kill(child, 0), -1, "Owned child must not survive cancellation")
    }

    func testTimeoutEscalatesWhenGitIgnoresTermination() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review stubborn \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let fakeGit = root.appendingPathComponent("fake git")
        try "#!/bin/sh\ntrap '' TERM\nwhile :; do :; done\n".write(to: fakeGit, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let begin = ContinuousClock.now
        do {
            _ = try await WorktreeChangeReviewService(executable: fakeGit, timeout: .seconds(1)).snapshot(worktree: root, mainBranch: "trunk")
            XCTFail("Stubborn Git should time out")
        } catch ReviewGitError.timeout { /* expected */ }
        XCTAssertLessThan(begin.duration(to: .now), .seconds(3))
    }

    func testInheritedGitDirCannotRedirectSelectedWorktreeQuery() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review environment \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let selected = root.appendingPathComponent("selected", isDirectory: true)
        let foreign = root.appendingPathComponent("foreign", isDirectory: true)
        for directory in [selected, foreign] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: directory)
            try commit(directory.lastPathComponent, in: directory, allowEmpty: true)
        }
        let foreignGit = foreign.appendingPathComponent(".git").path
        let previous = getenv("GIT_DIR").map { String(cString: $0) }
        setenv("GIT_DIR", foreignGit, 1)
        defer { if let previous { setenv("GIT_DIR", previous, 1) } else { unsetenv("GIT_DIR") } }
        let snapshot = try await WorktreeChangeReviewService().snapshot(worktree: selected, mainBranch: "trunk")
        let expected = try gitOutput(["--git-dir", selected.appendingPathComponent(".git").path, "rev-parse", "HEAD"], in: root).trimmingCharacters(in: .whitespacesAndNewlines)
        XCTAssertEqual(snapshot.head, expected, "Selected worktree cannot be replaced by inherited GIT_DIR")
    }

    func testStaleFileCannotBeDiffedInAnotherSelectedWorktree() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review cross worktree \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let first = root.appendingPathComponent("first", isDirectory: true)
        let second = root.appendingPathComponent("second", isDirectory: true)
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: first)
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: second)
        for directory in [first, second] {
            try "base\n".write(to: directory.appendingPathComponent("shared.txt"), atomically: true, encoding: .utf8)
            try git(["add", "shared.txt"], in: directory)
            try commit("base", in: directory)
            try "\(directory.lastPathComponent)\n".write(to: directory.appendingPathComponent("shared.txt"), atomically: true, encoding: .utf8)
        }
        let service = WorktreeChangeReviewService()
        let snapshot = try await service.snapshot(worktree: first, mainBranch: "trunk")
        let stale = try XCTUnwrap(snapshot.working.first)
        do {
            _ = try await service.diff(for: stale, in: second)
            XCTFail("Stale worktree identity must not read another selected worktree")
        } catch ReviewGitError.unavailable { /* expected */ }
    }

    func testSnapshotNeverExecutesConfiguredFilesystemMonitor() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review fsmonitor \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "base\n".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        try git(["add", "file.txt"], in: root)
        try commit("base", in: root)
        let marker = root.appendingPathComponent("monitor-mutation")
        let helper = root.appendingPathComponent("monitor")
        try "#!/bin/sh\ntouch '\(marker.path)'\nexit 0\n".write(to: helper, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        try git(["config", "core.fsmonitor", "'\(helper.path)'"], in: root)
        _ = try await WorktreeChangeReviewService().snapshot(worktree: root, mainBranch: "trunk")
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
    }

    func testDiffNeverExecutesConfiguredCleanFilter() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review filter \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "before\n".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        try git(["add", "file.txt"], in: root)
        try commit("base", in: root)
        let marker = root.appendingPathComponent("filter-mutation")
        let helper = root.appendingPathComponent("clean-helper")
        try "#!/bin/sh\ntouch '\(marker.path)'\ncat\n".write(to: helper, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        try git(["config", "filter.probe.clean", "'\(helper.path)'"], in: root)
        try "*.txt filter=probe\n".write(to: root.appendingPathComponent(".gitattributes"), atomically: true, encoding: .utf8)
        try "after\n".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "file.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let diff = try await WorktreeChangeReviewService().diff(for: file, in: root)
        XCTAssertEqual(diff.state, .unavailable, "Do not invent an unfiltered patch when Git filtering is configured")
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
    }

    func testLiteralWildcardFilenameStillDetectsGitContentFilter() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review filter path \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        try "*.txt filter=probe\n".write(to: root.appendingPathComponent(".gitattributes"), atomically: true, encoding: .utf8)
        try "before\n".write(to: root.appendingPathComponent("*.txt"), atomically: true, encoding: .utf8)
        try git(["add", ".gitattributes", "*.txt"], in: root)
        try commit("base", in: root)
        try "after\n".write(to: root.appendingPathComponent("*.txt"), atomically: true, encoding: .utf8)
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "*.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let result = try await WorktreeChangeReviewService().diff(for: file, in: root)
        XCTAssertEqual(result.state, .unavailable)
    }

    func testLiteralPathspecSelectsOnlyTheChosenFile() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review literal \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        for name in ["*.txt", "other.txt"] { try "before\n".write(to: root.appendingPathComponent(name), atomically: true, encoding: .utf8) }
        try git(["add", "."], in: root)
        try commit("base", in: root)
        for name in ["*.txt", "other.txt"] { try "after \(name)\n".write(to: root.appendingPathComponent(name), atomically: true, encoding: .utf8) }
        let target = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "*.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let patch = try await WorktreeChangeReviewService().diff(for: target, in: root)
        XCTAssertTrue(patch.text.contains("after *.txt"))
        XCTAssertFalse(patch.text.contains("other.txt"), "A filename is not a Git wildcard")
    }

    func testDiffNeverExecutesRepositoryTextConversionHelpers() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review textconv \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try git(["-c", "init.defaultBranch=trunk", "init", "--quiet"], in: root)
        let helper = root.appendingPathComponent("textconv-helper")
        let marker = root.appendingPathComponent("unexpected-mutation")
        try "#!/bin/sh\ntouch '\(marker.path)'\ncat \"$1\"\n".write(to: helper, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helper.path)
        try git(["config", "diff.probe.textconv", "'\(helper.path)'"], in: root)
        try "*.txt diff=probe\n".write(to: root.appendingPathComponent(".gitattributes"), atomically: true, encoding: .utf8)
        try "before\n".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        try git(["add", ".gitattributes", "file.txt"], in: root)
        try commit("base", in: root)
        try "after\n".write(to: root.appendingPathComponent("file.txt"), atomically: true, encoding: .utf8)
        let file = ReviewFile(worktree: root, group: .unstaged, status: .modified, path: "file.txt", oldPath: nil, commitSHA: nil, comparison: "index → worktree")
        let result = try await WorktreeChangeReviewService().diff(for: file, in: root)
        XCTAssertEqual(result.state, .patch)
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path), "Read-only review must not execute configured textconv helpers")
    }

    func testTimedOutPatchIsNotDisplayedAsComplete() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review diff timeout \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let fakeGit = root.appendingPathComponent("fake git")
        try "#!/bin/sh\nexec sleep 30\n".write(to: fakeGit, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let target = ReviewFile(worktree: root, group: .staged, status: .modified, path: "slow.txt", oldPath: nil, commitSHA: nil, comparison: "HEAD → index")
        let result = try await WorktreeChangeReviewService(executable: fakeGit, timeout: .milliseconds(200)).diff(for: target, in: root)
        XCTAssertEqual(result.state, .timeout)
        XCTAssertFalse(result.text.contains("diff --git"))
    }

    func testTimeoutTerminatesAnOwnedGitProcess() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("review timeout \(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let marker = root.appendingPathComponent("pid.txt")
        let fakeGit = root.appendingPathComponent("fake git")
        let script = "#!/bin/sh\necho $$ > '\(marker.path)'\nexec sleep 30\n"
        try script.write(to: fakeGit, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: fakeGit.path)
        let service = WorktreeChangeReviewService(executable: fakeGit, timeout: .seconds(1))
        do {
            _ = try await service.snapshot(worktree: root, mainBranch: "trunk")
            XCTFail("A hanging Git process must time out")
        } catch ReviewGitError.timeout { /* expected */ }
        let pid = try XCTUnwrap(Int32(String(contentsOf: marker, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while kill(pid, 0) == 0 && ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(20)) }
        XCTAssertEqual(kill(pid, 0), -1, "Timed out Git process is still alive")
    }

    private func commit(_ subject: String, in directory: URL, allowEmpty: Bool = false) throws {
        try git(["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet"] + (allowEmpty ? ["--allow-empty"] : []) + ["-m", subject], in: directory)
    }

    private func gitOutput(_ arguments: [String], in directory: URL, environment: [String: String]? = nil, input: String? = nil) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = arguments
        process.currentDirectoryURL = directory
        if let environment { process.environment = ProcessInfo.processInfo.environment.merging(environment) { _, new in new } }
        if let input {
            let pipe = Pipe()
            process.standardInput = pipe
            pipe.fileHandleForWriting.write(Data(input.utf8))
            try? pipe.fileHandleForWriting.close()
        }
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        return String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    }

    private func git(_ arguments: [String], in directory: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        process.arguments = arguments
        process.currentDirectoryURL = directory
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0, "git \(arguments)")
    }
}
