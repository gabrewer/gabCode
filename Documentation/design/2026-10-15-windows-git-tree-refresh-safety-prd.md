# Windows Git Tree Refresh Safety

| Field | Value |
| --- | --- |
| Date | 2026-10-15 |
| Status | Windows implementation in progress in sprint #119; native acceptance not checked |
| Sprint | [Windows Git Tree Refresh Safety #119](https://github.com/gabrewer/gabCode/issues/119) |
| Target | Windows native client (C#/WPF) |
| Scope | Opening-time and subsequent Git tree/review refresh safety |

## Product Name & One-Liner

**gabCode Windows Git Tree Refresh Safety** — Keep Windows and gabCode usable during automatic Git refresh, stopping expensive work safely rather than requiring the user to kill the application.

## Problem & Audience

A developer opening a local directory in gabCode on Windows reported that the automatic Git tree refresh locked up the computer. They had to kill gabCode to restore usability.

Reported facts and uncertainties:

- The trigger was opening a directory, not clicking Refresh.
- Local `main` was behind `origin/main` at the time.
- Updating local `main` appeared to resolve the problem.
- The user has not deliberately reproduced the incident. The exact expensive operation, resource bottleneck, repository size, and role of branch divergence are unknown.
- Branch lag is an investigation lead, **not a confirmed root cause**. Being behind a remote is ordinary repository state and must not require repair to use gabCode safely.

The smallest useful outcome is safe automatic refresh with bounded work and honest fallback states. Responsiveness takes priority over freshness. The user should not need to risk another machine lockup to supply initial evidence.

## Core Features

### 1. Bounded refresh work — Must-have

Place enforceable limits on refresh duration, child-process concurrency, captured output, queued work, and in-memory/UI materialization so opening a repository cannot launch uncontrolled work.

- Cover the entire refresh lifecycle: scheduling, Git execution, parsing, snapshot construction, and WPF presentation—not just each Git command's timeout.
- Keep expensive work off the UI thread, but do not treat background execution alone as sufficient protection against machine-wide resource exhaustion.
- Apply finite aggregate limits across windows within a gabCode process, not only per view. Account for multiple application instances when selecting and validating limits.
- Load commit details and large result sets on demand or in bounded pages. Never silently present a truncated tree as complete.
- Exact numeric budgets and target-machine responsiveness thresholds must be established during investigation and recorded in the implementation plan before the safety behavior is considered verifiable.

### 2. No refresh pileups — Must-have

Coalesce opening, watcher, focus, periodic, and manual refresh requests through coordinated scheduling rather than allowing overlapping replacement work to accumulate.

- For a given refresh scope, keep at most one running refresh and one pending invalidation representing newer state.
- Cancellation does not free a running slot until owned work and processes have actually stopped.
- Use bounded scheduling across scopes. A continuous event stream must not cause an endless cancel/restart loop or an ever-growing dispatcher queue.
- Apply results only to the worktree and generation that requested them.

### 3. Safe stop and recovery — Must-have

Stop work automatically when a safety budget is exceeded, and let the user cancel an active refresh without terminating gabCode.

- Cancellation must reach child processes, parsing, and pending UI work; cleanup itself must have a finite deadline.
- Stop only processes owned by the refresh. Never kill user terminals, unrelated Git commands, or other applications.
- After a safety stop, pause automatic refresh for the affected scope so watcher, focus, and timer events cannot immediately repeat the overload. Show an explicit Retry action that uses the same limits; successful retry restores normal automatic updates.
- Worktree switching, workspace closing, and application shutdown must not leave orphan refresh workers or publish late results.
- Normal terminal retention and exit-confirmation behavior remain unchanged.

### 4. Honest, non-blocking tree state — Must-have

Retain the last successful snapshot for the same worktree when refresh stops, clearly marking it out of date; show an unavailable state when no successful snapshot exists.

- Show a lightweight loading state without taking focus or blocking unrelated navigation and terminal use.
- Example safety-stop message: “Git refresh stopped to keep the app responsive. Automatic refresh is paused.” Add “Showing previous results” only when previous results exist.
- Provide keyboard-reachable Cancel while running and Retry after stopping. Keep the warning local to the affected view rather than repeatedly opening dialogs.
- Preserve selection and expansion where identities remain valid. Never show a previous worktree's data as the current worktree's snapshot.
- Expose loading, stale/unavailable, paused, and error states through native UI Automation; do not rely on color alone or announce every watcher event.

### 5. Safe handling of lagging and large histories — Must-have

Opening a directory remains safe when the configured local comparison branch is far behind a remote-tracking branch, when history is large or divergent, or when Git is slow or unresponsive.

- Preserve the existing comparison against configured local `project.mainBranch`. Do not silently substitute `origin/main`, special-case the name `main`, or require it to be checked out.
- This safety fix must not add automatic fetch, pull, reset, merge, rebase, branch advancement, or repository repair as a workaround.
- Manual Refresh must use the same safety controls as automatic refresh; it is not an escape from resource limits.

### 6. Bounded diagnostic evidence — Should-have

Provide enough local diagnostic information to identify the expensive phase without collecting repository contents or creating another unbounded workload.

Record refresh trigger, phase, duration, process/concurrency counts, output/result counts, cancellation or budget-stop reason, and cleanup outcome. Bound diagnostic storage and avoid source contents, diffs, credentials, and unnecessary identifying paths. No hosted telemetry service is required.

## Non-Goals

- macOS implementation or claims about macOS behavior.
- A new Git graph, sidebar redesign, or change to comparison semantics.
- Automatically updating local branches or remote-tracking refs to avoid the incident.
- Disabling all automatic refresh permanently as the finished solution.
- Killing or restarting terminals, or managing CLI harness sessions.
- A shared runtime, sidecar service, database, or new internal protocol.
- Claiming a root cause or a fix from the reported workaround alone.

## Technical Considerations

Use the existing Windows C#/WPF client, direct Git invocation, native cancellation/process ownership, and existing test surfaces. Extend existing safeguards where evidence shows gaps rather than replacing the refresh architecture by assumption.

### Existing context and investigation entry points

- [Initial product requirements](2026-07-24-gabcode-initial-prd.md) — native ownership, Git authority, bounded tool execution, and terminal safety.
- [Worktree Change Review Sidebar](2026-09-20-worktree-change-review-sidebar-prd.md) — existing automatic refresh, lazy commit details, bounded queries, and stale-result behavior. This PRD hardens Windows safety without redefining shared Git semantics.
- `src/GabCode.Windows/MainWindow.xaml.cs` — directory/workspace opening and refresh entry points.
- `src/GabCode.Windows/Projects/GitWorktreeDiscovery.cs` — repository discovery and Git process paths involved in opening.
- `src/GabCode.Windows/Projects/WorktreeReviewSidebar.cs` — review scheduling and native tree presentation.
- `src/GabCode.Windows/Projects/WorktreeChangeReviewCoordinator.cs` — cancellation, generations, and retained snapshots.
- `src/GabCode.Windows/Projects/WorktreeChangeReviewService.cs` — Git queries, history pages, output limits, and cleanup.
- `src/GabCode.Windows/Projects/WatcherInvalidation.cs` — existing constant-space invalidation hints.
- `tests/GabCode.Windows.Tests/Projects/` — existing discovery, review coordinator/service/boundary, and watcher tests.

### Initial read-only investigation (2026-10-15)

Code inspection, not a reproduction or root-cause confirmation:

- Opening a workspace calls `ActivateProject`, which selects the review sidebar and starts its refresh; it also starts a separate worktree reconciliation (`MainWindow.xaml.cs`). Thus the incident can occur without clicking Refresh.
- Each review snapshot runs `git status --untracked-files=all`, then a Git history page using `git log --max-count=100 --shortstat HEAD ^<local-main-head>` (`WorktreeChangeReviewService.cs`). The first page is bounded by count and output, but `--shortstat` requests per-commit change statistics and may do significant history/diff work before returning. A far-behind configured local main ref can enlarge the unique-commit range *if the selected HEAD contains those commits*; lag behind `origin/main` alone does not prove that it did. The service does not query `origin/main` on this path.
- `WorktreeReviewSidebar.cs` uses one active read and one pending flag per sidebar for normal refreshes, but each window owns its own sidebar and service. While a read runs, repeated triggers set the pending flag; after completion it immediately runs again. There is no cross-window refresh budget, aggregate timeout, safety pause, or user-facing Cancel for the review refresh. A per-Git-command 10-second timeout and 2 MiB output limit exist, but they do not cap total repeated work or UI layout.
- The review tree is rebuilt synchronously on the WPF dispatcher, creating controls for visible working files and the fetched commit page. There is no explicit bound on retained pages/rows after repeated Load more operations. This is a plausible UI-pressure path, not proof of the reported lockup.
- Git child cleanup calls `Kill(entireProcessTree: true)` and then awaits `WaitForExitAsync(CancellationToken.None)` with no cleanup deadline. Disposal/selection cancels the current coordinator token but does not await process exit before a new selection can start. This warrants controlled lifecycle tests, not an assertion that a process leaked in the reported incident.
- Existing tests cover a 250-commit paged history, timeout/oversized output, stale-result suppression, and watcher invalidation. They do not measure machine resource peaks, multi-window concurrency, persistent watcher bursts, or bounded shutdown under a stuck child.

No affected directory was opened, no reproduction attempted, and no repository Git refs were changed. Focused Windows tests (`WorktreeChangeReviewCoordinatorTests` and `WatcherInvalidationTests`) passed 7/7 via `dotnet test tests/GabCode.Windows.Tests/GabCode.Windows.Tests.csproj --configuration Release --filter 'FullyQualifiedName~WorktreeChangeReviewCoordinatorTests|FullyQualifiedName~WatcherInvalidationTests' --no-restore`; these do not validate machine-wide responsiveness.

### Required verification outcomes

Use disposable local repositories and controllable child-process test doubles before considering the user's affected directory. No network access or update of the user's branches is required to construct lagging remote-tracking refs.

| Scenario | Required outcome |
| --- | --- |
| Open with local comparison branch far behind its remote-tracking ref; repeat with equal and divergent refs | Window and Windows remain usable; bounded work completes or stops visibly; refs remain unchanged by the review refresh |
| Large history, many changed files, or oversized command output | Declared budgets hold; no unbounded capture/materialization; incomplete data is explicitly identified |
| Watcher/focus/manual refresh bursts and multiple windows | Active and queued work stay within declared limits; no cancel/restart storm or starvation |
| Slow/hung Git, cancellation, or a safety limit | Owned refresh processes stop within the cleanup budget; automatic retry does not recreate the overload; Retry remains bounded |
| First load fails versus later refresh fails | Unavailable state versus retained same-worktree snapshot marked stale; no misleading empty-success state |
| Switch worktrees or close during refresh | No stale-generation publication, orphan refresh process, or unrelated terminal termination |
| Keyboard/Narrator use during loading and recovery | Cancel/Retry are reachable, status is understandable, and refresh does not steal focus |

Windows evidence must record hardware/OS, fixture scale, configured budgets, peak gabCode-plus-refresh-child resource use, maximum process/queue counts, UI responsiveness, and cancellation-to-cleanup time. Repeated cycles must show stable resource use, not accumulating workers or retained allocations. Use an independent harness stop condition to protect the test machine. A timeout-only or mocked scheduling test is not sufficient proof of machine responsiveness.

Runtime, resource, cleanup, and accessibility checks are **NOT CHECKED** in this PRD-writing session. Passing future automated tests will be implementation evidence, not human acceptance or proof that the original incident was reproduced.

## Milestones

1. **Trace and characterize safely** — Audit the directory-opening refresh path and existing guards; create isolated lagging-history and overload cases; identify measured bottlenecks or explicitly retain the root cause as unknown. Define numeric budgets and a safe Windows measurement procedure.
2. **Enforce bounded lifecycle** — Implement and test coordinated scheduling, resource/work bounds, cancellation, owned-process cleanup, and protection from automatic retry storms.
3. **Deliver usable fallback and recovery** — Wire loading, Cancel, stale/unavailable states, paused automatic refresh, and bounded Retry into the native view without disrupting terminals or focus.
4. **Validate on Windows** — Exercise the verification matrix with target-machine resource and responsiveness evidence, adversarial review, and a human acceptance checklist. Keep any unreproduced incident or unverified safety claim explicit.

These are product milestones, not an execution task board. [Windows sprint #119](https://github.com/gabrewer/gabCode/issues/119) owns the implementation-ready task board, measured budget gate, native interaction details, dependencies, and verification. The sprint issue records the current engineering budgets, implementation evidence, and outstanding native acceptance checks. The current directory-discovery budget is a user-requested three-second cooperative cancellation deadline with no folder-count cap; it is not a guarantee that a blocking filesystem call or process cleanup finishes within three seconds.

## Open Questions

- Which phase actually caused the reported machine-wide loss of responsiveness: Git processing, process overlap, memory pressure, WPF layout/materialization, or another path?
- What finite duration, process, output, row/memory, scheduling, and cleanup budgets preserve useful refresh on representative Windows hardware? What measured responsiveness threshold will gate acceptance?
- What repository/history shape reproduces the problem safely, and does local-branch lag affect it causally or merely correlate with a larger comparison range?
- Are additional OS-level child-process resource controls necessary after application-level work limits are measured? The implementation must justify its protection, not assume moving work off-thread is enough.
