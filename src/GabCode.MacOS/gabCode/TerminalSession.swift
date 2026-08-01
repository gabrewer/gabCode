import AppKit
import Combine
import Darwin
import Foundation
import SwiftTerm

@_silgen_name("proc_listallpids")
private func proc_listallpids(
    _ buffer: UnsafeMutableRawPointer?,
    _ bufferSize: Int32
) -> Int32

enum TerminalSessionState: Equatable {
    case idle
    case starting
    case ready
    case failed
    case exited
    case closing
    case closed
}

enum TerminalSessionError: Error, Equatable {
    case inaccessibleWorkingDirectory(URL)
    case unavailableShell
    case launchFailed
    case launchCancelled
}

enum TerminalShellSelection: Equatable {
    case configured(String)
    case fallback(String)

    var path: String {
        switch self {
        case let .configured(path), let .fallback(path):
            path
        }
    }
}

enum TerminalShutdownResult: Equatable {
    case graceful
    case forced
    case alreadyStopped
    case failed
}

@MainActor
final class TerminalSession: NSObject, ObservableObject, LocalProcessTerminalViewDelegate {
    private static let defaultScrollbackLines = 2_000
    private static let forcedShutdownTimeout = Duration.seconds(2)

    private let workingDirectory: URL
    private let environment: [String: String]
    private let processGroupSignaler: (pid_t, Int32) -> Void
    private var hostedTerminalView: LocalProcessTerminalView?
    private var hostedProcessTerminationRequested = false
    private var startupInProgress = false
    private var startupStopRequested = false
    private var startupWaiters: [CheckedContinuation<Void, Never>] = []

    @Published private(set) var state: TerminalSessionState = .idle
    private(set) var processIdentifier: pid_t?
    private(set) var processGroupIdentifier: pid_t?
    private(set) var pseudoTerminalDescriptor: Int32?
    @Published private(set) var shellSelection: TerminalShellSelection?
    private var processSessionIdentifier: pid_t?

    var terminalView: NSView {
        guard let hostedTerminalView else {
            preconditionFailure("A closed terminal session no longer has a hosted view.")
        }
        return hostedTerminalView
    }

    var requiresCleanup: Bool {
        if startupInProgress {
            return true
        }

        switch state {
        case .starting, .ready, .closing:
            return true
        case .failed:
            // A failed cleanup intentionally retains its owned scope until a later stop proves
            // that no process remains. Startup failures have no session identifier and stay inert.
            return processSessionIdentifier != nil
        case .exited:
            guard let processSessionIdentifier else {
                return false
            }
            return processSessionExists(processSessionIdentifier)
        case .idle, .closed:
            return false
        }
    }

    init(
        workingDirectory: URL,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        processGroupSignaler: @escaping (pid_t, Int32) -> Void = { processGroup, signal in
            _ = kill(-processGroup, signal)
        }
    ) {
        self.workingDirectory = workingDirectory
        self.environment = environment
        self.processGroupSignaler = processGroupSignaler
        let terminalView = LocalProcessTerminalView(
            frame: CGRect(x: 0, y: 0, width: 800, height: 500)
        )
        self.hostedTerminalView = terminalView
        super.init()

        terminalView.processDelegate = self
        terminalView.configureNativeColors()
        terminalView.terminal.changeScrollback(Self.defaultScrollbackLines)
        terminalView.setAccessibilityLabel("gabCode terminal feasibility host")
    }

