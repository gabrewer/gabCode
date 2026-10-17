import SwiftUI

/// Read-only, window-local Git navigator. A button selects a diff; no source operation is exposed.
@MainActor
struct WorktreeChangeReviewSidebar: View {
    @ObservedObject var coordinator: WorktreeChangeReviewCoordinator
    let presentation: () -> TerminalWorkspacePresentation?
    @FocusState private var focusedCommit: String?
    @State private var workingExpanded = true
    @State private var stagedExpanded = true
    @State private var unstagedExpanded = true
    @State private var historyExpanded = true
    @ScaledMetric(relativeTo: .subheadline) private var commitRowHeight = 26.0

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Changes & History").font(.headline)
                Spacer()
                if coordinator.isLoading { ProgressView().controlSize(.small).accessibilityLabel("Refreshing change review") }
                Button { coordinator.requestRefresh() } label: { Image(systemName: "arrow.clockwise") }
                    .accessibilityLabel("Refresh Changes and History")
                    .accessibilityIdentifier("refresh-change-review")
                    .disabled(coordinator.selectedWorktree == nil)
            }
            .padding(10)
            if let error = coordinator.errorMessage {
                Text("\(coordinator.isStale ? "Stale Git data — " : "Git review unavailable — ")\(error)")
                    .font(.caption).foregroundStyle(.red).padding(8)
                    .accessibilityIdentifier("review-error")
                    .background(ReadOnlyDiffAnnouncement(message: "Git review unavailable. \(error)"))
            }
            if let warning = coordinator.observerWarning {
                Text(warning).font(.caption).foregroundStyle(.orange).padding(8)
                    .accessibilityIdentifier("review-observer-warning")
                    .background(ReadOnlyDiffAnnouncement(message: warning))
            }
            if let notice = coordinator.notice {
                Text(notice).font(.caption).padding(8)
                    .accessibilityIdentifier("review-notice")
                    .background(ReadOnlyDiffAnnouncement(message: notice))
            }
            GeometryReader { available in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        if let snapshot = coordinator.snapshot {
                            workingChanges(snapshot)
                            history(snapshot)
                        } else if coordinator.isLoading {
                            Text("Loading Git changes…").foregroundStyle(.secondary)
                        } else {
                            Text(coordinator.selectedWorktree == nil ? "Select a worktree." : "Review unavailable. Use Refresh to retry.")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .frame(width: max(0, available.size.width - 20), alignment: .leading)
                    .padding(10)
                }
            }
        }
        .frame(minWidth: WorkspaceWindowLayout.reviewMinimumWidth,
               idealWidth: WorkspaceWindowLayout.reviewIdealWidth,
               maxWidth: WorkspaceWindowLayout.reviewIdealWidth,
               maxHeight: .infinity)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("change-review-sidebar")
    }

    @ViewBuilder private func workingChanges(_ snapshot: ReviewSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            disclosure("Working changes", expanded: $workingExpanded, identifier: "review-group-working")
                .accessibilityAddTraits(.isHeader)
            if workingExpanded {
                if snapshot.working.isEmpty {
                    Text("Working tree clean").font(.caption).foregroundStyle(.secondary)
                        .padding(.leading, 20)
                }
                let staged = snapshot.working.filter { $0.group == .staged }
                let unstaged = snapshot.working.filter { $0.group == .unstaged }
                fileGroup("Staged", files: staged, expanded: $stagedExpanded, identifier: "staged")
                fileGroup("Unstaged", files: unstaged, expanded: $unstagedExpanded, identifier: "unstaged")
            }
        }
    }

    private func disclosure(_ title: String, expanded: Binding<Bool>, identifier: String) -> some View {
        Button { expanded.wrappedValue.toggle() } label: {
            HStack(spacing: 5) {
                Image(systemName: expanded.wrappedValue ? "chevron.down" : "chevron.right")
                    .font(.caption2).frame(width: 10).accessibilityHidden(true)
                Text(title).font(.subheadline).fontWeight(.semibold).lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 25, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .accessibilityValue(expanded.wrappedValue ? "Expanded" : "Collapsed")
        .accessibilityIdentifier(identifier)
    }

    private func fileGroup(_ title: String, files: [ReviewFile], expanded: Binding<Bool>, identifier: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            disclosure("\(title) (\(files.count))", expanded: expanded, identifier: "review-group-\(identifier)")
            if expanded.wrappedValue {
                if files.isEmpty {
                    Text("No \(identifier) files.").font(.caption).foregroundStyle(.secondary)
                        .padding(.leading, 20)
                } else {
                    ForEach(files) { file in fileButton(file).padding(.leading, 16) }
                }
            }
        }
        .padding(.leading, 12)
    }

    private func statusBadge(for file: ReviewFile) -> (glyph: String, accessibilityName: String) {
        let state: String
        switch file.status {
        case .added: state = "Added"
        case .modified, .changed: state = "Modified"
        case .deleted: state = "Deleted"
        case .renamed: state = "Renamed"
        case .untracked: state = "Untracked"
        case .conflicted: state = "Conflicted"
        case .copied: state = "Copied"
        }
        let group = file.group == .staged ? "Staged" : file.group == .unstaged ? "Unstaged" : "Commit"
        let glyph: String
        switch file.status {
        case .added, .untracked: glyph = file.status == .untracked ? "U" : "A"
        case .modified, .changed: glyph = "M"
        case .deleted: glyph = "D"
        case .renamed: glyph = "R"
        case .conflicted: glyph = "!"
        case .copied: glyph = "C"
        }
        return (glyph, "\(state), \(group)")
    }

    private func fileButton(_ file: ReviewFile) -> some View {
        let comparison = file.group == .commit
            ? (file.comparison.hasPrefix("empty tree →") ? "Root commit · empty tree → commit" : "First parent → commit")
            : file.comparison
        let badge = statusBadge(for: file)
        return Button {
            guard let presentation = presentation() else {
                coordinator.reportUnavailableSelection()
                return
            }
            coordinator.open(file: file, in: presentation)
        } label: {
            HStack(spacing: 5) {
                Text(badge.glyph)
                    .font(.caption.monospaced().weight(.semibold)).frame(width: 18)
                    .foregroundStyle(file.status == .conflicted ? Color.orange : Color.primary)
                    .accessibilityLabel(badge.accessibilityName)
                Text(file.oldPath.map { "\($0) → \(file.path)" } ?? file.path)
                    .font(.caption)
                    .lineLimit(1).truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .frame(minHeight: 24)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(badge.accessibilityName), \(file.path)\(comparison.isEmpty ? "" : ", \(comparison)")\(file.oldPath.map { ", renamed from \($0)" } ?? "")")
        .accessibilityAddTraits(coordinator.selectedFile?.id == file.id ? [.isSelected] : [])
        .accessibilityValue(coordinator.selectedFile?.id == file.id ? "Selected diff" : "Not selected")
        .help("\(badge.accessibilityName) · \(file.oldPath.map { "\($0) → " } ?? "")\(file.path) · \(comparison)")
        .accessibilityIdentifier("review-file-\(file.group.rawValue)-\(file.path)")
    }

    @ViewBuilder private func history(_ snapshot: ReviewSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            disclosure("Commits ahead of main (\(snapshot.commits.count)\(snapshot.hasMoreCommits ? "+" : ""))", expanded: $historyExpanded, identifier: "review-group-history")
                .accessibilityAddTraits(.isHeader)
            if historyExpanded {
            if snapshot.commits.isEmpty {
                Text("No commits unique to this branch.")
                    .font(.caption).foregroundStyle(.secondary).padding(.leading, 20)
            }
            ForEach(Array(snapshot.commits.enumerated()), id: \.element.sha) { index, commit in
                let expanded = coordinator.expandedCommits.contains(commit.sha)
                let isHead = commit.sha == snapshot.head
                let selected = coordinator.selectedFile?.commitSHA == commit.sha
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 4) {
                        Button {
                            coordinator.toggleCommit(commit.sha)
                            focusedCommit = commit.sha
                        } label: {
                            HStack(spacing: 5) {
                                Image(systemName: expanded ? "chevron.down" : "chevron.right")
                                    .font(.caption2).frame(width: 10).accessibilityHidden(true)
                                Image(systemName: commit.parents.count > 1 ? "arrow.triangle.merge" : "circle.fill")
                                    .font(.system(size: 9, weight: .semibold))
                                    .foregroundStyle(commit.parents.count > 1 ? Color.orange : Color.accentColor)
                                    .frame(width: 16)
                                    .accessibilityHidden(true)
                                if isHead {
                                    Text("HEAD")
                                        .font(.system(size: 9, weight: .semibold))
                                        .foregroundStyle(.secondary)
                                        .frame(width: 28)
                                        .layoutPriority(1)
                                        .accessibilityHidden(true)
                                }
                                Text(commit.subject)
                                    .font(.subheadline).fontWeight(selected ? .semibold : .regular)
                                    .lineLimit(1).truncationMode(.tail)
                                    .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
                                    .layoutPriority(-1)
                                Text(commit.author)
                                    .font(.caption2).foregroundStyle(.secondary)
                                    .lineLimit(1).truncationMode(.tail)
                                    .frame(width: 56, alignment: .trailing)
                                    .layoutPriority(1)
                            }
                            .padding(.trailing, 12)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .frame(height: commitRowHeight)
                            .contentShape(Rectangle())
                            .background(selected ? Color.accentColor.opacity(0.14) : Color.clear)
                        }
                        .buttonStyle(.plain)
                        .focused($focusedCommit, equals: commit.sha)
                        .help("\(commit.sha)\n\(commit.subject)\n\(commit.author) · \(commit.timestamp.formatted())\n\(commit.summary)")
                        .contextMenu {
                            Button("Copy Commit Hash") { coordinator.copyCommitHash(commit.sha) }
                                .accessibilityIdentifier("copy-commit-hash-\(commit.sha)")
                        }
                        .accessibilityAction(named: Text("Copy Commit Hash")) {
                            coordinator.copyCommitHash(commit.sha)
                        }
                        .accessibilityLabel("\(commit.parents.count > 1 ? "Merge commit" : "Commit"), \(isHead ? "HEAD, " : "")\(commit.sha), \(commit.subject), \(commit.author), \(commit.timestamp.formatted()), \(commit.summary)")
                        .accessibilityValue("\(expanded ? "Expanded" : "Collapsed")\(selected ? ", selected diff" : "")")
                        .accessibilityIdentifier("review-commit-\(commit.sha)")
                        if expanded {
                            Button("Copy Commit Hash", systemImage: "doc.on.doc") {
                                coordinator.copyCommitHash(commit.sha)
                            }
                            .labelStyle(.iconOnly)
                            .buttonStyle(.borderless)
                            .help("Copy Commit Hash")
                            .accessibilityLabel("Copy Commit Hash")
                            .accessibilityIdentifier("copy-commit-hash-button-\(commit.sha)")
                        }
                    }
                    .background(alignment: .leading) {
                        Rectangle().fill(.secondary.opacity(0.45)).frame(width: 1)
                            .padding(.leading, 22.5)
                            .padding(.top, index == 0 ? commitRowHeight / 2 : 0)
                            .padding(.bottom, index == snapshot.commits.count - 1 && !snapshot.hasMoreCommits ? commitRowHeight / 2 : 0)
                            .accessibilityHidden(true)
                    }
                    if expanded {
                        VStack(alignment: .leading, spacing: 2) {
                            if let files = coordinator.commitFiles[commit.sha] {
                                if files.isEmpty { Text("No files changed").font(.caption) }
                                ForEach(files) { file in fileButton(file) }
                            } else {
                                ProgressView().controlSize(.small).accessibilityLabel("Loading commit files")
                            }
                        }
                        .padding(.leading, 20)
                        .background(alignment: .leading) {
                            if index < snapshot.commits.count - 1 || snapshot.hasMoreCommits {
                                Rectangle().fill(.secondary.opacity(0.45)).frame(width: 1)
                                    .padding(.leading, 22.5)
                                    .accessibilityHidden(true)
                            }
                        }
                    }
                }
            }
            if snapshot.hasMoreCommits {
                Button("Load More Commits") { coordinator.loadMore() }
                    .disabled(coordinator.isLoadingMore)
                    .accessibilityIdentifier("review-load-more")
            }
            }
        }
    }
}
