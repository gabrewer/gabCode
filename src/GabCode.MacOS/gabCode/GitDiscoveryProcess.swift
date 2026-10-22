import Darwin
import Foundation

enum GitDiscoveryProcessError: Error { case timedOut }

/// One read-only Git process in its own POSIX process group. Pipes are drained
/// with nonblocking reads; unconfirmed cleanup quarantines the affected root.
final class GitDiscoveryProcess: @unchecked Sendable {
    private nonisolated static let blockedLock = NSLock()
    private nonisolated(unsafe) static var blockedRoots: Set<URL> = []
    private let executable: URL
    private let arguments: [String]
    private let root: URL
    private let outputLimit: Int
    private let lock = NSLock()
    private nonisolated(unsafe) var cancelled = false // protected by lock
    private nonisolated(unsafe) var pid: pid_t = 0 // protected by lock

    nonisolated init(executable: URL, arguments: [String], root: URL, outputLimit: Int = 64 * 1024) {
        self.executable = executable
        self.arguments = arguments
        self.root = root
        self.outputLimit = outputLimit
    }

    nonisolated func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
        // Only the polling owner signals its group; the cancellation callback
        // cannot race reaping/PID reuse after an exceptional cleanup path.
    }

    nonisolated func run(budget: Duration, accepted: Set<Int32>) async throws -> (String, Int32) {
        let result = try await runDetailed(budget: budget, accepted: accepted)
        return (result.0, result.1)
    }

    nonisolated func runDetailed(budget: Duration, accepted: Set<Int32>) async throws -> (String, Int32, String) {
        try await withTaskCancellationHandler {
            try await Task.detached(priority: .utility) { [self] in
                try execute(budget: budget, accepted: accepted)
            }.value
        } onCancel: { cancel() }
    }

    private nonisolated func execute(budget: Duration, accepted: Set<Int32>) throws -> (String, Int32, String) {
        Self.blockedLock.lock()
        let blocked = Self.blockedRoots.contains(root.standardizedFileURL)
        Self.blockedLock.unlock()
        if blocked { throw GitWorktreeDiscoveryError.cleanupFailed(root) }
        var out: [Int32] = [0, 0], err: [Int32] = [0, 0]
        guard Darwin.pipe(&out) == 0 else { throw failure("Cannot open Git output pipe") }
        guard Darwin.pipe(&err) == 0 else {
            close(out[0]); close(out[1]); throw failure("Cannot open Git error pipe")
        }
        defer { close(out[0]); close(err[0]) }
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        guard posix_spawn_file_actions_init(&actions) == 0 else {
            close(out[1]); close(err[1]); throw failure("Cannot initialize owned Git launch")
        }
        guard posix_spawnattr_init(&attributes) == 0 else {
            posix_spawn_file_actions_destroy(&actions)
            close(out[1]); close(err[1]); throw failure("Cannot initialize owned Git process group")
        }
        defer { posix_spawn_file_actions_destroy(&actions); posix_spawnattr_destroy(&attributes) }
        let actionResults = [
            posix_spawn_file_actions_adddup2(&actions, out[1], STDOUT_FILENO),
            posix_spawn_file_actions_adddup2(&actions, err[1], STDERR_FILENO),
            posix_spawn_file_actions_addclose(&actions, out[0]),
            posix_spawn_file_actions_addclose(&actions, err[0]),
            posix_spawn_file_actions_addclose(&actions, out[1]),
            posix_spawn_file_actions_addclose(&actions, err[1])
        ]
        guard actionResults.allSatisfy({ $0 == 0 }) else {
            close(out[1]); close(err[1]); throw failure("Cannot configure owned Git pipes")
        }
        let directoryResult = root.path.withCString { posix_spawn_file_actions_addchdir(&actions, $0) }
        guard directoryResult == 0 else { close(out[1]); close(err[1]); throw failure("Cannot select Git directory") }
        guard posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT)) == 0,
              posix_spawnattr_setpgroup(&attributes, 0) == 0 else {
            close(out[1]); close(err[1]); throw failure("Cannot isolate owned Git process group")
        }
        let argv = ([executable.path] + arguments).map { strdup($0) }
        defer { argv.forEach { free($0) } }
        var pointers: [UnsafeMutablePointer<CChar>?] = argv + [nil]
        let environment = ProcessInfo.processInfo.environment
            .filter { !$0.key.hasPrefix("GIT_") }
            .map { strdup("\($0.key)=\($0.value)") }
        defer { environment.forEach { free($0) } }
        var envp: [UnsafeMutablePointer<CChar>?] = environment + [nil]
        var child: pid_t = 0
        lock.lock()
        if cancelled { lock.unlock(); close(out[1]); close(err[1]); throw CancellationError() }
        let launch = executable.path.withCString { path in
            pointers.withUnsafeMutableBufferPointer { args in
                envp.withUnsafeMutableBufferPointer { env in
                    posix_spawn(&child, path, &actions, &attributes, args.baseAddress!, env.baseAddress!)
                }
            }
        }
        if launch == 0 { pid = child }
        let wasCancelled = cancelled
        lock.unlock()
        close(out[1]); close(err[1])
        guard launch == 0 else { throw failure("Git launch failed (\(launch))") }
        if wasCancelled { _ = Darwin.kill(-child, SIGTERM) }
        guard fcntl(out[0], F_SETFL, O_NONBLOCK) != -1,
              fcntl(err[0], F_SETFL, O_NONBLOCK) != -1 else {
            _ = Darwin.kill(-child, SIGKILL)
            let cleanupEnd = ContinuousClock.now.advanced(by: .seconds(1))
            var status: Int32 = 0
            while ContinuousClock.now < cleanupEnd {
                if waitpid(child, &status, WNOHANG) == child {
                    lock.lock(); pid = 0; lock.unlock()
                    throw failure("Cannot configure nonblocking Git pipes")
                }
                usleep(5_000)
            }
            Self.quarantine(root, child: child)
            throw GitWorktreeDiscoveryError.cleanupFailed(root)
        }
        let deadline = ContinuousClock.now.advanced(by: budget)
        var stopStart: ContinuousClock.Instant?
        var sentKill = false
        var stdout = Data(), stderr = Data()
        var stdoutOpen = true, stderrOpen = true
        var stdoutOverflow = false, stderrOverflow = false
        var pipeError = false
        var status: Int32 = 0
        var reaped = false
        var leaderExited = false
        var abandonedChild = false
        while !reaped || stdoutOpen || stderrOpen {
            let now = ContinuousClock.now
            lock.lock(); let stopRequested = cancelled; lock.unlock()
            if (stopRequested || now >= deadline || stdoutOverflow || stderrOverflow || pipeError) && stopStart == nil {
                stopStart = now
                _ = Darwin.kill(-child, SIGTERM)
            }
            if let stopStart, !sentKill && stopStart.duration(to: now) >= .milliseconds(250) {
                _ = Darwin.kill(-child, SIGKILL)
                sentKill = true
            }
            if let stopStart, stopStart.duration(to: now) >= .seconds(1) {
                _ = Darwin.kill(-child, SIGKILL)
                Self.quarantine(root, child: child)
                throw GitWorktreeDiscoveryError.cleanupFailed(root)
            }
            // Hold the leader unreaped until every pipe closes: its PID/process-group
            // identity cannot be reused while cleanup might still signal descendants.
            // WNOWAIT keeps the leader PID reserved while its process group is
            // signalled and drained. Git may exit zero after spawning a helper
            // which closed its pipes; do not mistake that for completed cleanup.
            if !leaderExited {
                var info = siginfo_t()
                let result = waitid(P_PID, id_t(child), &info, WEXITED | WNOHANG | WNOWAIT)
                if result == -1 {
                    Self.block(root) // ownership lost; signalling a reused PID is unsafe
                    throw GitWorktreeDiscoveryError.cleanupFailed(root)
                }
                if info.si_pid == child {
                    leaderExited = true
                    // Check group members *before* signalling. A successful Git
                    // exit with a live helper cannot publish partial output.
                    var members = [pid_t](repeating: 0, count: 64)
                    let byteCount = members.withUnsafeMutableBytes {
                        proc_listpids(UInt32(PROC_PGRP_ONLY), UInt32(bitPattern: child),
                                      $0.baseAddress, Int32($0.count))
                    }
                    abandonedChild = byteCount < 0 || members.prefix(max(0, Int(byteCount) / MemoryLayout<pid_t>.size))
                        .contains(where: { $0 > 0 && $0 != child })
                    _ = Darwin.kill(-child, SIGKILL)
                }
            }
            if stdoutOpen {
                do { stdoutOpen = try Self.drain(fd: out[0], into: &stdout, overflow: &stdoutOverflow, limit: outputLimit) }
                catch { stdoutOpen = false; pipeError = true }
            }
            if stderrOpen {
                do { stderrOpen = try Self.drain(fd: err[0], into: &stderr, overflow: &stderrOverflow, limit: 64 * 1024) }
                catch { stderrOpen = false; pipeError = true }
            }
            if leaderExited && !stdoutOpen && !stderrOpen && !reaped {
                lock.lock()
                let result = waitpid(child, &status, WNOHANG)
                if result == child { reaped = true; pid = 0 }
                lock.unlock()
                if result == -1 {
                    Self.block(root) // identity no longer owned; never signal reused PID
                    throw GitWorktreeDiscoveryError.cleanupFailed(root)
                }
            }
            if !reaped || stdoutOpen || stderrOpen { usleep(5_000) }
        }
        lock.lock(); pid = 0; let stopped = cancelled; lock.unlock()
        if stopped { throw CancellationError() }
        if abandonedChild { throw failure("Owned Git process left a background child; discovery was incomplete") }
        if stdoutOverflow || stderrOverflow { throw failure("Git output exceeded its safe read limit") }
        if pipeError { throw failure("Cannot drain owned Git pipe") }
        if stopStart != nil { throw GitDiscoveryProcessError.timedOut }
        let exitCode = (status >> 8) & 0xff
        guard status & 0x7f == 0 && accepted.contains(exitCode) else {
            throw failure(String(decoding: stderr, as: UTF8.self))
        }
        return (String(decoding: stdout, as: UTF8.self), exitCode, String(decoding: stderr, as: UTF8.self))
    }

    private nonisolated static func drain(fd: Int32, into data: inout Data, overflow: inout Bool, limit: Int) throws -> Bool {
        var bytes = [UInt8](repeating: 0, count: 4096)
        for _ in 0..<16 {
            let count = Darwin.read(fd, &bytes, bytes.count)
            if count > 0 {
                if data.count + count > limit { overflow = true }
                if data.count < limit { data.append(contentsOf: bytes.prefix(min(count, limit - data.count))) }
            } else if count == 0 { return false }
            else if errno == EAGAIN || errno == EWOULDBLOCK { return true }
            else if errno != EINTR { throw failure("Cannot drain owned Git pipe") }
        }
        return true // bounded read quantum; re-check deadline/cancellation before draining more
    }

    private nonisolated static func block(_ root: URL) {
        blockedLock.lock()
        blockedRoots.insert(root.standardizedFileURL)
        blockedLock.unlock()
    }

    private nonisolated static func quarantine(_ root: URL, child: pid_t) {
        block(root)
        // Bounded best-effort reaper; a kernel-stuck child remains quarantined,
        // never grants another launch slot for this root in this app process.
        DispatchQueue.global(qos: .utility).async {
            var status: Int32 = 0
            for _ in 0..<12 {
                var info = siginfo_t()
                guard waitid(P_PID, id_t(child), &info, WEXITED | WNOHANG | WNOWAIT) == 0 else { return }
                _ = Darwin.kill(-child, SIGKILL)
                let result = waitpid(child, &status, WNOHANG)
                if result == child || result == -1 { return }
                Thread.sleep(forTimeInterval: 1)
            }
        }
    }

    private nonisolated func failure(_ reason: String) -> GitWorktreeDiscoveryError {
        GitWorktreeDiscoveryError.gitFailed(root, reason: reason)
    }
    private nonisolated static func failure(_ reason: String) -> GitWorktreeDiscoveryError {
        GitWorktreeDiscoveryError.gitFailed(URL(fileURLWithPath: "/"), reason: reason)
    }
}