    func start() async throws {
        guard state == .idle || state == .failed else {
            return
        }
        guard processSessionIdentifier == nil else {
            throw TerminalSessionError.launchFailed
        }

        guard isUsableDirectory(workingDirectory) else {
            state = .failed
            throw TerminalSessionError.inaccessibleWorkingDirectory(workingDirectory)
        }

        guard let shellSelection = resolvedShell() else {
            state = .failed
            throw TerminalSessionError.unavailableShell
        }

        guard let hostedTerminalView else {
            state = .failed
            throw TerminalSessionError.launchFailed
        }

        startupInProgress = true
        startupStopRequested = false
        hostedProcessTerminationRequested = false
        defer { finishStartup() }

        state = .starting
        self.shellSelection = shellSelection
        hostedTerminalView.startProcess(
            executable: shellSelection.path,
            args: ["-l"],
            environment: childEnvironment(),
            currentDirectory: workingDirectory.path
        )

        guard let process = hostedTerminalView.process else {
            state = .failed
            throw TerminalSessionError.launchFailed
        }
        let pid = process.shellPid
        let descriptor = process.childfd
        guard process.running, pid > 0, descriptor >= 0 else {
            await abortLaunch(processIdentifier: pid)
            state = startupStopRequested ? .closing : .failed
            throw TerminalSessionError.launchFailed
        }

        // forkpty can return to the parent before the child completes login_tty/setsid.
        // Never accept the host's Unix session as terminal-owned cleanup scope.
        guard let (processGroup, processSession) = await waitForDedicatedProcessIdentity(pid) else {
            await abortLaunch(processIdentifier: pid)
            state = startupStopRequested ? .closing : .failed
            throw TerminalSessionError.launchFailed
        }

        processIdentifier = pid
        processGroupIdentifier = processGroup
        processSessionIdentifier = processSession
        pseudoTerminalDescriptor = descriptor
        guard !startupStopRequested else {
            state = .closing
            throw TerminalSessionError.launchCancelled
        }
        state = .ready
    }

    func attachTerminalView(to host: NSView) {
        guard let hostedTerminalView, hostedTerminalView.superview !== host else {
            return
        }

        hostedTerminalView.removeFromSuperview()
        hostedTerminalView.translatesAutoresizingMaskIntoConstraints = false
        host.addSubview(hostedTerminalView)
        NSLayoutConstraint.activate([
            hostedTerminalView.leadingAnchor.constraint(equalTo: host.leadingAnchor),
            hostedTerminalView.trailingAnchor.constraint(equalTo: host.trailingAnchor),
            hostedTerminalView.topAnchor.constraint(equalTo: host.topAnchor),
            hostedTerminalView.bottomAnchor.constraint(equalTo: host.bottomAnchor),
        ])
    }

    func send(_ text: String) throws {
        guard
            state == .ready,
            let hostedTerminalView,
            hostedTerminalView.process?.running == true
        else {
            throw TerminalSessionError.launchFailed
        }

        let bytes = Array(text.utf8)
        hostedTerminalView.send(source: hostedTerminalView, data: bytes[...])
    }

    @discardableResult
    func focusTerminalView() -> Bool {
        guard let hostedTerminalView, let window = hostedTerminalView.window else {
            return false
        }

        return window.makeFirstResponder(hostedTerminalView)
    }

    func stop(gracePeriod: Duration) async -> TerminalShutdownResult {
        if startupInProgress {
            startupStopRequested = true
            state = .closing
            await waitForStartupCompletion()
        }

        guard
            let processGroupIdentifier,
            let processSessionIdentifier
        else {
            releaseHostedProcess()
            state = .closed
            return .alreadyStopped
        }

        state = .closing
        signalDescendantProcessGroups(
            in: processSessionIdentifier,
            excluding: processGroupIdentifier,
            signal: SIGTERM
        )
        // Give the shell a bounded opportunity to reap terminated background jobs before
        // signaling its own group. Reparented jobs remain discoverable by Unix session ID.
        try? await ContinuousClock().sleep(for: .milliseconds(25))
        signalProcessGroupIfOwned(
            processGroupIdentifier,
            in: processSessionIdentifier,
            signal: SIGTERM
        )
        if await waitForProcessSessionExit(
            processSessionIdentifier,
            timeout: gracePeriod,
            repeating: SIGTERM
        ) {
            releaseHostedProcess()
            state = .closed
            return .graceful
        }

        signalProcessSession(processSessionIdentifier, signal: SIGKILL)
        requestHostedProcessTermination()
        if await waitForProcessSessionExit(
            processSessionIdentifier,
            timeout: Self.forcedShutdownTimeout,
            repeating: SIGKILL
        ) {
            releaseHostedProcess()
            state = .closed
            return .forced
        }

        // Keep the host and validated process identity so another close/quit cannot be
        // reported as clean and a later bounded stop can retry verification.
        state = .failed
        return .failed
    }

