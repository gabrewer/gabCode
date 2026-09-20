# gabCode Worktree Change Review Sidebar PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-09-20 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related worktree navigation | `Documentation/design/2026-08-10-worktree-navigation-prd.md` |

## Product Name & One-Liner

**gabCode Worktree Change Review Sidebar** — a right-side, read-only review surface that shows the selected worktree's uncommitted changes and branch-unique commits, down to each changed file and its diff.

## Problem & Audience

A developer running an LLM coding tool in several Git worktrees needs to quickly review what changed in the current worktree. Terminal output is not a reliable review surface, and inherited main-branch history obscures the work being evaluated. The developer needs to see both edits that have not yet been committed and commits that belong to the worktree branch, then inspect an individual file's exact diff without leaving gabCode.

The initial audience is the individual gabCode developer who uses Git worktrees and CLI coding tools. The sidebar reports Git-visible worktree state regardless of which tool or person made it; gabCode does not inspect, identify, or attribute LLM sessions or terminal activity.

## Core Features

### 1. Selected-worktree review sidebar — Must-have

gabCode provides a fixed, read-only sidebar on the right for the currently selected worktree. It is distinct from the worktree-navigation sidebar on the left and from the main content area between them. It is a focused review surface, replacing its contents when the selected worktree changes; it does not present a combined repository-wide history.

The sidebar contains, in order:

1. **Working changes**, when present.
2. **Branch commits**, containing commits reachable from the selected worktree's `HEAD` that are not reachable from the workspace's configured local `project.mainBranch`, newest first.

An empty section communicates its empty state explicitly. The sidebar is keyboard navigable and exposes section, commit, file, Git-status, expansion, and loading/error state to native accessibility APIs.

### 2. Working changes, split by index state — Must-have

**Working changes** expands to separate **Staged** and **Unstaged** flat file lists. Staged contains index changes; Unstaged contains tracked worktree changes and untracked files. Each list shows repository-relative paths and Git change badges for added, modified, deleted, renamed, untracked, and conflicted files. A file with both index and worktree changes appears in both relevant lists. Ignored files are not shown.

Renamed files show their old and new paths. Selecting a staged file opens its index-to-`HEAD` diff; selecting an unstaged tracked file opens its worktree-to-index diff; selecting an untracked file compares its working content to an empty file. Added and deleted files remain readable as one-sided diffs where Git supports them. A conflicted file is visibly identified and opens a read-only conflict-state presentation rather than being silently represented as an ordinary diff. No action in this surface stages, unstages, discards, resolves, or otherwise changes Git state.

### 3. Branch-unique commit review — Must-have

Each branch-unique commit row shows its abbreviated SHA, subject, author, timestamp, and file-change summary. Commits are collapsed initially. Activating a commit expands its flat list of changed files, with added, modified, deleted, and renamed badges; activating it again collapses the list.

Selecting a changed file opens the diff for that file at that exact commit in gabCode's existing main content area. The displayed historical content remains read-only. A deleted historical file does not offer an action that would open a non-existent current file in an external editor.

Merge commits remain visible and are clearly identified. Their changed-file lists and file diffs compare the merge commit with its first parent, and the UI labels that comparison basis. The branch range is recomputed from Git whenever branch tips or the selected worktree change; it is not captured as local history metadata.

### 4. Graph-inspired history affordance — Should-have

Commit rows use compact nodes and connecting rails inspired by Git graph views, so a developer can scan sequence and merge points quickly. The visual treatment must not claim topology it cannot represent: merge commits have a clear merge indicator.

This increment does not require a full lane-based directed acyclic graph renderer, arbitrary-history lane crossing, graph filtering, or a list/tree preference. Those remain follow-on work once the review workflow is proven.

### 5. Automatic, Git-authoritative refresh — Must-have

Filesystem and Git-reference events are invalidation hints. Relevant events include metadata for the selected worktree and shared repository references, including updates to the configured local `project.mainBranch`. On an event, gabCode debounces a short burst, runs bounded read-only Git queries, and atomically refreshes the sidebar from Git and the filesystem. A refresh also occurs when the worktree becomes selected or the application/worktree regains focus, with bounded periodic reconciliation as an additional safety net, so missed watcher events cannot leave the view indefinitely stale.

The sidebar offers an explicit **Refresh** control. It indicates loading while an update is in progress and preserves the last known data with an actionable non-destructive error if Git cannot be queried. Refreshing does not alter the repository.

