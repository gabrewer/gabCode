import Darwin
import Foundation

enum InstanceStartupPolicy {
    enum Action: Equatable { case openExplicit, restoreRemembered, remainEmpty }

    static func action(hasExplicitWorkspace: Bool, ownsPresence: Bool, isFirstWindow: Bool) -> Action {
        if hasExplicitWorkspace { return .openExplicit }
        return ownsPresence && isFirstWindow ? .restoreRemembered : .remainEmpty
    }
}

@MainActor
final class ProcessWindowStartupClaims {
    static let shared = ProcessWindowStartupClaims()
    private var hasClaimedFirstWindow = false

    func claimFirstWindow() -> Bool {
        guard !hasClaimedFirstWindow else { return false }
        hasClaimedFirstWindow = true
        return true
    }
}

final class MacOSInstancePresence {
    static let shared: Result<MacOSInstancePresence, Error> = Result { try MacOSInstancePresence() }

    private var descriptor: Int32 = -1
    let ownsPresence: Bool

    init(lockURL: URL = MacOSInstancePresence.defaultLockURL()) throws {
        try FileManager.default.createDirectory(at: lockURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        descriptor = open(lockURL.path, O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno)!) }
        guard fcntl(descriptor, F_SETFD, FD_CLOEXEC) == 0 else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno)!)
            Darwin.close(descriptor)
            descriptor = -1
            throw error
        }
        if flock(descriptor, LOCK_EX | LOCK_NB) == 0 {
            ownsPresence = true
        } else if errno == EWOULDBLOCK || errno == EAGAIN {
            ownsPresence = false
        } else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno)!)
            Darwin.close(descriptor)
            descriptor = -1
            throw error
        }
    }

    deinit { close() }
    func close() {
        guard descriptor >= 0 else { return }
        Darwin.close(descriptor)
        descriptor = -1
    }

    private static func defaultLockURL() -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("gabCode", isDirectory: true)
            .appendingPathComponent("instance.lock")
    }
}
