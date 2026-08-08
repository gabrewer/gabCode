import Foundation
@testable import gabCode
import XCTest

@MainActor
final class WorkspaceDescriptorTests: XCTestCase {
    func testDecodesStrictV1DescriptorAndResolvesRelativeProjectRoot() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("Project ünicode", isDirectory: true)
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        let data = Data("{\"version\":1,\"name\":\"My Project\",\"project\":{\"path\":\"Project ünicode\",\"branch\":\"trunk\"}}".utf8)

        let descriptor = try WorkspaceDescriptor.decode(data: data, from: descriptorURL)

        XCTAssertEqual(descriptor.name, "My Project")
        XCTAssertEqual(descriptor.projectRoot, project.standardizedFileURL)
        XCTAssertEqual(descriptor.originalProjectPath, "Project ünicode")
        XCTAssertEqual(descriptor.branch, "trunk")
    }

    func testDecodesAbsolutePathWithoutRewritingOriginalValue() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let descriptorURL = root.appendingPathComponent("project.gabcode-workspace")
        let absolutePath = root.appendingPathComponent("project", isDirectory: true).path
        try FileManager.default.createDirectory(atPath: absolutePath, withIntermediateDirectories: true)

        let json = "{\"version\":1,\"name\":\"Absolute\",\"project\":{\"path\":\"\(absolutePath)\",\"branch\":\"feature/demo\"}}"
        let descriptor = try WorkspaceDescriptor.decode(
            data: Data(json.utf8),
            from: descriptorURL
        )

        XCTAssertEqual(descriptor.originalProjectPath, absolutePath)
        XCTAssertEqual(descriptor.projectRoot, URL(fileURLWithPath: absolutePath).standardizedFileURL)
        XCTAssertEqual(descriptor.branch, "feature/demo")
    }

    func testRejectsMalformedUnknownUnsupportedMissingAndEmptyProjectValues() throws {
        let descriptorURL = URL(fileURLWithPath: "/tmp/project.gabcode-workspace")
        let cases: [(String, WorkspaceDescriptorError)] = [
            ("not json", .malformed),
            ("{\"version\":1,\"name\":\"x\",\"project\":{\"path\":\"x\",\"branch\":\"trunk\"},\"extra\":true}", .unknownProperty("extra")),
            ("{\"version\":2,\"name\":\"x\",\"project\":{\"path\":\"x\",\"branch\":\"trunk\"}}", .unsupportedVersion(2)),
            ("{\"version\":1,\"name\":\"\",\"project\":{\"path\":\"x\",\"branch\":\"trunk\"}}", .emptyName),
            ("{\"version\":1,\"project\":{\"path\":\"x\",\"branch\":\"trunk\"}}", .missingName),
            ("{\"version\":1,\"name\":\"x\"}", .missingProject),
            ("{\"version\":1,\"name\":\"x\",\"project\":{\"path\":\"\",\"branch\":\"trunk\"}}", .emptyProjectPath),
            ("{\"version\":1,\"name\":\"x\",\"project\":{\"path\":\"x\",\"branch\":\"\"}}", .emptyBranch)
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
        let project = root.appendingPathComponent("project", isDirectory: true)
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        let descriptorURL = root.appendingPathComponent("nested/project.gabcode-workspace")

        try WorkspaceDescriptor.write(name: "Created", projectRoot: project, branch: "trunk", to: descriptorURL)
        XCTAssertThrowsError(try WorkspaceDescriptor.write(name: "Other", projectRoot: project, branch: "trunk", to: descriptorURL)) { error in
            XCTAssertEqual(error as? WorkspaceDescriptorError, .destinationExists)
        }
        let written = try WorkspaceDescriptor.decode(data: Data(contentsOf: descriptorURL), from: descriptorURL)
        XCTAssertEqual(written.originalProjectPath, "../project")
        XCTAssertEqual(written.branch, "trunk")
    }

    private func temporaryDirectory() throws -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("gabCode workspace descriptor ünicode", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