To keep refresh responsive, the initial query retrieves working-change and commit-row metadata only. Commit rows are virtualized or incrementally presented for large branch ranges. A commit's changed-file list is retrieved when that commit expands, then invalidated when Git reconciliation establishes that the history changed. Binary, oversized, and otherwise unavailable file diffs show an explicit read-only state rather than failing silently.

A refresh preserves an expanded commit row and selected file when its commit SHA and file identity remain present in the refreshed data; it does not collapse a developer's review context merely because unrelated Git state changed.

### 6. Native-platform parity — Must-have

Windows and macOS independently implement the same Git semantics, review ordering, staged/unstaged distinction, diff selection behavior, refresh behavior, keyboard behavior, and accessible state. Their native controls may differ in presentation. Evidence from one platform does not establish the other's behavior.

## Non-Goals

This increment will not:

- Create dockable, movable, floating, or independently managed windows; dockable-window design is a future issue.
- Render a complete GitKraken-style multi-lane DAG, add graph filters, or provide list/tree display preferences.
- Show commits inherited from the configured main branch as branch work.
- Infer whether a particular change was made by an LLM, Pi, a terminal command, an IDE, or a person.
- Inspect, manage, start, resume, or interpret CLI coding-tool sessions or terminal content.
- Stage, unstage, discard, commit, amend, rebase, merge, push, pull, or otherwise mutate Git history or working state.
- Replace gabCode's existing main-area diff experience, file explorer, terminal behavior, or worktree-navigation model.
- Introduce a shared client/core runtime, companion service, internal protocol, database, or web application.

## Technical Considerations

- Git and the filesystem remain authoritative. Each client invokes installed Git with structured, read-only output; it must not parse the human-oriented layout emitted by `git log --graph`.
- Resolve branch-unique commits using the selected `HEAD` and the workspace's configured local `project.mainBranch`, excluding commits reachable from that main branch. If either reference is unavailable, preserve prior data only as stale presentation and state the recovery failure visibly.
- Use porcelain status data capable of distinguishing index and worktree changes, including rename paths. Compute staged and unstaged diffs from the corresponding Git comparison, and commit-file diffs from the selected commit.
- Treat watcher events for the selected worktree's Git metadata and the repository's shared references as hints, coalesce them, cancel superseded refreshes, and apply a completed result only if it still belongs to the selected worktree and current refresh generation. Reconciliation on selection/focus, bounded periodic reconciliation, and explicit refresh are required safety nets for missed or coalesced filesystem events.
- Do not eagerly retrieve file lists for every commit. Bound Git process output, duration, cancellation, and error reporting; large branches must not block native UI threads.
- **Windows:** C#/WPF owns Git process execution, watcher/reconciliation, cancellation, UI-thread application, sidebar controls, native keyboard behavior, and UI Automation accessibility.
- **macOS:** Swift/SwiftUI/AppKit owns the equivalent process execution, filesystem observation, async coordination, UI update, native controls, keyboard behavior, and accessibility.
- Cross-platform artifacts may describe fixtures and expected outcomes, but production implementation remains independent native code.

## Milestones

1. **Behavior and fixture definition** — Define language-neutral Git fixtures and expected outcomes for clean, staged, unstaged, untracked, conflicted, mixed staged/unstaged, rename, deletion, binary/oversized content, linear unique history, merge commits with first-parent comparison, rewritten history, large commit ranges, and unavailable Git/reference states.
2. **Windows review sidebar** — Implement the Windows right-sidebar review surface, Git normalization, staged/unstaged and commit-file diff routing, debounced reconciliation, explicit refresh, automated coverage, and Windows target-machine accessibility evidence.
3. **macOS review sidebar** — Implement the equivalent macOS behavior, native refresh/cancellation lifecycle, diff routing, automated coverage, and macOS target-machine accessibility evidence.
4. **Parity and resilience review** — Verify both clients against the shared fixtures; exercise external Git changes, rapid commit/rewrite bursts, branch/worktree switching during refresh, large branch behavior, missed-event focus reconciliation, and keyboard/screen-reader operation.

## Open Questions

None at the product-definition level. A later graph-rendering proposal may decide whether full topology lanes, graph filtering, and a list/tree preference provide sufficient value after this review workflow is in use.

## Approval Boundary

This PRD defines product behavior only. `/pm-agent` must create an approved implementation sprint, split into Windows and macOS increments as warranted, with separate target-platform evidence before execution. Passing tests and commits are implementation evidence, not human acceptance.