    func sizeChanged(source: LocalProcessTerminalView, newCols: Int, newRows: Int) {}

    func setTerminalTitle(source: LocalProcessTerminalView, title: String) {}

    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}

    func processTerminated(source: TerminalView, exitCode: Int32?) {
        if state != .closing && state != .closed {
            state = .exited
        }
    }

    private func waitForStartupCompletion() async {
        guard startupInProgress else {
            return
        }

        await withCheckedContinuation { continuation in
            if startupInProgress {
                startupWaiters.append(continuation)
            } else {
                continuation.resume()
            }
        }
    }

    private func finishStartup() {
        startupInProgress = false
        let waiters = startupWaiters
        startupWaiters.removeAll()
        waiters.forEach { $0.resume() }
    }

    private func abortLaunch(processIdentifier: pid_t) async {
        requestHostedProcessTermination()
        guard processIdentifier > 0 else {
            return
        }

        if await waitForExactProcessExit(processIdentifier, timeout: .milliseconds(250)) {
            return
        }

        _ = kill(processIdentifier, SIGKILL)
        _ = await waitForExactProcessExit(
            processIdentifier,
            timeout: Self.forcedShutdownTimeout
        )
    }

    private func waitForExactProcessExit(
        _ processIdentifier: pid_t,
        timeout: Duration
    ) async -> Bool {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)

        while true {
            var status: Int32 = 0
            let result = waitpid(processIdentifier, &status, WNOHANG)
            if result == processIdentifier || (result == -1 && errno == ECHILD) {
                return true
            }
            if kill(processIdentifier, 0) == -1 && errno == ESRCH {
                return true
            }
            guard clock.now < deadline else {
                return false
            }
            try? await clock.sleep(for: .milliseconds(10))
        }
    }

    private func waitForDedicatedProcessIdentity(
        _ processIdentifier: pid_t
    ) async -> (processGroup: pid_t, processSession: pid_t)? {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(1))

        while clock.now < deadline {
            let processGroup = getpgid(processIdentifier)
            let processSession = getsid(processIdentifier)
            if processGroup == processIdentifier && processSession == processIdentifier {
                return (processGroup, processSession)
            }
            if kill(processIdentifier, 0) == -1 && errno == ESRCH {
                return nil
            }
            try? await clock.sleep(for: .milliseconds(5))
        }

        return nil
    }

    private func isUsableDirectory(_ directory: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDirectory)
            && isDirectory.boolValue
            && access(directory.path, R_OK | X_OK) == 0
    }

    private func resolvedShell() -> TerminalShellSelection? {
        if let configuredShell = environment["SHELL"], isExecutableRegularFile(configuredShell) {
            return .configured(configuredShell)
        }

        return ["/bin/zsh", "/bin/bash", "/bin/sh"]
            .first(where: isExecutableRegularFile)
            .map(TerminalShellSelection.fallback)
    }

    private func isExecutableRegularFile(_ path: String) -> Bool {
        guard path.hasPrefix("/") else {
            return false
        }
        let values = try? URL(fileURLWithPath: path).resourceValues(forKeys: [.isRegularFileKey])
        return values?.isRegularFile == true && access(path, X_OK) == 0
    }

    private func childEnvironment() -> [String] {
        var merged = ProcessInfo.processInfo.environment
        merged.merge(environment) { _, supplied in supplied }
        merged["TERM"] = "xterm-256color"
        return merged.map { "\($0.key)=\($0.value)" }.sorted()
    }

    private func signalProcessGroup(_ processGroup: pid_t, signal: Int32) {
        processGroupSignaler(processGroup, signal)
    }

    private func signalProcessGroupIfOwned(
        _ processGroup: pid_t,
        in processSession: pid_t,
        signal: Int32
    ) {
        guard processGroups(in: processSession).contains(processGroup) else {
            return
        }
        signalProcessGroup(processGroup, signal: signal)
    }

    private func signalDescendantProcessGroups(
        in processSession: pid_t,
        excluding rootProcessGroup: pid_t,
        signal: Int32
    ) {
        for processGroup in processGroups(in: processSession) where processGroup != rootProcessGroup {
            signalProcessGroupIfOwned(processGroup, in: processSession, signal: signal)
        }
    }

    private func signalProcessSession(_ processSession: pid_t, signal: Int32) {
        for processGroup in processGroups(in: processSession) {
            signalProcessGroupIfOwned(processGroup, in: processSession, signal: signal)
        }
    }

    private func waitForProcessSessionExit(
        _ processSession: pid_t,
        timeout: Duration,
        repeating signal: Int32
    ) async -> Bool {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)

        while true {
            let rootProcessExited = reapRootProcessIfNeeded()
            let ownedSessionExists = processSessionExists(processSession)
            if rootProcessExited && !ownedSessionExists {
                return true
            }
            guard clock.now < deadline else {
                return false
            }
            signalProcessSession(processSession, signal: signal)
            try? await clock.sleep(for: .milliseconds(10))
        }
    }

    private func reapRootProcessIfNeeded() -> Bool {
        guard let processIdentifier else {
            return true
        }
        var status: Int32 = 0
        let result = waitpid(processIdentifier, &status, WNOHANG)
        if result == processIdentifier {
            return true
        }
        if result == -1 {
            return errno == ECHILD
        }
        return false
    }

    private func processSessionExists(_ processSession: pid_t) -> Bool {
        processIdentifiers(in: processSession).isEmpty == false
    }

    private func processGroups(in processSession: pid_t) -> Set<pid_t> {
        Set(processIdentifiers(in: processSession).compactMap { processIdentifier in
            let processGroup = getpgid(processIdentifier)
            return processGroup > 0 ? processGroup : nil
        })
    }

    private func processIdentifiers(in processSession: pid_t) -> [pid_t] {
        allProcessIdentifiers().filter { processIdentifier in
            getsid(processIdentifier) == processSession
        }
    }

    private func allProcessIdentifiers() -> [pid_t] {
        let reportedProcessCount = max(Int(proc_listallpids(nil, 0)), 0)
        var capacity = max(reportedProcessCount + 256, 4_096)

        while true {
            var processes = Array(repeating: pid_t(0), count: capacity)
            let bufferSize = Int32(processes.count * MemoryLayout<pid_t>.stride)
            let receivedCount = processes.withUnsafeMutableBytes {
                proc_listallpids($0.baseAddress, bufferSize)
            }
            guard receivedCount > 0 else {
                return []
            }

            if receivedCount < capacity {
                return Array(processes.prefix(Int(receivedCount))).filter { $0 > 0 }
            }
            capacity *= 2
        }
    }

    private func requestHostedProcessTermination() {
        guard !hostedProcessTerminationRequested else {
            return
        }
        hostedProcessTerminationRequested = true
        hostedTerminalView?.terminate()
    }

    private func releaseHostedProcess() {
        hostedTerminalView?.removeFromSuperview()
        hostedTerminalView = nil
        clearIdentity()
    }

    private func clearIdentity() {
        processIdentifier = nil
        processGroupIdentifier = nil
        processSessionIdentifier = nil
        pseudoTerminalDescriptor = nil
    }
}
