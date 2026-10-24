# Windows Refresh Focus Preservation

| Field | Value |
| --- | --- |
| Date | 2026-10-24 |
| Status | Windows sprint planned; implementation and native validation not started |
| Sprint | [Windows Refresh Focus Preservation and Terminal Opening Focus #129](https://github.com/gabrewer/gabCode/issues/129) |
| Target | Windows native client (C#/WPF) |
| Scope | Preserve terminal input during automatic review-tree refresh and focus a newly opened terminal |

## Product Name & One-Liner

**gabCode Windows Refresh Focus Preservation** — Intentional terminal opening puts input in the new terminal; automatic refresh never redirects that input into the review tree.

## Problem & Audience

A developer typing in gabCode's Windows terminal reports that automatic refresh sometimes transfers focus to the tree, after which a keystroke opens a diff or performs another unintended action. They did not click the tree or manually request refresh.

The user calls this the “GitHub tree.” The existing **Changes and commits** review sidebar is an investigation entry point, not a confirmed diagnosis of the failing component. The exact focus-transfer path and triggering key remain unconfirmed.

The smallest useful outcome is uninterrupted terminal input while automatic refresh remains enabled. Briefly stealing focus and restoring it afterward is not sufficient: a key can be misdirected during that interval. When the user opens a new terminal window or reopens terminals in a workspace, it should be ready for typing: with two terminal panes, focus starts in the top pane. The lower pane finishing initialization later must not take it.

## Core Features

### 1. Focus-neutral automatic refresh — Must-have

Automatic refresh must not move keyboard focus away from the user's current input surface.

- Cover refresh start, loading, result publication, tree rebuilding, deferred callbacks, and completion, including lazy detail completions.
- While the terminal owns input, it continues to own input throughout these transitions, not just before and after them.
- An inactive window must not activate itself or claim input because its refresh completes.

### 2. No refresh-driven activation — Must-have

Retaining or reconciling tree selection must not be treated as an instruction to open a diff or activate another view.

- Terminal keystrokes, including Enter, Space, and navigation keys, retain their normal terminal meaning.
- Existing read-only content may refresh in the background, but must not become the active input surface merely because it refreshed.
- Removing a selected file may invalidate its review state; it must not activate a replacement file or pull focus into the tree.

### 3. Latest user navigation wins — Must-have

Delayed refresh work must respect focus changes made by the user after that work started.

- If the user moves from the tree to the terminal before a queued callback runs, that callback must not reclaim focus for the tree.
- If the user deliberately moves from the terminal to the tree or another control, refresh must not force focus back to the terminal.
- Focus preservation must account for the hosted native terminal, not assume WPF's remembered focus alone proves where keyboard input is going.

### 4. Preserve intentional tree interaction — Must-have

Clicking and existing supported keyboard navigation must still allow the user to focus tree controls and deliberately open a diff.

- Preserve selected-file and expansion context where identities survive refresh, without confusing selection with keyboard focus.
- When focus genuinely remains in the tree, preserve the equivalent focused control where possible. If that control disappears, use a predictable non-activating fallback within the tree.
- Refresh status and errors remain accessible without focusing their controls or introducing modal prompts. Do not make the tree unfocusable as a workaround.

### 5. New terminal opening focus — Must-have

When the user deliberately opens a project window with terminals or reopens a terminal pair in the current window, keyboard input should go to the new window's top terminal pane as soon as it is ready for input. “Top” means the pane currently displayed in the main/top terminal region, not a fixed shell identity; the bottom pane remains reachable by deliberate navigation.

- If both panes initialize at different times, the bottom pane's later readiness, layout, or attachment must not override focus established in the top pane.
- A pending review refresh, old window callback, or terminal initialization callback must not reclaim focus after the user intentionally chooses another pane or control.
- Background work must not bring an inactive gabCode window in front of another application. A deliberate window-opening action may activate the new window; later initialization must not steal activation back if the user has switched elsewhere.
- If top-terminal initialization fails, show its normal actionable failure state rather than silently focusing the bottom pane or claiming input is ready.

### 6. Safe failure and repeated refresh — Must-have

Empty results, errors, safety stops, cancellation, and repeated automatic refreshes obey the same input-preservation rules.

Keep existing refresh resource limits, stale-result protection, terminal sessions, and worktree lifecycle behavior intact. Completion from an old worktree or closed window must not change current focus.

## Non-Goals

- macOS implementation or claims about macOS behavior.
- Redesigning the sidebar, Git comparison semantics, or refresh scheduling architecture without evidence that the fix requires it.
- Disabling automatic refresh, disabling keyboard access to the tree, or adding a user preference for correct focus behavior.
- Polling to force focus back to the terminal, swallowing keys, or replaying intercepted terminal input as a workaround.
- Changing intentional worktree-switching, diff-opening, dialog, or manual refresh navigation behavior beyond preventing stale automatic work from overriding it.
- Forcing focus to the top terminal on every window activation, periodic refresh, or return from another application; the top-pane default applies to intentional creation/reopening, not all navigation.
- Replacing the terminal control, interpreting CLI harness sessions, or creating a shared runtime/service.

## Technical Considerations

Use the existing Windows C#/WPF client and native terminal hosting. No new service, third-party API, or package is required by this design. Git, the filesystem, and read-only `gh` remain authoritative; focus is transient presentation state.

Related requirements:

- [Initial product requirements](2026-07-24-gabcode-initial-prd.md).
- [Windows Native Terminal Foundation](2026-07-30-windows-native-terminal-foundation-prd.md): two retained terminal regions and native input hosting.
- [Instance Window Opening](2026-09-06-instance-window-opening-prd.md): separate native project windows.
- [Worktree Change Review Sidebar](2026-09-20-worktree-change-review-sidebar-prd.md): automatic reconciliation and preservation of review context.
- [Windows Git Tree Refresh Safety](2026-10-15-windows-git-tree-refresh-safety-prd.md): bounded refresh and non-focus-stealing status/recovery.
- [Native Diff Review](2026-10-18-native-diff-review-prd.md): existing read-only diff presentation and native host responsibilities.

This is a focused Windows correction to those expectations, not a new cross-platform architecture decision.

Read-only inspection identified these investigation entry points:

- `src/GabCode.Windows/Projects/WorktreeReviewSidebar.cs`: `Render` captures WPF focus information, replaces rows, and queues a callback that may focus a replacement file button or disclosure control. Its render/worktree/snapshot guards do not by themselves establish that the user has not moved focus since capture. This is an investigation lead, not a reproduced root cause.
- `src/GabCode.Windows/MainWindow.xaml.cs`: review selection, diff-opening/refresh routing, terminal-pair attachment, window activation, and focus restoration integration. `CreateTerminalWorkspace` attaches two terminal views and currently calls `ShowPiInMain`, which focuses the main/top terminal; check late readiness and cross-window behavior rather than assuming that call alone guarantees native input focus.
- `tests/GabCode.Windows.Tests/Projects/ReviewRefreshControlsTests.cs` and the existing review coordinator/service tests: starting points for determining reusable coverage and gaps.

Prefer preventing unauthorized focus changes at their source. Audit native keyboard-focus ownership as well as WPF focus state and deferred restoration; a final-focus assertion alone cannot prove input safety.

### Required Windows verification

| Scenario | Required outcome |
| --- | --- |
| Type identifiable harmless input in each terminal region while watcher and periodic refreshes run | Input reaches only the intended terminal; no refresh-caused focus excursion, lost input, duplicate input, or review action |
| Select a file, return to the terminal, then refresh unchanged and changed tree data | Remembered selection does not focus the tree or activate a diff |
| Pause a deferred tree-focus callback, move focus to the terminal, then release it | The old callback cannot reclaim input |
| Move deliberately into the tree while refresh is pending | The user's navigation wins; deliberate click/keyboard activation still opens the intended diff |
| Remove, rename, reorder, or replace selected/focused rows; complete lazy details | No unintended activation; tree-local focus fallback occurs only when the tree still owns input |
| Empty/error/stale/safety-stop results and repeated refresh bursts | Status remains non-modal and focus-neutral; terminals remain alive and usable |
| Switch worktrees, close a window, or move to another window while refresh is pending | Late work cannot focus an obsolete tree, reactivate a window, or override current input |
| User opens a project window containing two terminal panes or reopens its terminal pair | New foreground window accepts harmless input in its top pane without an extra click; bottom-pane initialization cannot take focus later |
| User changes pane/control or switches to another application before terminal initialization finishes | The later callback respects that choice and does not reactivate the new or an older window |
| Top pane fails to initialize while bottom succeeds | Actionable top-terminal failure is visible; no false ready-to-type state or surprising focus shift to the bottom pane |
| Keyboard and Narrator use of affected tree/status controls and new terminals | Existing navigation and accessible status remain usable; opening focus is predictable without later automatic focus transfer |

Use deterministic regression tests for callback ordering, selection-versus-activation, and terminal readiness ordering, plus Windows native-host interaction evidence with the real terminal. Exercise keys around publication and deferred callbacks, not only after refresh finishes. Verify the active top pane rather than assuming a particular shell remains on top after a swap. Use disposable repositories and harmless input capture rather than risking commands in the user's working shell.

Runtime reproduction, automated regression tests, native focus/input behavior, and Narrator checks are **NOT CHECKED** in this PRD-writing session. No root cause or fix is claimed.

## Milestones

1. **Characterize the transfer** — Identify the affected tree and actual native/WPF focus path; establish a safe reproduction or deterministic race case and retain any unresolved incident details explicitly.
2. **Prevent unauthorized focus changes** — Add regression coverage and implement the narrow Windows correction while preserving intentional navigation and background updates.
3. **Validate uninterrupted input on Windows** — Run the verification matrix with real hosted terminals, including two-pane startup and deliberate new-window activation; inspect transient focus/input routing and prepare evidence for human acceptance.

These are product milestones, not filesystem-backed execution state. [Windows sprint #129](https://github.com/gabrewer/gabCode/issues/129) owns the implementation-ready task board, native interaction details, source-delta audit, dependencies, and verification. This document does not begin implementation.

## Open Questions

- Which component and callback actually cause the reported transfer: tree reconstruction, deferred restoration, diff refresh, native-host focus interaction, or another path?
- Which key and prior selection/expansion state reproduce the incident most reliably? This is investigation work, not a requirement for the user to reproduce it before planning can proceed.
