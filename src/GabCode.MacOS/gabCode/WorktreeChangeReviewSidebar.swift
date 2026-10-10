import SwiftUI

/// Read-only, window-local Git navigator. A button selects a diff; no source operation is exposed.
@MainActor
struct WorktreeChangeReviewSidebar: View {
    @ObservedObject var coordinator: WorktreeChangeReviewCoordinator
    let presentation: () -> TerminalWorkspacePresentation?

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
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
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
        VStack(alignment: .leading, spacing: 6) {
            Text("Branch History · since main").font(.headline)
                .accessibilityAddTraits(.isHeader)
            if snapshot.commits.isEmpty {
                Text("No commits ahead of the configured main branch.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ForEach(snapshot.commits) { commit in
                VStack(alignment: .leading, spacing: 4) {
                    Button { coordinator.toggleCommit(commit.sha) } label: {
                        HStack(alignment: .top, spacing: 6) {
                            Image(systemName: coordinator.expandedCommits.contains(commit.sha) ? "chevron.down" : "chevron.right")
                                .frame(width: 12).accessibilityHidden(true)
                            Image(systemName: commit.parents.count > 1 ? "arrow.triangle.merge" : "circle.fill")
                                .foregroundStyle(.secondary).frame(width: 16)
                                .accessibilityHidden(true)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(commit.subject).fontWeight(.medium).lineLimit(2)
                                Text("\(commit.sha.prefix(8)) · \(commit.author) · \(commit.timestamp.formatted(date: .abbreviated, time: .shortened))")
                                    .font(.caption2).foregroundStyle(.secondary)
                                Text(commit.summary).font(.caption2).foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 0)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(commit.parents.count > 1 ? "Merge commit" : "Commit"), \(commit.sha), \(commit.subject), \(commit.author), \(commit.timestamp.formatted()), \(commit.summary)")
                    .accessibilityValue(coordinator.expandedCommits.contains(commit.sha) ? "Expanded" : "Collapsed")
                    .accessibilityIdentifier("review-commit-\(commit.sha)")
                    if coordinator.expandedCommits.contains(commit.sha) {
                        if let files = coordinator.commitFiles[commit.sha] {
                            if files.isEmpty { Text("No files changed").font(.caption) }
                            ForEach(files) { file in fileButton(file) }
                        } else {
                            ProgressView().controlSize(.small).accessibilityLabel("Loading commit files")
                        }
                    }
                }
                .padding(.leading, 4)
                Divider()
            }
            if snapshot.hasMoreCommits {
                Button("Load More Commits") { coordinator.loadMore() }
                    .disabled(coordinator.isLoadingMore)
                    .accessibilityIdentifier("review-load-more")
            }
        }
    }
}
