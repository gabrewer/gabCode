import Combine
import Darwin
import Foundation

struct WorkspaceProjectTitle {
    static func value(name: String, folder: URL) -> String {
        "\(name) — \(folder.lastPathComponent) — gabCode"
    }
}

enum WorkspaceOpenError: Error, Equatable {
    case cancelled
    case unreadableDescriptor(URL)
    case malformedDescriptor(URL)
    case invalidFolder(URL)
    case notGitRepository(URL, reason: String)
    case gitUnavailable(URL)
    case gitFailed(URL, reason: String)
    case descriptorWriteFailed(URL)

    var message: String {
        switch self {
        case .cancelled: "Workspace opening was cancelled."
        case let .unreadableDescriptor(url): "Could not read workspace file: \(url.path)"
        case let .malformedDescriptor(url): "The workspace file is malformed or unsupported: \(url.path)"
        case let .invalidFolder(url): "The workspace folder is missing or inaccessible: \(url.path)"
        case let .notGitRepository(url, reason): "This folder is not in a Git repository: \(url.path)\n\(reason)"
        case let .gitUnavailable(url): "Git could not be found at \(url.path)."
        case let .gitFailed(url, reason): "Git validation failed for \(url.path).\n\(reason)"
        case let .descriptorWriteFailed(url): "Could not write workspace file: \(url.path)"
        }
    }
}

enum WorkspaceProjectSurfaceState: Equatable {
    case empty
    case loading
    case ready
    case recovery(WorkspaceOpenError)
}

@MainActor
final class WorkspaceProjectController: ObservableObject {
    @Published private(set) var state: WorkspaceProjectSurfaceState = .empty
    @Published private(set) var activeDescriptor: WorkspaceDescriptor?

    let preference: WorkspacePreference
    private let gitValidator: GitRepositoryValidator

    init(
        defaults: UserDefaults = .standard,
        gitValidator: GitRepositoryValidator = GitRepositoryValidator()
    ) {
        preference = WorkspacePreference(defaults: defaults)
        self.gitValidator = gitValidator
    }

    var windowTitle: String {
        guard let activeDescriptor else { return "gabCode" }
        return WorkspaceProjectTitle.value(name: activeDescriptor.name, folder: activeDescriptor.resolvedFolder)
    }

    func reopenRememberedWorkspace() async -> Bool {
        guard let url = preference.lastWorkspaceURL else {
            state = .empty
            return false
        }
        let opened = await openWorkspace(at: url)
        if !opened {
            preference.lastWorkspaceURL = nil
        }
        return opened
    }

    func openWorkspace(at url: URL) async -> Bool {
        state = .loading
        do {
            let descriptor = try loadDescriptor(at: url)
            try validateFolder(descriptor.resolvedFolder)
            let validation = await gitValidator.validate(folder: descriptor.resolvedFolder)
            try validateGitResult(validation, folder: descriptor.resolvedFolder)
            activeDescriptor = descriptor
            preference.lastWorkspaceURL = url
            state = .ready
            return true
        } catch let error as WorkspaceOpenError {
            state = .recovery(error)
            return false
        } catch {
            state = .recovery(.unreadableDescriptor(url))
            return false
        }
    }

    func createWorkspace(
        name: String,
        folder: URL,
        descriptorURL: URL
    ) async -> Bool {
        state = .loading
        do {
            try validateFolder(folder)
            let validation = await gitValidator.validate(folder: folder)
            try validateGitResult(validation, folder: folder)
            try WorkspaceDescriptor.write(name: name, folder: folder, to: descriptorURL)
            return await openWorkspace(at: descriptorURL)
        } catch let error as WorkspaceOpenError {
            state = .recovery(error)
            return false
        } catch {
            state = .recovery(.descriptorWriteFailed(descriptorURL))
            return false
        }
    }

    private func loadDescriptor(at url: URL) throws -> WorkspaceDescriptor {
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            throw WorkspaceOpenError.unreadableDescriptor(url)
        }
        do {
            return try WorkspaceDescriptor.decode(data: data, from: url)
        } catch {
            throw WorkspaceOpenError.malformedDescriptor(url)
        }
    }

    private func validateFolder(_ folder: URL) throws {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory),
              isDirectory.boolValue,
              access(folder.path, R_OK | X_OK) == 0
        else {
            throw WorkspaceOpenError.invalidFolder(folder)
        }
    }

    private func validateGitResult(
        _ result: GitRepositoryValidationResult,
        folder: URL
    ) throws {
        switch result {
        case .valid:
            return
        case let .gitUnavailable(url):
            throw WorkspaceOpenError.gitUnavailable(url)
        case let .notRepository(_, stderr):
            throw WorkspaceOpenError.notGitRepository(folder, reason: stderr)
        case let .failed(_, _, stderr):
            throw WorkspaceOpenError.gitFailed(folder, reason: stderr)
        case .timedOut:
            throw WorkspaceOpenError.gitFailed(folder, reason: "Git validation timed out.")
        case .cancelled:
            throw WorkspaceOpenError.cancelled
        }
    }
}
