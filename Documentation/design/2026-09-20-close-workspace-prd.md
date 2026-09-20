# gabCode Close Workspace PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-09-20 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related worktree actions | `Documentation/design/2026-08-22-worktree-actions-prd.md` |
| Related navigation | `Documentation/design/2026-08-10-worktree-navigation-prd.md` |

## Product Name & One-Liner

**gabCode Close Workspace** — stop and discard gabCode's retained terminal workspace for one selected worktree without changing Git or the filesystem.

## Problem & Audience

A developer using several worktrees may be finished with the terminal sessions for one piece of work but still need the Git worktree, branch, and folder available. Deleting that worktree is destructive and requires Git removal; leaving its retained terminal pair alive wastes resources and keeps unnecessary shell processes running.

The developer needs a clear action next to **Delete worktree** that closes only the selected item's gabCode-owned terminal workspace. In this document, **Close Workspace** is the user-facing action label; its target is the selected worktree in the worktree sidebar, not the application window or the `*.gabcode-workspace` descriptor.

## Core Features

### 1. Selected-worktree Close Workspace action — Must-have

The native worktree-sidebar context menu provides **Close Workspace** alongside the existing selected-worktree actions.

- The action operates on the context-menu target, even when that worktree is not the currently active sidebar selection.
- It is available for every accessible worktree, including Git's primary worktree; closing terminals is not Git worktree deletion.
- Right-clicking continues not to activate, navigate to, or start terminals for the target.
- The action is keyboard accessible and identifies the target worktree name, branch, and path to assistive technology.

### 2. Always-confirmed terminal closure — Must-have

Choosing **Close Workspace** always opens a fresh native confirmation before any terminal lifecycle change, including when the target has no active terminal processes or no retained terminal pair.

The confirmation identifies the selected worktree and states that only gabCode-owned terminals for that worktree will be closed. It offers **Cancel** and an explicit confirmation such as **Close Workspace**. Cancellation changes nothing and returns focus predictably to the invoking worktree item.

When active terminals exist, the confirmation states their count and that running shell work will be interrupted. The confirmation must not imply that the Git worktree, files, branch, descriptor, or other worktrees will be removed.

### 3. Targeted retained-terminal cleanup — Must-have

After explicit confirmation, gabCode gracefully stops only the selected worktree's gabCode-owned terminal sessions, using the same bounded lifecycle and failure handling as worktree deletion.

- A successful closure removes that worktree's retained terminal pair from gabCode memory.
- The worktree remains listed and selectable. Selecting it later creates a fresh terminal pair lazily; prior terminal processes and scrollback are not resumed.
- If no retained pair exists, confirmation succeeds without starting a terminal.
- If owned-terminal cleanup cannot be verified, gabCode leaves the retained workspace open, reports the failure, and does not claim that closure completed.
- Other worktrees' terminal pairs and the application window remain open and unaffected.

### 4. No Git or filesystem mutation — Must-have

Close Workspace does not invoke Git, refresh/reconcile as a mutation outcome, delete a worktree, delete a branch, delete a folder, alter the selected-worktree preference, or modify the `*.gabcode-workspace` descriptor. The sidebar entry remains visible after a successful close, with its running-terminal indicator updated.

### 5. Native-platform parity — Must-have

Windows and macOS independently provide the same selected-item behavior, confirmation rule, terminal scope, retained-pair disposal outcome, and accessibility semantics through their native context menus and dialogs. The presentation may follow each platform's native conventions; one platform's implementation or evidence does not establish the other's.

## Non-Goals

This increment will not:

- Close the gabCode application window or any other workspace/window.
- Delete, detach, prune, or otherwise alter a Git worktree.
- Delete or modify local or remote branches, files, folders, descriptors, preferences, commits, or GitHub data.
- Stop terminals associated with another worktree.
- Start a terminal merely to close it.
- Add a terminal archive, terminal resume feature, background terminal service, shared runtime, sidecar, web service, or database.
- Change the existing guarded **Delete worktree** behavior.

## Technical Considerations

### Platform-owned terminal registries

Each native client owns the operation in its existing retained-terminal registry:

- **Windows:** C#/WPF closes and removes only the context-targeted `WorktreeTerminalPair` from `WorktreeTerminalRegistry` after the native confirmation.
- **macOS:** SwiftUI/AppKit stops and removes only the context-targeted `TerminalWorkspacePresentation` from `WorkspaceTerminalRegistry` after the native confirmation.

Neither client creates a shared runtime abstraction. Existing process-tree termination, cancellation, bounded waits, cleanup failure handling, focus restoration, and UI-thread coordination remain platform-owned.

### Context-menu targeting and concurrency

The operation target must be captured from the context menu rather than inferred from whichever worktree happens to be selected when confirmation completes. A close request must not race with deletion, refresh/reconciliation, window shutdown, or another close request in a way that closes a newly created pair or a different worktree's pair. The implementation must disable or serialize conflicting actions for that target and re-check the target pair immediately before removal.

### Evidence

Each target platform needs automated and target-machine evidence for:

- closing a non-selected context-menu target without changing selection or opening that worktree;
- mandatory confirmation with zero, one, and two active terminals;
- cancellation preserving the exact target pair and processes;
- confirmed closure stopping and removing only the target pair;
- retained worktree/branch/folder/sidebar entry after closure;
- selecting the retained worktree later starting a fresh pair;
- cleanup failure retaining the target pair and reporting failure; and
- keyboard, focus, and native accessibility behavior for the menu and confirmation.

## Milestones

1. **Platform behavior audit** — identify each platform's context-menu targeting, terminal-pair registry/removal path, lifecycle lock, and existing deletion confirmation behavior.
2. **Windows increment** — add the Windows context action, always-confirmed targeted closure, concurrency safeguards, automated tests, and Windows target-machine evidence.
3. **macOS increment** — add the macOS context action, always-confirmed targeted closure, concurrency safeguards, automated tests, and macOS target-machine evidence.
4. **Parity and lifecycle review** — compare outcomes against this PRD, run adversarial cleanup/concurrency checks, and prepare human acceptance evidence for both platforms.

## Open Questions

None at the product-definition level.

## Approval Boundary

This PRD defines product behavior only. `/pm-agent` must create separate Windows and macOS implementation increments with target-platform evidence before execution. Passing tests and commits are implementation evidence, not human acceptance.
