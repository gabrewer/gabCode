# gabCode Windows Worktree Deletion Reliability PRD

| Field | Value |
| --- | --- |
| Status | Approved for planning; implementation not yet approved |
| Date | 2026-09-06 |
| Platform | Windows native client only |
| Related baseline | `Documentation/design/2026-08-22-worktree-actions-prd.md` |
| Related reliability work | `Documentation/design/2026-08-24-worktree-actions-reliability-prd.md` |
| Related navigation | `Documentation/design/2026-08-10-worktree-navigation-prd.md` |

## Product Name & One-Liner

**gabCode Windows Worktree Deletion Reliability** — make Windows worktree deletion and reconciliation complete predictably without stale selectable worktrees, prunable remnants, or unexplained retained folders.

## Problem & Audience

A Windows gabCode developer deleting a secondary worktree can encounter a split result: Git registration changes while the directory remains, or the directory disappears while Git still reports the worktree. Refresh can then fail on a missing path, show a `prunable` worktree, or leave a selectable entry that fails terminal startup with **“Terminal working directory does not exist.”**

The developer needs deletion to reach one understandable outcome: the worktree remains available and usable, or it is removed from gabCode and Git. If Windows prevents the final folder cleanup, gabCode must say exactly what remains and provide a safe recovery action.

This Windows-only PRD refines the deletion and reconciliation behavior in the related PRDs where the requirements conflict. It does not alter macOS behavior.

## Core Features

### 1. Reconciled deletion result — Must-have

A delete operation for a secondary worktree closes gabCode-owned terminals/processes through the existing approved flow, performs the guarded Git removal workflow, and reconciles the resulting state before reporting success.

- The primary worktree remains protected.
- Existing dirty-worktree, force-removal, terminal-stop approval, and optional local-branch deletion safeguards remain in effect.
- gabCode never reports a worktree removed merely because an individual process exited; it re-reads Git worktree state and the target path.
- A successful deletion removes the worktree from the gabCode list and Git registration. Local-branch deletion remains available only after that success and under the existing confirmation rules.

### 2. Retained-folder recovery — Must-have

Git for Windows may remove the worktree registration before it can remove the final directory or one or more files retained by another process.

- After normal and explicitly confirmed force-removal attempts, gabCode retries target-directory cleanup three times with a short bounded backoff totaling approximately three seconds.
- After every Git removal result, including a nonzero exit, gabCode re-reads `git worktree list --porcelain` before classifying the outcome.
- If that re-read confirms the target registration is absent but the target path remains, gabCode reports: **“Worktree removed; local folder remains at <path>.”** It does not misreport the Git removal as wholly failed merely because Windows retained files or the root directory.
- The result supplies **Retry Cleanup** and **Open Folder** actions. Retry Cleanup first confirms that no worktree is now registered at the normalized path, targets only the retained folder, and does not recreate or manually alter a Git worktree. A later retry that finds a non-empty folder requires fresh confirmation before recursively deleting it so files created after the original operation are not silently lost.
- If required worktree content cannot be removed and Git still reports the target registration, gabCode does not manually edit Git administrative metadata or claim removal. It reports that deletion is blocked, preserves the registered worktree, and offers **Retry Cleanup**. If Git no longer reports the registration, any remaining `.git` file or other content is treated as retained-folder residue under the confirmed removed-worktree outcome above.
- The message reports the actual failed path and operation. It must not assert that a folder is open or identify a locking application unless that fact is independently established.

### 3. Automatic missing-directory reconciliation — Must-have

A Git-registered secondary worktree whose directory no longer exists is stale and must not remain selectable.

- During refresh and before activation/terminal creation, gabCode verifies that every discovered secondary-worktree path exists and is accessible.
- If a registered path is missing, gabCode performs supported Git reconciliation/pruning, then re-reads Git worktree state.
- A registration confirmed absent after reconciliation is removed from the sidebar immediately and produces a brief recovery notice such as **“Removed missing worktree: <path>.”**
- gabCode must not launch a terminal for a missing directory or expose the raw terminal-working-directory error as the primary recovery experience.
- A refresh error for one missing/stale path must not discard valid worktrees or prevent the remainder of the worktree list from refreshing.

### 4. Non-activating worktree context menus — Must-have

Right-clicking a worktree opens only its native context menu.

- Right-click does not activate, select, navigate to, open terminals for, or otherwise open the worktree.
- Context-menu deletion operates on the context-menu target, not a worktree incidentally selected by pointer handling.
- The menu and its destructive actions remain keyboard accessible and expose the target worktree name/path to assistive technology.

### 5. Clear operation outcomes and diagnostics — Must-have

Deletion and reconciliation outcomes are visible at the point of the operation.

- Success, retained-folder recovery, blocked deletion, timeout, cancellation, Git rejection, and missing-directory reconciliation are distinguishable.
- Retained-folder and blocked-deletion results identify the path and offer the applicable recovery actions.
- Detailed Git/process diagnostics are available through an existing activity/log surface where one exists; this increment does not create a new history feature.
- Progress, recovery notices, action labels, and focus return are keyboard reachable and announced through Windows native accessibility surfaces without relying on color alone.

## Non-Goals

This increment will not:

