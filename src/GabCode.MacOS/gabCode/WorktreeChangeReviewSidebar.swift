import SwiftUI

/// Read-only, window-local Git navigator. A button selects a diff; no source operation is exposed.
@MainActor
struct WorktreeChangeReviewSidebar: View {
    @ObservedObject var coordinator: WorktreeChangeReviewCoordinator
    let presentation: () -> TerminalWorkspacePresentation?
    @ScaledMetric(relativeTo: .subheadline) private var commitRowHeight = 30.0

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
                    LazyVStack(alignment: .leading, spacing: 8) {
                        if let snapshot = coordinator.snapshot {
                            workingChanges(snapshot)
                            Divider()
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
        VStack(alignment: .leading, spacing: 6) {
            Text("Working Changes").font(.headline)
                .accessibilityAddTraits(.isHeader)
            let staged = snapshot.working.filter { $0.group == .staged }
            let unstaged = snapshot.working.filter { $0.group == .unstaged }
            if snapshot.working.isEmpty {
                Text("Working tree clean").font(.caption).foregroundStyle(.secondary)
            }
            if !staged.isEmpty { fileGroup("Staged", files: staged) }
            if !unstaged.isEmpty { fileGroup("Unstaged & Untracked", files: unstaged) }
        }
    }

    private func fileGroup(_ title: String, files: [ReviewFile]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("\(title) (\(files.count))").font(.subheadline).fontWeight(.semibold)
            ForEach(files) { file in fileButton(file) }
        }
    }

    private func fileButton(_ file: ReviewFile) -> some View {
        Button {
            guard let presentation = presentation() else {
                coordinator.reportUnavailableSelection()
                return
            }
            coordinator.open(file: file, in: presentation)
        } label: {
            HStack(alignment: .top, spacing: 6) {
                Text(file.status.rawValue.prefix(1).uppercased())
                    .font(.caption.monospaced()).frame(width: 18)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(file.oldPath.map { "\($0) → \(file.path)" } ?? file.path)
                        .lineLimit(2).truncationMode(.middle)
                    Text("\(file.status.rawValue.capitalized) · \(file.comparison)")
                        .font(.caption2).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 3)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(file.status.rawValue.capitalized), \(file.path), \(file.comparison)\(file.oldPath.map { ", renamed from \($0)" } ?? "")")
        .accessibilityAddTraits(coordinator.selectedFile?.id == file.id ? [.isSelected] : [])
        .accessibilityValue(coordinator.selectedFile?.id == file.id ? "Selected diff" : "Not selected")
        .accessibilityIdentifier("review-file-\(file.group.rawValue)-\(file.path)")
    }

    @ViewBuilder private func history(_ snapshot: ReviewSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Branch History · since main").font(.headline)
                .accessibilityAddTraits(.isHeader)
                .padding(.bottom, 6)
            if snapshot.commits.isEmpty {
                Text("No commits ahead of the configured main branch.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ForEach(Array(snapshot.commits.enumerated()), id: \.element.sha) { index, commit in
                let expanded = coordinator.expandedCommits.contains(commit.sha)
                let isHead = commit.sha == snapshot.head
                let selected = coordinator.selectedFile?.commitSHA == commit.sha
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 4) {
                        Image(systemName: commit.parents.count > 1 ? "arrow.triangle.merge" : "circle.fill")
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(commit.parents.count > 1 ? Color.orange : Color.accentColor)
                            .frame(width: 16, height: 24)
                            .accessibilityHidden(true)
                        Button { coordinator.toggleCommit(commit.sha) } label: {
                            HStack(spacing: 5) {
                                Image(systemName: expanded ? "chevron.down" : "chevron.right")
                                    .font(.caption2).frame(width: 10).accessibilityHidden(true)
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
                        .help("\(commit.subject) · \(commit.author) · \(commit.sha.prefix(8))")
                        .accessibilityLabel("\(commit.parents.count > 1 ? "Merge commit" : "Commit"), \(isHead ? "HEAD, " : "")\(commit.sha), \(commit.subject), \(commit.author), \(commit.timestamp.formatted()), \(commit.summary)")
                        .accessibilityValue("\(expanded ? "Expanded" : "Collapsed")\(selected ? ", selected diff" : "")")
                        .accessibilityIdentifier("review-commit-\(commit.sha)")
                    }
                    .background(alignment: .leading) {
                        Rectangle().fill(.secondary.opacity(0.45)).frame(width: 1)
                            .padding(.leading, 7.5)
                            .padding(.top, index == 0 ? commitRowHeight / 2 : 0)
                            .padding(.bottom, index == snapshot.commits.count - 1 && !snapshot.hasMoreCommits ? commitRowHeight / 2 : 0)
                            .accessibilityHidden(true)
                    }
                    if expanded {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("\(commit.subject)\n\(commit.sha.prefix(8)) · \(commit.author) · \(commit.timestamp.formatted(date: .abbreviated, time: .shortened))\n\(commit.summary)")
                                .font(.caption2).foregroundStyle(.secondary)
                                .accessibilityIdentifier("review-commit-details-\(commit.sha)")
                            if let files = coordinator.commitFiles[commit.sha] {
                                if files.isEmpty { Text("No files changed").font(.caption) }
                                ForEach(files) { file in fileButton(file) }
                            } else {
                                ProgressView().controlSize(.small).accessibilityLabel("Loading commit files")
                            }
                        }
                        .padding(.leading, 20)
                        .padding(.bottom, 4)
                        .background(alignment: .leading) {
                            if index < snapshot.commits.count - 1 || snapshot.hasMoreCommits {
                                Rectangle().fill(.secondary.opacity(0.45)).frame(width: 1)
                                    .padding(.leading, 7.5)
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
