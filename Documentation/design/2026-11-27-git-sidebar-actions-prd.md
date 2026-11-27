# gabCode Git Sidebar Actions PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-11-27 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related sidebar | `Documentation/design/2026-09-20-worktree-change-review-sidebar-prd.md` |

## Product Name & One-Liner

**Git Sidebar Actions** — native context menus for safely managing changes and stashes without leaving the selected worktree.

## Problem & Audience

A developer working with CLI coding tools across Git worktrees wants to manage the changes already visible in gabCode without switching to VS Code or a terminal. The immediate need is discarding an unwanted file change; staging, unstaging, ignoring a new file, and temporarily putting all work aside should be similarly convenient.

## Core Features

### 1. Stage and unstage files — Must-have

Right-clicking a working-change file with unstaged changes offers **Stage Changes**, including for a new untracked file or a file that also has staged changes. It stages the selected file's remaining changes, not other files. A file with staged changes offers **Unstage Changes**, which removes its changes from the index but retains its working content. Both commands are available for a file with staged and unstaged changes. This is whole-file staging, not line-by-line staging.

### 2. Discard changes safely — Must-have

A tracked file with unstaged changes and **no staged changes for that file** offers **Discard Changes…**. It restores that file to the selected worktree's last commit, without affecting other files. Before doing so, a native confirmation identifies the file, explains that its changes will be lost, and defaults to Cancel. A file with any staged changes cannot be discarded until those changes are unstaged. Untracked files do not offer this action and are not deleted. If the state has changed since the menu or confirmation opened, recheck the preconditions and refuse rather than discard staged work.

### 3. Ignore a new file — Must-have

An untracked file offers **Add to .gitignore**. Add a rule for only the selected repository-relative path to the selected worktree's root `.gitignore`, creating the file if needed. Leave the selected file on disk and leave the `.gitignore` change unstaged. Do not offer this action for tracked files, or add folder-wide or extension-wide rules. Escape special characters correctly so the rule matches the intended literal path; do not silently overwrite existing content. Refresh the untracked list from Git. The action adds the rule; it does not guarantee that the file becomes ignored. If a higher-precedence rule (for example, a nested `.gitignore` negation) keeps the file untracked, leave the new root rule in place and keep showing the file. Do not change other ignore files or rules to force it to disappear.

### 4. Stash all changes — Must-have

The worktree context menu offers **Stash All Changes…** for the selected worktree. Save its staged and unstaged changes and untracked files in a Git stash, leaving ignored files alone. Clearly identify the worktree and scope before proceeding. Other worktrees' working files must not be changed.

### 5. Pop a stash — Must-have

The worktree context menu offers two commands. **Pop Latest…** identifies the newest repository stash and asks for confirmation without opening a picker. **Pop Stash…** opens a keyboard-accessible list of that same repository's stashes, newest first; the developer selects one, including an older entry. In both flows, show the entry and destination worktree before confirmation. Cancel changes nothing. If there are no stashes, neither command can run.

A successful pop restores the stash's changes **unstaged**, without disturbing unrelated files or existing staged changes. A previously staged new file returns as untracked. After confirmation, revalidate the target worktree and the selected stash against the current list. If that check detects a different or ambiguous entry, refuse before invoking Git; do not silently switch entries.

For a validated selection, gabCode delegates consumption to one ordinary native `git stash pop` of the verified selector. With no concurrent external stash changes, Git consumes the selected entry on success and keeps it if applying fails or conflicts. Once the command is dispatched, external Git changes have ordinary Git semantics: Git does not atomically bind the applied stash identity to removal if another process changes the stack mid-command. Another entry can be removed in that race. gabCode does not add a separate drop, rewrite the reflog, retry automatically, or fall back to Apply. Refresh worktree status and the stash list and report the observed result, including partial or uncertain outcomes.

### 6. Identity and refusal rules — Must-have

The selected registered worktree's resolved root identifies the operation target; its repository's Git common directory identifies the shared stash namespace. File identity is the worktree plus the exact repository-relative path and, where relevant, both rename paths. A stash identity includes the Git object ID and its verified current list entry; an ordinal alone is not durable. Identical object IDs can occur more than once, so ambiguity must not be resolved by guessing. Recheck identities and status after confirmation and before mutation. Git and the filesystem, not cached UI rows, are authoritative.

