# gabCode Worktree Actions Reliability PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-08-24 |
| Platforms | Native Windows and native macOS clients |
| Related baseline | `Documentation/design/worktree-actions-prd.md` |
| Related navigation | `Documentation/design/worktree-navigation-prd.md` |
| Originating review | PR #78 review findings |

## Product Name & One-Liner

**gabCode Worktree Actions Reliability** — make worktree creation and deletion recover predictably from branch conflicts, missing remotes, partial success, Git failures, and cancellation on both Windows and macOS.

## Problem & Audience

The normal worktree creation and deletion paths are useful and understandable, but less-common failures can still leave developers with a raw Git error, a hidden partial success, an operation that appears to do nothing, or uncertainty about whether a cancelled process is still running.

This affects gabCode developers on Windows and macOS who expect destructive and repository-changing actions to be as trustworthy when something goes wrong as when everything succeeds. The product should explain the state Git actually reached, preserve unrelated work, and offer the next safe action without requiring the user to diagnose implementation details.

This PRD is a focused reliability follow-up to the Worktree Actions PRD. It does not replace that workflow or change Git/filesystem authority.

## Core Features

### 1. Existing-branch conflict recovery — Must-have

Before creating a generated branch, gabCode determines whether that branch already exists and whether it is attached to a worktree.

- If the branch does not exist, creation proceeds normally.
- If it exists locally but is not attached, gabCode offers two explicit choices: create the worktree from that existing branch, or return to choose another branch name.
- If it is already attached, creation remains blocked and identifies the owning worktree.
- If a selected remote branch would conflict with an existing local branch of the same name, gabCode explains the conflict and offers the valid local-branch path instead of allowing a predictable Git failure.
- No conflict path silently forces, resets, deletes, or rewrites a branch.

Windows and macOS may use different native dialogs, but they expose the same decisions and outcomes.

### 2. Upstream-aware latest-remote creation — Must-have

The latest-remote option appears only when the workspace-selected branch has a usable configured upstream.

- Missing or unusable upstream configuration is not an error during ordinary local creation; the option is hidden and creation uses the local workspace branch.
- Selecting latest remote fetches only the configured remote branch and does not pull, merge, rebase, reset, or otherwise modify the existing workspace worktree.
- If fetch fails, gabCode reports the reason and lets the user retry or explicitly continue from the local workspace branch.
- Cancellation preserves the current selection, worktrees, terminals, and focus.

### 3. Honest partial-success recovery — Must-have

Worktree creation and optional editor setup are reported as separate outcomes.

If Git successfully creates and reconciles the worktree but optional setup fails—for example, a VS Code workspace file cannot be written or VS Code cannot be launched—gabCode:

- clearly states that the worktree itself was created successfully;
- keeps the failure visible until the user dismisses it;
- identifies which optional action failed;
- offers a practical retry or recovery action where possible; and
- does not roll back or misrepresent the valid worktree.

The new worktree remains selected after successful Git reconciliation even when optional setup fails.

### 4. Bounded Git process lifecycle — Must-have

Every Git action completes, fails, times out, or cancels within a defined bound without leaving a gabCode-owned helper process running.

- Standard output and standard error are drained while the process runs so valid large output cannot deadlock the operation.
- Retained diagnostic output is bounded to protect memory while preserving actionable error details.
- Cancellation and timeout stop and reap the owned Git process tree, including fetch credential, transport, or SSH helpers started for that action.
- Cancellation, timeout, launch failure, and Git nonzero exit remain distinguishable outcomes.
- gabCode never terminates unrelated external Git, editor, shell, or terminal processes.

Windows uses native process-tree facilities appropriate to .NET/Windows. macOS uses native Unix process-group and signal/reaping behavior. The platforms share expected outcomes, not process-management production code.

### 5. Actionable deletion preflight failures — Must-have

Deletion begins by determining the current Git worktree and dirty state. If that inspection cannot complete, gabCode does not silently stop.

- Git unavailable, timeout, missing/moved worktree, repository discovery failure, and status failure each produce a visible native error.
- The message explains that deletion did not begin and gives a useful next step, such as retrying, refreshing, or resolving the repository state.
- No terminal stop, safe removal, force removal, or branch deletion runs after a failed preflight.
- Current selection, retained terminals, process ownership, and focus remain unchanged.
- Existing clean, dirty, force-recovery, primary-protection, and local-branch deletion behavior remains unchanged.

