import Foundation

enum GitRepositoryValidationResult: Equatable, Sendable {
    case valid(repository: URL)
    case gitUnavailable(URL)
    case notRepository(URL, stderr: String)
    case failed(URL, status: Int32, stderr: String)
    case timedOut(URL)
    case cancelled
}

final class GitRepositoryValidator: @unchecked Sendable {
    private let gitExecutable: URL
    private let commandArguments: [String]?
    private let timeout: Duration

    init(
        gitExecutable: URL = URL(fileURLWithPath: "/usr/bin/git"),
        arguments: [String]? = nil,
        timeout: Duration = .seconds(5)
    ) {
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
              isDirectory.boolValue,
              FileManager.default.isReadableFile(atPath: folder.path)
        else {
            return .notRepository(folder, stderr: "The selected folder is inaccessible.")
        }

        let arguments = commandArguments ?? ["-C", folder.path, "rev-parse", "--show-toplevel"]
        let operation = GitProcessOperation(
            executable: gitExecutable,
            arguments: arguments,
            currentDirectory: folder
        )
        let result = await withTaskCancellationHandler {
            await run(operation: operation)
        } onCancel: {
            operation.cancel()
        }
        switch result {
        case .cancelled:
            return .cancelled
        case .timedOut:
            return .timedOut(folder)
        case let .finished(status, stdout, stderr):
            guard status == 0 else {
                return .notRepository(folder, stderr: bounded(stderr))
            }
            guard commandArguments == nil else {
                return .valid(repository: folder)
            }
            let repositoryPath = URL(fileURLWithPath: stdout.trimmingCharacters(in: .whitespacesAndNewlines), isDirectory: true)
                .standardizedFileURL
            return .valid(repository: repositoryPath)
        case let .launchFailed(message):
            return .failed(folder, status: -1, stderr: message)
        }
    }

    private func run(operation: GitProcessOperation) async -> GitProcessResult {
        let processTask = Task { await operation.run() }
        return await withTaskGroup(of: GitProcessResult.self) { group in
            group.addTask { await processTask.value }
            group.addTask {
                do {
                    try await Task.sleep(for: self.timeout)
                    return .timedOut
                } catch {
                    return .cancelled
                }
            }
            let first = await group.next() ?? .cancelled
            group.cancelAll()
            if case .timedOut = first {
                operation.cancel()
            } else if Task.isCancelled {
                operation.cancel()
                return .cancelled
            }
            return first
        }
    }

    private func bounded(_ output: String) -> String {
        String(output.prefix(64 * 1024))
    }
}

private enum GitProcessResult: Sendable {
    case finished(status: Int32, stdout: String, stderr: String)
    case launchFailed(String)
    case timedOut
    case cancelled
}

private final class BoundedPipeCapture: @unchecked Sendable {
    private let handle: FileHandle
    private let lock = NSLock()
    private var retained = Data()

    init(handle: FileHandle) {
        self.handle = handle
        handle.readabilityHandler = { [weak self] handle in
            self?.drain(handle)
        }
    }

    var data: Data {
        lock.lock()
        defer { lock.unlock() }
        return retained
    }

    func stop() {
        handle.readabilityHandler = nil
        drain(handle)
    }

    private func drain(_ handle: FileHandle) {
        let chunk = handle.readData(ofLength: 4096)
        guard !chunk.isEmpty else { return }
        lock.lock()
        if retained.count < 64 * 1024 {
            retained.append(chunk.prefix(64 * 1024 - retained.count))
        }
        lock.unlock()
    }
}

private final class GitProcessOperation: @unchecked Sendable {
    private let process: Process
    private let lock = NSLock()
    private var cancelled = false

    init(executable: URL, arguments: [String], currentDirectory: URL) {
        process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
    }

    func cancel() {
        lock.lock()
        cancelled = true
        let isRunning = process.isRunning
        if isRunning { process.terminate() }
        lock.unlock()
    }

    func run() async -> GitProcessResult {
        await Task.detached(priority: .utility) { [self] in
            lock.lock()
            let wasCancelled = cancelled
            lock.unlock()
            guard !wasCancelled else { return .cancelled }

            let stdout = Pipe()
            let stderr = Pipe()
            process.standardOutput = stdout
            process.standardError = stderr
            let stdoutCapture = BoundedPipeCapture(handle: stdout.fileHandleForReading)
            let stderrCapture = BoundedPipeCapture(handle: stderr.fileHandleForReading)
            do {
                try process.run()
            } catch {
                return .launchFailed(error.localizedDescription)
            }
            process.waitUntilExit()
            stdoutCapture.stop()
            stderrCapture.stop()
            if Task.isCancelled { return .cancelled }
            return .finished(
                status: process.terminationStatus,
                stdout: String(data: stdoutCapture.data, encoding: .utf8) ?? "",
                stderr: String(data: stderrCapture.data, encoding: .utf8) ?? ""
            )
        }.value
    }
}