| Situation | Required result |
| --- | --- |
| Tracked, unstaged only, HEAD version exists | Stage or confirmed discard; discard affects only this path. |
| Any staged content and unstaged content | Stage or unstage; no discard. |
| Staged only | Unstage; retain current working content. |
| Untracked | Stage or add that exact path to `.gitignore`; no discard. |
| Conflict, unsafe rename, unsafe symlink, inaccessible worktree, or unavailable required HEAD | Refuse affected action with an explanation; never broaden its scope. |
| Stale file/worktree state, or changed or ambiguous stash selection detected before dispatch | Abort the unsafe operation, explain and refresh; do not invoke Git with a selector known to be stale. |
| Pop fails or conflicts | Report observed worktree and stash state, refresh, and leave recovery to the user; do not remove the stash separately. |

An in-app gate must coordinate overlapping worktree/index operations and repository-wide stash operations; it cannot lock out external Git. Pop has the native Git concurrency limit described above, not an app-level compare-and-remove guarantee. This limit does not relax safeguards for unrelated index entries, worktree contents, or other actions.

### 7. Native interaction and recovery — Must-have

Make menus accessible by pointer and keyboard, with native accessible names and status/error announcements on Windows and macOS. Disable or omit actions when their prerequisites are not met, and explain failures without claiming success. Reconcile from Git and the filesystem after operations, including failures; preserve review context where possible. Unavailable Git, a changed worktree, conflicts, and unsupported file states must not trigger a guessed or broader operation.

## Non-Goals

- Staging individual lines or hunks; multi-file selection and batch file actions.
- Deleting untracked files, ignoring folders or file extensions, or automatically staging `.gitignore`.
- Per-file stashes, stash editing, standalone stash deletion, restoring a stash's original staged state, or automatic conflict resolution.
- Committing, amending, rebasing, merging, pushing, pulling, or modifying repository history.
- General source editing, terminal/session interpretation, or a shared client/core runtime.

## Technical Considerations

- **Scoped product-boundary change:** these explicitly user-invoked actions would modify Git's index, working files and stash, and the root `.gitignore`. This is the explicit narrow exception to the initial PRD's stage/working-state restrictions and the review sidebar PRD's read-only action boundary; it does not authorize general source editing or other Git mutations.
- Windows and macOS implement the same observable behavior independently in their native C#/WPF and Swift/SwiftUI/AppKit clients. Git and the filesystem remain authoritative; do not introduce a shared runtime or service.
- Invoke Git directly in the captured target worktree with safe argument handling and literal path semantics. Revalidate state immediately before mutation, especially after confirmation or worktree switching. Do not allow stale UI state to broaden an operation; report race or Git-lock failures and refresh.
- Define and test safe handling for staged additions, deletions, renames, conflicts, unborn `HEAD`, unusual filenames, symlinks, nested paths, ignore-pattern escaping, and existing `.gitignore` contents. Disable an action where its exact safe behavior cannot be established rather than affecting other paths.
- Stashing and popping can fail or conflict, including when changes occur concurrently or a stash from another worktree cannot apply cleanly. Do not automatically retry a destructive operation or report a clean worktree without checking Git. Verify the destination index and working files in real Git tests; simply omitting index restoration is not by itself evidence that added files return unstaged. If existing staged changes overlap and cannot be preserved, refuse before applying.
- Validate menus, confirmation, focus, keyboard and screen-reader behavior on each target OS. Shared fixtures and expected outcomes do not substitute for separate platform evidence.

## Milestones

1. **Shared behavior and fixtures** — Define cross-platform Git-state, stash identity, concurrency and path-handling cases and expected outcomes.
2. **Windows increment** — Implement and validate the native Windows actions, failure handling, refresh, and accessibility against the shared cases.
3. **macOS increment** — Independently implement and validate equivalent native macOS behavior against the same cases.
4. **Parity and recovery review** — Exercise both platforms with disposable repositories, multiple worktrees, concurrent changes, conflicts, and unavailable Git; record target-specific evidence.

## Open Questions

- Platform implementations must determine which rename cases can be supported safely; unsupported or ambiguous cases must refuse without widening the operation. The external stash-stack race after Pop dispatch follows native Git semantics as specified above.

## Approval Boundary

This PRD records the requested product direction, not implementation approval. The scoped change to the former read-only Git boundary is reflected in the baseline, sidebar PRD and project instructions; platform implementation remains subject to the shared-first dependency. Planning and platform increments require their normal separate evidence; passing tests are not human acceptance.