### 6. Native parity and accessible recovery — Must-have

Windows and macOS independently prove the same reliability outcomes on their target operating systems.

- Recovery dialogs and errors are reachable by keyboard and full keyboard access.
- Narrator and VoiceOver identify the operation, affected branch/worktree, whether Git mutation succeeded, the consequence of each recovery action, and current progress.
- Focus returns predictably after retry, cancellation, local continuation, partial success, and deletion preflight failure.
- Destructive and non-destructive choices are distinguishable without relying only on color or icons.
- One platform's automated or manual evidence does not prove the other platform.

## Non-Goals

This reliability increment will not:

- Add new worktree creation modes, PR/fork discovery, or GitHub branch APIs.
- Delete remote branches or weaken primary-worktree protection.
- Add pull, merge, rebase, push, commit, stage, reset, or history-editing behavior.
- Replace Git/filesystem authority with gabCode metadata.
- Introduce a shared Windows/macOS production runtime, sidecar, internal protocol, web service, or database.
- Redesign the successful normal creation/deletion experience beyond the recovery controls required here.
- Treat VS Code as mandatory for successful worktree creation.

## Technical Considerations

### Independent native clients

The Windows client remains a complete C#/WPF implementation. The macOS client remains a complete Swift/SwiftUI/AppKit implementation. They share vocabulary, language-neutral scenarios, and expected results only.

Existing behavior should be audited before implementation. If one platform already satisfies a requirement—such as remote availability detection, concurrent output draining, or full process-tree termination—the work for that platform is evidence and focused regression coverage rather than unnecessary rewriting.

### Git authority and reconciliation

Branch existence, upstream configuration, dirty state, and worktree membership come directly from Git. Creation and deletion success is reported only after `git worktree list --porcelain` reconciliation confirms the resulting state.

Recovery UI must carry enough typed state to distinguish:

- nonexistent, existing-unattached, and attached branches;
- local and remote-tracking refs;
- unavailable and usable upstreams;
- Git success with optional-action failure;
- cancellation, timeout, launch failure, and Git rejection; and
- deletion preflight failure versus removal failure.

These are platform-native internal state models, not a cross-client runtime contract.

### Process safety

Process tests must use identifiable child/descendant fixtures and verify their disappearance after cancellation or timeout. Output-pressure tests must exceed normal pipe capacity and validate bounded retained diagnostics. Target-specific implementation should use supported native process APIs rather than shell command strings.

### Evidence

Automated evidence should include real temporary repositories with:

- existing unattached and attached local branches;
- local/remote name collisions;
- configured, missing, and invalid upstreams;
- fetch failure and cancellation;
- optional workspace-file and VS Code launch failures;
- large stdout/stderr output;
- long-running and signal-resistant descendants; and
- deletion discovery/status failures.

Native UI evidence must exercise the actual menus/dialogs rather than only model state. Windows requires Windows target-machine evidence; macOS requires Apple Silicon macOS target-machine evidence.

## Milestones

1. **Cross-platform conformance scenarios** — define shared vocabulary and expected outcomes for the five reliability categories without creating shared production runtime code.
2. **Creation recovery** — independently validate or implement branch-conflict, upstream availability/fetch recovery, and partial-success reporting on Windows and macOS.
3. **Process lifecycle hardening** — independently prove concurrent output draining and owned process-tree cleanup on each target OS.
4. **Deletion and accessibility recovery** — surface deletion preflight failures and verify keyboard, Narrator/VoiceOver, focus, and non-interference behavior on each platform.
5. **Platform parity review** — compare Windows and macOS outcomes, record intentional native UI differences, and prepare evidence for human acceptance.

## Open Questions

- Should optional VS Code workspace-file recovery offer an in-dialog retry, reveal the new worktree in the file manager, or both? The required outcome is visible partial success; the native presentation may differ.
- Should fetch failure default focus to **Retry** or **Continue from local**? Neither action may execute without explicit user choice.
- Which language-neutral fixture format best captures process and Git outcomes without becoming an internal runtime protocol?
- What target-machine accessibility evidence will the human accept when Narrator or VoiceOver automation cannot prove spoken output quality?
