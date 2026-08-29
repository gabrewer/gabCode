import Darwin
import Foundation

struct WorkspaceDescriptor: Equatable, Sendable {
    static let supportedVersion = 1

    let version: Int
    let name: String
    let originalProjectPath: String
    let projectRoot: URL
    let mainBranch: String
    let resolvedFolder: URL

    enum WorkspaceDescriptorError: Error, Equatable {
        case malformed
        case unknownProperty(String)
        case unsupportedVersion(Int)
        case missingVersion
        case missingName
        case emptyName
        case missingProject
        case unknownProjectProperty(String)
        case missingProjectPath
        case emptyProjectPath
        case missingMainBranch
        case emptyMainBranch
        case destinationExists
        case writeFailed
    }

    static func decode(data: Data, from descriptorURL: URL) throws -> WorkspaceDescriptor {
        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        } catch {
            throw WorkspaceDescriptorError.malformed
        }
        guard let dictionary = object as? [String: Any] else {
            throw WorkspaceDescriptorError.malformed
        }

        let allowedKeys = Set(["version", "name", "project"])
        if let unknown = dictionary.keys.first(where: { !allowedKeys.contains($0) }) {
            throw WorkspaceDescriptorError.unknownProperty(unknown)
        }
        guard let versionValue = dictionary["version"] as? NSNumber else {
            throw WorkspaceDescriptorError.missingVersion
        }
        let version = versionValue.intValue
        guard versionValue.stringValue == String(version) else {
            throw WorkspaceDescriptorError.malformed
        }
        guard version == supportedVersion else {
            throw WorkspaceDescriptorError.unsupportedVersion(version)
        }
        guard let name = dictionary["name"] as? String else {
            throw WorkspaceDescriptorError.missingName
        }
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WorkspaceDescriptorError.emptyName
        }
        guard let project = dictionary["project"] as? [String: Any] else {
            throw WorkspaceDescriptorError.missingProject
        }
        let allowedProjectKeys = Set(["path", "mainBranch"])
        if let unknown = project.keys.first(where: { !allowedProjectKeys.contains($0) }) {
            throw WorkspaceDescriptorError.unknownProjectProperty(unknown)
        }
        guard let originalPath = project["path"] as? String else {
            throw WorkspaceDescriptorError.missingProjectPath
        }
        guard !originalPath.isEmpty else {
            throw WorkspaceDescriptorError.emptyProjectPath
        }
        guard let mainBranch = project["mainBranch"] as? String else {
            throw WorkspaceDescriptorError.missingMainBranch
        }
        guard !mainBranch.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WorkspaceDescriptorError.emptyMainBranch
        }

        let base = descriptorURL.deletingLastPathComponent().standardizedFileURL
        let resolved = URL(fileURLWithPath: originalPath, relativeTo: base).standardizedFileURL
        return WorkspaceDescriptor(
            version: version,
            name: name,
            originalProjectPath: originalPath,
            projectRoot: resolved,
            mainBranch: mainBranch,
            resolvedFolder: resolved
        )
    }

    static func write(
        name: String,
        projectRoot: URL,
        mainBranch: String,
        to descriptorURL: URL
    ) throws {
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WorkspaceDescriptorError.emptyName
        }
        guard !mainBranch.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WorkspaceDescriptorError.emptyMainBranch
        }
        let projectPath = projectRoot.standardizedFileURL.path
        let descriptorDirectory = descriptorURL.deletingLastPathComponent().standardizedFileURL
        let path = relativePath(from: descriptorDirectory, to: projectRoot.standardizedFileURL) ?? projectPath
        let object: [String: Any] = [
            "version": supportedVersion,
            "name": name,
            "project": ["path": path, "mainBranch": mainBranch]
        ]
        let data: Data
        do {
            data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        } catch {
            throw WorkspaceDescriptorError.writeFailed
        }

        do {
            try FileManager.default.createDirectory(at: descriptorDirectory, withIntermediateDirectories: true)
        } catch {
            throw WorkspaceDescriptorError.writeFailed
        }

        let fd = open(descriptorURL.path, O_WRONLY | O_CREAT | O_EXCL, 0o644)
        guard fd >= 0 else {
            if errno == EEXIST { throw WorkspaceDescriptorError.destinationExists }
            throw WorkspaceDescriptorError.writeFailed
        }
        do {
            let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
        } catch {
            try? FileManager.default.removeItem(at: descriptorURL)
            throw WorkspaceDescriptorError.writeFailed
        }
    }

    func resolved(to folder: URL) -> WorkspaceDescriptor {
        WorkspaceDescriptor(
            version: version,
            name: name,
            originalProjectPath: originalProjectPath,
            projectRoot: projectRoot,
            mainBranch: mainBranch,
            resolvedFolder: folder.standardizedFileURL
        )
    }

    private static func relativePath(from base: URL, to target: URL) -> String? {
        guard base.pathComponents.first == target.pathComponents.first else { return nil }
        let baseComponents = base.pathComponents
        let targetComponents = target.pathComponents
        var common = 0
        while common < baseComponents.count,
              common < targetComponents.count,
              baseComponents[common] == targetComponents[common] {
            common += 1
        }
        let upward = Array(repeating: "..", count: baseComponents.count - common)
        let downward = Array(targetComponents.dropFirst(common))
        let result = upward + downward
        return result.isEmpty ? "." : result.joined(separator: "/")
    }
}

typealias WorkspaceDescriptorError = WorkspaceDescriptor.WorkspaceDescriptorError
