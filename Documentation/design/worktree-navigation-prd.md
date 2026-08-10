# gabCode Worktree Navigation and Retained Terminals PRD

| Field | Value |
| --- | --- |
| Status | Approved for planning; implementation not yet approved |
| Date | 2026-08-09 |
| Related baseline | `Documentation/design/projects-and-worktrees-prd.md` |

## Product Name & One-Liner

**gabCode Worktree Navigation** — discover existing Git worktrees in a project, switch between them quickly, and retain each worktree's terminal sessions like Windows Terminal tabs.

## Problem & Audience

A developer working across several existing Git worktrees needs to move between them without reopening shells, losing output, or manually remembering which folder belongs to which branch. gabCode should provide a lightweight worktree switcher while leaving Git and terminal processes authoritative and user-controlled.

This increment assumes the developer has already created the repository and worktree structure using Git or other tools. gabCode does not create or remove worktrees in this PRD.

## Core Features

### 1. Discover registered worktrees — Must-have

When the user opens a valid workspace, gabCode discovers the single Git repository/worktree set beneath its configured project root using read-only Git queries. Git's `git worktree list --porcelain` output is authoritative.

- Every branch-bearing Git-registered worktree is listed, including the normal repository checkout. Detached worktrees remain out of scope for v1 because the workspace contract identifies selection by branch.
- The workspace's selected branch/worktree is selected on activation.
- A missing selected worktree produces recovery; gabCode does not silently choose another worktree.
- Discovery remains limited to the existing workspace contract: exactly one repository/worktree set beneath the project root.

### 2. Simple worktree sidebar — Must-have

The project surface gains one native sidebar list. It defaults to the left and can move to the right through a keyboard-reachable native **View → Move Sidebar Right/Left** command; gabCode remembers the chosen side in local per-user preferences without changing the workspace descriptor. Native resizing is used where the platform supplies it, while width persistence remains deferred. Worktrees are not duplicated as a second tab or header navigation system.

Each available entry shows only:

- Worktree folder name as the primary label.
- Branch name as the secondary label.
- Active selection state.
- A running-terminal indicator when retained sessions exist.

Ordering is:

1. Primary worktree first.
2. Other available worktrees sorted by folder name.
3. Temporarily unavailable worktrees last.

The primary-worktree designation comes from Git/worktree discovery, not from a folder named `main`.

### 3. Lightweight switching — Must-have

Selecting an available worktree changes gabCode's selected context immediately without confirmation.

- No process is stopped or restarted during switching.
- No terminal cleanup dialog appears during switching.
- The active title/context and project chrome update to the selected worktree.
- Git actions in later increments must use the selected worktree context, not the focused terminal's current directory.
- Keyboard users can move through the sidebar and activate a worktree without pointer-only interaction.
- Switching changes in-memory window context only; it does not rewrite `project.branch` in the workspace descriptor. Reopening starts from the descriptor-selected branch until a later local selected-worktree preference is approved.

### 4. Lazy terminal creation and retention — Must-have

Each worktree owns two ordinary terminals, matching the existing project foundation.

- A worktree's terminal pair is created the first time that worktree is selected.
- The terminals start in the exact normalized worktree folder.
- Switching away leaves both processes, output, and session views alive.
- Returning restores the same terminal views and processes.
- Terminal `cd` does not change selected worktree identity or ownership.
- Worktrees never selected during the session do not start shells merely because they were discovered.
- Closing or quitting gabCode retains the existing explicit terminal shutdown confirmation and cleanup behavior.

The experience should feel like switching Windows Terminal tabs, except each selected worktree owns its own retained pair of terminals.

### 5. Explicit refresh — Must-have

V1 uses a user-invoked **Refresh Worktrees** button in the sidebar header rather than filesystem watchers or automatic reconciliation. The same action is available through the native command surface with `Ctrl+R` on Windows and `Command-R` on macOS.

- Refresh reruns read-only Git worktree discovery.
- Refresh is keyboard reachable and exposes progress/error state where needed.
- Refresh never creates, removes, or mutates Git worktrees.
- Refresh does not terminate retained terminal processes.
- A stale result must not overwrite a newer selection or terminal state.

### 6. Temporary unavailable state — Must-have

If Git no longer reports a previously discovered worktree, gabCode keeps it visible temporarily as **unavailable**.

- The unavailable entry is moved below available worktrees.
- Its branch/folder identity remains visible with an unavailable state.
- Retained terminals remain visible and are not killed automatically.
- The user may close those terminals explicitly through normal terminal controls.
- If the unavailable worktree is active, gabCode shows recovery and does not silently switch to another worktree.
- The first refresh that no longer reports a worktree marks it unavailable. If the next explicit refresh still does not report it, gabCode removes it from the worktree list.
- When a removed sidebar entry still owns retained terminals, those sessions move to a compact **Orphaned terminals** section at the bottom of the sidebar until the user closes them or quits gabCode; gabCode never kills them merely because Git stopped reporting the worktree.
- An orphaned entry remains selectable so the developer can return to its terminal pair. It shows an unavailable banner, supplies no authoritative Git worktree context, and disables future worktree-scoped Git actions while selected.
- **Close Terminals** on an orphaned entry uses the existing explicit terminal-stop confirmation and removes the entry only after cleanup succeeds. Application close/quit continues to aggregate every available, unavailable, and orphaned pair.
- If Git reports the same normalized worktree path again while its prior terminal pair remains retained—whether unavailable or orphaned—gabCode automatically restores the worktree entry and its prior terminal pair.

