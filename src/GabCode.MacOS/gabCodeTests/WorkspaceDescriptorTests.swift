import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceDescriptorTests: XCTestCase {
    func testDecodesStrictV1DescriptorAndResolvesRelativeFolder() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Repository ünicode", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        let data = Data("{\"version\":1,\"name\":\"My Project\",\"folders\":[{\"path\":\"Repository ünicode\"}]}".utf8)

        let descriptor = try WorkspaceDescriptor.decode(data: data, from: descriptorURL)

        XCTAssertEqual(descriptor.name, "My Project")
        XCTAssertEqual(descriptor.resolvedFolder, folder.standardizedFileURL)
        XCTAssertEqual(descriptor.originalFolderPath, "Repository ünicode")
    }

    func testDecodesAbsolutePathWithoutRewritingOriginalValue() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        let absolutePath = root.appendingPathComponent("repo", isDirectory: true).path
        try FileManager.default.createDirectory(atPath: absolutePath, withIntermediateDirectories: true)

        let json = "{\"version\":1,\"name\":\"Absolute\",\"folders\":[{\"path\":\"\(absolutePath)\"}]}"
        let descriptor = try WorkspaceDescriptor.decode(
            data: Data(json.utf8),
            from: descriptorURL
        )

        XCTAssertEqual(descriptor.originalFolderPath, absolutePath)
        XCTAssertEqual(descriptor.resolvedFolder, URL(fileURLWithPath: absolutePath).standardizedFileURL)
    }

    func testRejectsMalformedUnknownUnsupportedMissingAndMultipleFolderValues() throws {
        let descriptorURL = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let cases: [(String, WorkspaceDescriptorError)] = [
            ("not json", .malformed),
            ("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"x\"}],\"extra\":true}", .unknownProperty("extra")),
            ("{\"version\":2,\"name\":\"x\",\"folders\":[{\"path\":\"x\"}]}", .unsupportedVersion(2)),
            ("{\"version\":1,\"name\":\"\",\"folders\":[{\"path\":\"x\"}]}", .emptyName),
            ("{\"version\":1,\"folders\":[{\"path\":\"x\"}]}", .missingName),
            ("{\"version\":1,\"name\":\"x\",\"folders\":[]}", .invalidFolderCount),
            ("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"a\"},{\"path\":\"b\"}]}", .invalidFolderCount),
            ("{\"version\":1,\"name\":\"x\",\"folders\":[{\"path\":\"\"}]}", .emptyFolderPath)
        ]

        for (json, expected) in cases {
            XCTAssertThrowsError(try WorkspaceDescriptor.decode(data: Data(json.utf8), from: descriptorURL)) { error in
                XCTAssertEqual(error as? WorkspaceDescriptorError, expected, json)
            }
        }
    }

    func testWritesRelativeDescriptorWithoutOverwritingExistingFile() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let descriptorURL = root.appendingPathComponent("nested/project.gabcode-workspace")

        try WorkspaceDescriptor.write(name: "Created", folder: folder, to: descriptorURL)
        XCTAssertThrowsError(try WorkspaceDescriptor.write(name: "Other", folder: folder, to: descriptorURL)) { error in
            XCTAssertEqual(error as? WorkspaceDescriptorError, .destinationExists)
        }
        let written = try WorkspaceDescriptor.decode(data: Data(contentsOf: descriptorURL), from: descriptorURL)
        XCTAssertEqual(written.originalFolderPath, "../repo")
    }

    private func temporaryDirectory() throws -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode workspace descriptor ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
