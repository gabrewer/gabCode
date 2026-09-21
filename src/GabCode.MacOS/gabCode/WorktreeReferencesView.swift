import AppKit
import SwiftUI

@MainActor
struct WorktreeReferencesBar: View {
    let references: WorktreeReferences
    let onChooseMarkdown: () -> Void
    let onReplaceMarkdown: () -> Void
    let onOpenMarkdown: () -> Void
    let onRemoveMarkdown: () -> Void
    let onAddIssue: () -> Void
    let onReplaceIssue: () -> Void
    let onOpenIssue: () -> Void
    let onRemoveIssue: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Text("References")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            Divider().frame(height: 24)
            markdownSlot
            Divider().frame(height: 24)
            issueSlot
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(.quaternary.opacity(0.35))
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("worktree-references-bar")
    }

    private var markdownSlot: some View {
        Group {
            if let url = references.markdownURL {
                let missing = !WorktreeReferenceLauncher.isExistingMarkdownFile(url)
                Menu {
                    Button("Open") { onOpenMarkdown() }
                        .disabled(missing)
                    Button(missing ? "Locate…" : "Replace") { onReplaceMarkdown() }
                    Button("Remove", role: .destructive) { onRemoveMarkdown() }
                } label: {
                    referenceLabel(
                        text: url.lastPathComponent,
                        icon: "doc.text",
                        status: missing ? "Missing" : "Available"
                    )
                }
                .menuStyle(.borderlessButton)
                .disabled(false)
                .accessibilityLabel("Markdown reference")
                .accessibilityValue("\(url.lastPathComponent), \(missing ? "Missing" : "Available")")
            } else {
                Menu {
                    Button("Choose Markdown…") { onChooseMarkdown() }
                } label: {
                    referenceLabel(text: "Markdown", icon: "doc.text", status: "Not assigned")
                }
                .menuStyle(.borderlessButton)
                .accessibilityLabel("Markdown reference")
                .accessibilityValue("Not assigned")
            }
        }
        .accessibilityIdentifier("markdown-reference")
    }

    private var issueSlot: some View {
        Group {
            if let issue = references.issue {
                Menu {
                    Button("Open") { onOpenIssue() }
                    Button("Replace") { onReplaceIssue() }
                    Button("Remove", role: .destructive) { onRemoveIssue() }
                } label: {
                    referenceLabel(text: issue.displayIdentity, icon: "link", status: nil)
                }
                .menuStyle(.borderlessButton)
                .accessibilityLabel("GitHub issue reference")
                .accessibilityValue(issue.displayIdentity)
            } else {
                Menu {
                    Button("Add issue…") { onAddIssue() }
                } label: {
                    referenceLabel(text: "GitHub issue", icon: "link", status: "Not assigned")
                }
                .menuStyle(.borderlessButton)
                .accessibilityLabel("GitHub issue reference")
                .accessibilityValue("Not assigned")
            }
        }
        .accessibilityIdentifier("issue-reference")
    }

    private func referenceLabel(text: String, icon: String, status: String?) -> some View {
        HStack(spacing: 5) {
            Label(text, systemImage: icon)
                .lineLimit(1)
            if let status {
                Text(status)
                    .foregroundStyle(status == "Missing" ? .red : .secondary)
            }
            Image(systemName: "chevron.down")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .contentShape(Rectangle())
    }
}

@MainActor
struct GitHubIssueEntrySheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    let onAdd: (GitHubIssueReference) -> Void

    private var parsed: GitHubIssueReference? { GitHubIssueReference(urlString: text) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add GitHub issue").font(.title2)
            TextField("https://github.com/owner/repository/issues/42", text: $text)
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("GitHub issue URL")
            if !text.isEmpty && parsed == nil {
                Text("Enter a canonical HTTPS GitHub issue URL.")
                    .foregroundStyle(.red)
                    .accessibilityLabel("Invalid GitHub issue URL")
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    guard let parsed else { return }
                    onAdd(parsed)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(parsed == nil)
            }
        }
        .padding(22)
        .frame(width: 480)
    }
}

@MainActor
struct WorktreeReferenceLauncher {
    let isRegularMarkdownFile: (URL) -> Bool
    let codeApplicationURL: () -> URL?
    let openInApplication: ([URL], URL, @escaping (Error?) -> Void) -> Void
    let openURL: (URL) -> Bool

    static let system = WorktreeReferenceLauncher(
        isRegularMarkdownFile: isExistingMarkdownFile,
        codeApplicationURL: { NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode") },
        openInApplication: { urls, application, completion in
            NSWorkspace.shared.open(urls, withApplicationAt: application, configuration: NSWorkspace.OpenConfiguration()) { _, error in
                completion(error)
            }
        },
        openURL: { NSWorkspace.shared.open($0) }
    )

    static func isExistingMarkdownFile(_ url: URL) -> Bool {
        let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
        return values?.isRegularFile == true && ["md", "markdown"].contains(url.pathExtension.lowercased())
    }

    func openMarkdown(_ url: URL, completion: @escaping (String?) -> Void) {
        guard isRegularMarkdownFile(url) else {
            completion("The Markdown file is missing or is no longer a Markdown file: \(url.path)")
            return
        }
        guard let application = codeApplicationURL() else {
            completion("Visual Studio Code could not be found.")
            return
        }
        openInApplication([url], application) { error in
            completion(error.map { "Visual Studio Code could not open the Markdown file: \($0.localizedDescription)" })
        }
    }

    func openIssue(_ issue: GitHubIssueReference) -> String? {
        openURL(issue.url) ? nil : "The default browser could not open the issue."
    }
}