- Change macOS worktree deletion or use Windows behavior as evidence for macOS.
- Delete a primary worktree, remote branch, unrelated folder, or external process.
- Bypass dirty-worktree, force-delete, local-branch deletion, or terminal-stop confirmation rules.
- Manually modify `.git/worktrees` or any other Git administrative metadata to hide a failed deletion.
- Promise to identify external locking processes.
- Add a mandatory PowerToys dependency, install PowerToys, or launch File Locksmith automatically.
- Add an archive of removed worktrees, a database, shared runtime, sidecar, web service, or internal client/core protocol.

## Technical Considerations

### Windows-native ownership

The Windows client remains a complete C#/WPF application. It owns native menus, deletion UI, direct Git invocation, filesystem cleanup, reconciliation, watcher/refresh coordination, retained terminal lifecycle, and accessible status presentation. No macOS production code or shared runtime is introduced.

### Authority and supported recovery

Git and the filesystem remain authoritative. gabCode invokes installed Git with argument-safe process APIs and uses `git worktree list --porcelain` to establish the post-operation state.

The implementation must validate the supported Git sequence on the target Windows Git versions. It may use supported `git worktree remove`, explicit force removal after existing confirmation, and `git worktree prune` reconciliation. It must not delete Git administrative records directly. A prune action may reconcile other genuinely stale registrations; the UI must re-read and accurately reconcile the complete resulting Git list.

### Concurrency and lifecycle

Deletion, refresh, watchers, terminal shutdown, Git process timeout/cancellation, and UI updates can race. The implementation needs one cancellation-aware operation ownership model per target worktree so that:

- a stale refresh result cannot restore a deleted entry;
- a watcher cannot produce a fatal refresh solely because a just-deleted path disappeared;
- cleanup retry cannot delete a path that has become a newly registered worktree; and
- gabCode-owned terminal shutdown finishes or is reported before cleanup begins.

Git process timeout remains distinct from filesystem cleanup retry. The existing bounded Git process lifecycle requirements continue to apply; the approximately three-second retry budget is only for final local-folder cleanup after the relevant Git result is known.

### Optional lock-holder integration

A later Windows-only enhancement may provide a user-clicked **Check locks with File Locksmith** action when PowerToys is installed and its supported invocation contract has been verified. It is best-effort, hidden when unavailable, and must not be required for deletion/retry recovery. Restart Manager-based lock attribution may also be evaluated separately; any result must be described as best-effort rather than certain.

### Evidence

Windows target-machine evidence must cover real temporary Git repositories and controlled filesystem/process fixtures for:

- normal clean and explicitly forced deletion;
- gabCode-owned terminal shutdown before deletion;
- all content removed while the final folder remains;
- cleanup blocked before the target `.git` file can be removed;
- externally removed worktree directory with stale Git registration;
- refresh/watcher activity racing deletion or external directory removal;
- no selectable missing-path entry and no terminal startup against a missing directory;
- context-menu right-click without selection/activation; and
- keyboard/UI Automation accessibility of recovery notices and actions.

External lock-holder attribution and File Locksmith availability are **NOT CHECKED** unless validated on an installed Windows target machine with a documented supported PowerToys invocation.

## Milestones

1. **Windows source and behavior audit** — identify the current deletion, refresh, watcher, terminal-stop, context-menu, and Git process paths; reproduce both split-brain states with durable fixtures.
2. **Reconciliation and selection safety** — make missing paths recoverable during refresh, reconcile stale Git registrations with supported Git operations, and prevent terminal creation for absent paths.
3. **Deletion and retained-folder recovery** — add bounded cleanup retry, verified Git reconciliation, retained-folder/blocked-deletion outcomes, and safe retry actions.
4. **Context-menu and accessibility behavior** — make right-click non-activating and verify native keyboard, UI Automation, focus, and recovery presentation.
5. **Windows lifecycle verification** — run automated fixtures, native build/tests, target-machine lock/race smoke evidence, and adversarial lifecycle review.

## Resolved Product Decisions

- When Git has removed the registration but Windows leaves the target folder or retained files, gabCode reports that leftover path rather than leaving a prunable/selectable worktree or misreporting the removal as wholly failed.
- When a Git registration points to a missing directory, gabCode reconciles it automatically before the user can select it or launch a terminal from it. Because supported `git worktree prune --expire now` is repository-wide, this may remove every genuinely stale registration in that repository; gabCode then re-reads and accurately reconciles the complete worktree list.
- If deletion cannot safely remove the target `.git` file, gabCode keeps the worktree registered and reports a recoverable blocked deletion rather than editing Git metadata directly.
- Final local-folder cleanup retries three times across approximately three seconds, then provides explicit recovery actions.
- Right-clicking a worktree never opens or activates it.
- Existing gabCode-owned terminal/session shutdown remains part of deletion; this increment does not weaken its existing approval behavior.
- File Locksmith integration is a separate optional enhancement, not a prerequisite for reliable deletion.

## Approval Boundary

This PRD is approved for `/pm-agent` planning as a Windows-only increment. Planning must establish the actual source paths, supported Git behavior, target-machine verification commands, and review-size boundary before implementation. Passing tests and commits remain implementation evidence; human acceptance is required.
