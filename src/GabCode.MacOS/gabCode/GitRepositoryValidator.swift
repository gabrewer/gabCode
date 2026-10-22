import Foundation

enum GitRepositoryValidationResult: Equatable, Sendable {
    case valid(repository: URL)
    case gitUnavailable(URL)
    case notRepository(URL, stderr: String)
    case failed(URL, status: Int32, stderr: String)
    case timedOut(URL)
    case cancelled
}

/// Opening validation shares discovery's owned-process group and bounded cleanup.
/// No workspace can publish a validation result after its owner cancels.
final class GitRepositoryValidator: @unchecked Sendable {
    private let gitExecutable: URL
    private let commandArguments: [String]?
    private let timeout: Duration

    init(gitExecutable: URL = URL(fileURLWithPath: "/usr/bin/git"),
         arguments: [String]? = nil, timeout: Duration = .seconds(5)) {
        self.gitExecutable = gitExecutable
        self.commandArguments = arguments
        self.timeout = timeout
    }

    func validate(folder: URL) async -> GitRepositoryValidationResult {
        let folder = folder.standardizedFileURL
        guard FileManager.default.isExecutableFile(atPath: gitExecutable.path) else {
            return .gitUnavailable(gitExecutable)
        }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory),
              isDirectory.boolValue, FileManager.default.isReadableFile(atPath: folder.path) else {
            return .notRepository(folder, stderr: "The selected folder is inaccessible.")
        }
        let args = commandArguments ?? ["-C", folder.path, "rev-parse", "--show-toplevel"]
        // Custom validation commands in the existing test seam may produce 200 KiB.
        // Larger output is rejected explicitly rather than silently truncated.
        let operation = GitDiscoveryProcess(executable: gitExecutable, arguments: args, root: folder,
                                            outputLimit: 256 * 1024)
        do {
            let (stdout, status, stderr) = try await operation.runDetailed(budget: timeout, accepted: Set(0...255))
            guard !Task.isCancelled else { return .cancelled }
            guard status == 0 else { return .notRepository(folder, stderr: stderr) }
            guard commandArguments == nil else { return .valid(repository: folder) }
            let repository = URL(fileURLWithPath: stdout.trimmingCharacters(in: .whitespacesAndNewlines), isDirectory: true)
                .standardizedFileURL
            return .valid(repository: repository)
        } catch is CancellationError {
            return .cancelled
        } catch GitDiscoveryProcessError.timedOut {
            return .timedOut(folder)
        } catch GitWorktreeDiscoveryError.cleanupFailed {
            return .failed(folder, status: -1, stderr: "Owned Git cleanup could not be confirmed. This repository is blocked for this app session; inspect owned Git before quitting and reopening gabCode.")
        } catch {
            if Task.isCancelled { return .cancelled }
            return .failed(folder, status: -1, stderr: error.localizedDescription)
        }
    }
}