## Non-Goals

- Creating, adding, moving, locking, pruning, or removing worktrees.
- Creating repositories, running `git init`, choosing repository layouts, or creating project folders.
- Git status, dirty-file counts, commit history, ahead/behind state, PR state, or GitHub integration.
- Automatic filesystem watchers or background refresh.
- Automatic switching based on terminal current-directory reports.
- Confirmation when switching worktrees.
- Automatically killing processes for unavailable or removed worktrees.
- Multiple repositories beneath one workspace root.
- Detached-worktree navigation, worktree pop-out windows, worktree-to-PR associations, or Git worktree cleanup workflows.
- Persisting the last worktree selection or sidebar width; reopening begins from the workspace descriptor's selected branch. Sidebar side is the only layout preference added in this increment.
- Interpreting terminal content or managing Pi sessions.
- Shared production runtime code, sidecars, HTTP services, databases, or internal client/core protocols.

## Product Behavior

### Startup and activation

1. Open or create a valid workspace through the existing workspace foundation.
2. Resolve the workspace's selected branch to a registered worktree.
3. Discover all registered worktrees beneath the project root.
4. Show the sidebar with the selected worktree active.
5. Create that worktree's two terminals lazily as activation completes.

If the selected worktree cannot be resolved, show actionable recovery, keep the project terminal-free, and do not select a different worktree automatically.

### Switching

1. User selects an available sidebar entry.
2. gabCode updates selected worktree context.
3. If that worktree has no terminal pair, create its two terminals in the exact worktree folder.
4. If it has retained terminals, restore their views without restarting processes.
5. Restore focus to that worktree's primary terminal, matching the existing selected-project terminal focus behavior.

### Refresh

1. User invokes **Refresh Worktrees**.
2. gabCode performs bounded, cancellable, read-only Git discovery.
3. Available entries are reconciled while retained terminal ownership remains unchanged.
4. A newly missing entry becomes temporarily unavailable; the active missing entry remains active and shows recovery.
5. An entry still missing on the following explicit refresh leaves the worktree list. Any retained pair moves to **Orphaned terminals** without process termination.
6. If the same normalized path returns while unavailable or orphaned, restore its prior terminal pair automatically.
7. Focus and selection are preserved unless the user explicitly selects another available worktree or orphaned terminal pair.

## Technical Considerations

- Implement Windows and macOS independently in their native clients: C#/WPF and Swift/SwiftUI/AppKit.
- Reuse the workspace v1 descriptor and existing project activation contracts. No workspace schema change is planned. Store sidebar side only in platform-owned per-user preferences.
- Use installed Git directly with argument-safe invocation of `git worktree list --porcelain`.
- Treat Git as authoritative for registered worktree identity and paths. Local UI state may retain terminal sessions and temporary unavailable presentation but does not replace Git authority.
- Model terminal ownership by the stable normalized worktree path for the session. Switching UI context, temporary unavailability, or sidebar removal must not terminate or redirect a terminal.
- Bound discovery, cancellation, stale-result handling, output capture, and cleanup on both platforms.
- Preserve native accessibility: keyboard navigation, visible focus, accessible folder/branch labels, active/unavailable state announcements, refresh progress/errors, and ordinary terminal input.
- Test real temporary Git repositories with multiple registered worktrees, spaces/Unicode, missing selected branches, refresh races, process retention, and cleanup.

## Platform Differences

Windows and macOS must provide the same observable behavior and vocabulary, but use native controls and process facilities.

- Windows uses WPF sidebar/list controls, UI Automation/Narrator evidence, and ConPTY terminal ownership already established by the foundation.
- macOS uses SwiftUI/AppKit navigation/accessibility, VoiceOver evidence, and SwiftTerm/Unix PTY ownership already established by the foundation.
- One platform's terminal or accessibility evidence never proves the other platform.

## Milestones

1. **Worktree domain and discovery** — Represent discovered worktrees, primary/available/unavailable states, stable selection, and bounded read-only Git reconciliation.
2. **Native sidebar navigation** — Add the platform-native sidebar, ordering, labels, active/unavailable states, keyboard behavior, and explicit Refresh Worktrees action.
3. **Retained terminal pairs** — Create terminal pairs lazily per selected worktree, preserve processes/output across switching, restore views, and maintain close/quit cleanup.
4. **Cross-platform verification** — Run real temporary-Git tests, full platform builds/tests, adversarial lifecycle review, and separate Windows/macOS target evidence.

## Resolved Product Decisions

- An unavailable worktree remains in the sidebar through one explicit refresh cycle. If it is still absent on the next explicit refresh, its sidebar entry is removed.
- Retained terminals from a removed sidebar entry move to a selectable **Orphaned terminals** section and remain alive until explicitly closed or application quit; selecting one provides terminal access but no authoritative Git worktree context.
- A worktree returning at the same normalized path while unavailable or orphaned automatically regains its retained terminal pair.
- Returning to an available worktree restores focus to that worktree's primary terminal.
- The sidebar defaults left, can move left or right through the native View command, and remembers that side locally without rewriting the workspace descriptor.

## Approval Boundary

This PRD is approved for `/pm-agent` planning. Production implementation requires a source-aware parent sprint and separate Windows/macOS implementation plans. Passing tests and commits remain implementation evidence; human acceptance is required.
