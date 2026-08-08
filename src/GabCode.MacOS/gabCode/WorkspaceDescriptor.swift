import Darwin
import Foundation

struct WorkspaceDescriptor: Equatable, Sendable {
    static let supportedVersion = 1

    let version: Int
    let name: String
    let originalFolderPath: String
    let resolvedFolder: URL

    enum WorkspaceDescriptorError: Error, Equatable {
        case malformed
        case unknownProperty(String)
        case unsupportedVersion(Int)
        case missingVersion
        case missingName
        case emptyName
        case invalidFolderCount
        case missingFolderPath
        case emptyFolderPath
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

        let allowedKeys = Set(["version", "name", "folders"])
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
        guard let folders = dictionary["folders"] as? [[String: Any]], folders.count == 1 else {
            throw WorkspaceDescriptorError.invalidFolderCount
        }
        let folder = folders[0]
        let folderKeys = Set(["path"])
        if let unknown = folder.keys.first(where: { !folderKeys.contains($0) }) {
            throw WorkspaceDescriptorError.unknownProperty(unknown)
        }
        guard let originalPath = folder["path"] as? String else {
            throw WorkspaceDescriptorError.missingFolderPath
        }
        guard !originalPath.isEmpty else {
            throw WorkspaceDescriptorError.emptyFolderPath
        }

        let base = descriptorURL.deletingLastPathComponent().standardizedFileURL
        let resolved = URL(fileURLWithPath: originalPath, relativeTo: base).standardizedFileURL
        return WorkspaceDescriptor(
            version: version,
            name: name,
            originalFolderPath: originalPath,
            resolvedFolder: resolved
        )
    }

    static func write(name: String, folder: URL, to descriptorURL: URL) throws {
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WorkspaceDescriptorError.emptyName
        }
        let folderPath = folder.standardizedFileURL.path
        let descriptorDirectory = descriptorURL.deletingLastPathComponent().standardizedFileURL
        let path = relativePath(from: descriptorDirectory, to: folder.standardizedFileURL)
            ?? folderPath
        let object: [String: Any] = [
            "version": supportedVersion,
            "name": name,
            "folders": [["path": path]]
        ]
        let data: Data
        do {
            data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        } catch {
            throw WorkspaceDescriptorError.writeFailed
        }

        do {
            try FileManager.default.createDirectory(
                at: descriptorDirectory,
                withIntermediateDirectories: true
            )
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

    private static func relativePath(from base: URL, to target: URL) -> String? {
        guard base.pathComponents.first == target.pathComponents.first else {
            return nil
        }
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
