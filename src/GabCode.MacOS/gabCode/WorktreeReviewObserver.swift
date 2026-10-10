import CoreServices
import Foundation

/// FSEvents are invalidation hints only. Git queries, focus, and periodic polling reconcile missed events.
final class WorktreeReviewObserver: @unchecked Sendable {
    private let worktree: URL
    private let onChange: @Sendable (Bool) -> Void
    private let lock = NSLock()
    private var stream: FSEventStreamRef?
    private let queue = DispatchQueue(label: "gabcode.review.fsevents", qos: .utility)

    init(worktree: URL, onChange: @escaping @Sendable (Bool) -> Void) {
        self.worktree = worktree.standardizedFileURL
        self.onChange = onChange
    }

    func start() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard stream == nil else { return true }
        let gitPointer = worktree.appendingPathComponent(".git")
        let gitMetadata: URL
        if let path = try? String(contentsOf: gitPointer, encoding: .utf8), path.hasPrefix("gitdir:") {
            let name = path.dropFirst("gitdir:".count).trimmingCharacters(in: .whitespacesAndNewlines)
            gitMetadata = URL(fileURLWithPath: name, relativeTo: worktree).standardizedFileURL
        } else { gitMetadata = gitPointer }
        let commonPointer = gitMetadata.appendingPathComponent("commondir")
        let commonDirectory: URL
        if let name = try? String(contentsOf: commonPointer, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines) {
            commonDirectory = URL(fileURLWithPath: name, relativeTo: gitMetadata).standardizedFileURL
        } else { commonDirectory = gitMetadata }
        let paths = Array(Set([worktree.path, gitMetadata.path, commonDirectory.path]))
            .filter { FileManager.default.fileExists(atPath: $0) }
        guard !paths.isEmpty else { return false }
        // The stream owns the callback target until invalidation (including callbacks already queued).
        // Owners must call stop(), which breaks the stream → observer → stream cycle.
        var context = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: { pointer in
                guard let pointer else { return nil }
                return UnsafeRawPointer(Unmanaged<WorktreeReviewObserver>.fromOpaque(pointer).retain().toOpaque())
            },
            release: { pointer in
                guard let pointer else { return }
                Unmanaged<WorktreeReviewObserver>.fromOpaque(pointer).release()
            },
            copyDescription: nil
        )
        let callback: FSEventStreamCallback = { _, info, count, _, flags, _ in
            guard let info else { return }
            let observer = Unmanaged<WorktreeReviewObserver>.fromOpaque(info).takeUnretainedValue()
            var overflow = false
            for i in 0..<count {
                if flags[i] & FSEventStreamEventFlags(kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagUserDropped | kFSEventStreamEventFlagKernelDropped | kFSEventStreamEventFlagRootChanged) != 0 {
                    overflow = true
                }
            }
            observer.onChange(overflow)
        }
        guard let stream = FSEventStreamCreate(nil, callback, &context, paths as CFArray, FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.2,
            FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagWatchRoot | kFSEventStreamCreateFlagNoDefer)) else { return false }
        FSEventStreamSetDispatchQueue(stream, queue)
        guard FSEventStreamStart(stream) else {
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
            return false
        }
        self.stream = stream
        return true
    }

    func stop() {
        lock.lock()
        defer { lock.unlock() }
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
        self.stream = nil
    }

    deinit { stop() }
}
