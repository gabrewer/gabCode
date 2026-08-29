# gabCode Workspace Opening PRD

| Field | Value |
| --- | --- |
| Status | Approved product correction; implementation tracked by #81 |
| Date | 2026-08-23 |
| Related baseline | `Documentation/design/gabcode-initial-prd.md` |
| Refines | `Documentation/design/projects-and-worktrees-prd.md`, `Documentation/design/worktree-navigation-prd.md` |
| Conformance fixtures | `tests/fixtures/workspace-opening/cases.json` |

This focused PRD corrects the unreleased workspace-file contract and workspace-opening behavior. Where those documents describe `project.branch` as the worktree selected during workspace opening, this PRD replaces that behavior with the required `project.mainBranch` and local last-worktree selection described below.

## Product Name & One-Liner

**gabCode Workspace Opening** — open a project from its workspace file without confusing the repository's configured main branch with the worktree the developer last selected.

## Problem & Audience

A developer may check out any branch in the repository's primary worktree while moving among several registered worktrees. That normal Git operation must not make the gabCode workspace impossible to open.

The unreleased workspace contract currently treats `project.branch` as both branch metadata and the worktree to activate. Both native clients therefore reject a workspace when no registered worktree currently reports that branch. The failure can also be invisible when opening through startup or file association. This couples durable project identity to temporary worktree selection and makes an ordinary branch checkout look like a broken workspace.

This correction is for gabCode developers on Windows and macOS who expect a workspace to reopen their last selected worktree while preserving an explicit conventional main-branch name for worktree actions.

## Core Features

### 1. Correct workspace-file identity — Must-have

A workspace file supplies the fields gabCode currently needs; it may contain additional fields:

```json
{
  "name": "gabCode Development",
  "project": {
    "path": "../project",
    "mainBranch": "main"
  }
}
```

- `project.mainBranch` replaces `project.branch`; it is required, non-empty, and uses camelCase.
- `project.mainBranch` names the repository's conventional main branch, whether that branch is called `main`, `master`, `trunk`, or something else.
- The named branch must exist as a local branch under `refs/heads`; a remote-tracking branch alone is insufficient.
- The main branch does not need to be checked out and does not need to have its own registered worktree.
- `project.path`, not `project.mainBranch`, identifies the project root from which gabCode discovers the single repository/worktree set.
- `version` is neither required nor interpreted for parsing. Unknown top-level and `project` properties are ignored so future fields do not break older clients.
- There is no fallback from the former `project.branch` shape; `project.mainBranch` remains required.

### 2. Worktree-independent opening — Must-have

Opening a workspace validates the file, resolves `project.path`, discovers exactly one Git repository/worktree set, and validates `project.mainBranch` as a local branch. It never matches `project.mainBranch` to `git worktree list` to decide which worktree to open.

The branch currently checked out in the repository's primary worktree, the primary worktree's folder name, and the configured main-branch name may all differ. That difference is normal and must not prevent opening.

After validation, gabCode selects a worktree using local remembered selection and Git's registered worktree data. Only then may it activate the project and lazily start that worktree's terminals.

### 3. Remembered worktree selection with a safe fallback — Must-have

- Each native client remembers the last available worktree selected for a workspace in platform-owned per-user preferences.
- The remembered normalized worktree path is keyed to the workspace file and is a hint, not repository authority.
- Reopening selects that path only when Git still reports it as a registered, accessible worktree in the discovered set.
- With no remembered selection, gabCode opens Git's primary worktree.
- If the remembered worktree is no longer available, gabCode opens Git's primary worktree and visibly reports: `The previously selected worktree is no longer available. Opened <worktree> instead.`
- Selecting the fallback does not rewrite the workspace file.
- Worktree switching updates the local remembered selection without changing `project.mainBranch`.

### 4. Visible, actionable opening failures — Must-have

A workspace-opening failure must never be silent.

- Malformed JSON, missing required values, wrong types for required values, and a missing local `project.mainBranch` show an **Invalid workspace file** heading with the specific reason and workspace-file path.
- Inaccessible project paths, unavailable Git, failed repository discovery, multiple discovered repositories, or no accessible registered worktree show a visible **Workspace could not be opened** error with the specific reason.
- No terminal starts and no partial project activation occurs after failure.
- Opening from the native menu, remembered startup state, command line, or file association must all leave a native recovery surface visible long enough to read and act on the error.
- A failed open in a new window or process must not alter or stop terminals in an already occupied project window.

A stale remembered worktree is not an invalid workspace file. It uses the primary-worktree fallback and notice defined above.

### 5. Main-branch creation and actions — Must-have

**Create Workspace** retains its existing simple branch picker, but labels the choice as the project's main branch and writes it as `project.mainBranch`. The choices are local branches; the selected branch does not need a registered worktree.

Actions described as operating **from main** use the validated `project.mainBranch`. They do not use the branch currently checked out in the primary worktree or the branch of the selected worktree unless that action explicitly says otherwise.

## Non-Goals

This correction does not:

- Automatically detect, fetch, create, check out, rename, or repair a main branch.
- Accept a remote-tracking branch as a substitute for the configured local main branch.
- Store selected worktree paths in the workspace file.
- Rewrite existing workspace files automatically.
- Treat the former `project.branch` property as a substitute for `project.mainBranch`.
- Change the existing one-project-root and one-repository/worktree-set boundary.
- Add detached-worktree navigation, automatic filesystem watchers, or new worktree lifecycle actions.
- Define special recovery for every unusual missing or inaccessible primary-worktree condition; those cases fail visibly in this increment.
- Introduce shared production runtime code, a sidecar, service, database, or internal client/core protocol.

## Technical Considerations

- Implement the behavior independently in the C#/WPF Windows client and Swift/SwiftUI/AppKit macOS client.
- Share only the workspace JSON requirements, vocabulary, language-neutral fixtures, and expected outcomes.
- Parse by the required fields `name`, `project.path`, and `project.mainBranch`; ignore `version` and unknown fields.
- Invoke installed Git directly with argument-safe, bounded, cancellable commands. Validate the configured local branch as `refs/heads/<mainBranch>` without requiring a worktree match.
- Continue to use `git worktree list --porcelain` and the filesystem as authority for available worktree identities and paths.
- Treat the locally persisted last-worktree path as revalidated presentation state. It must never create a competing repository identity or override Git discovery.
- Persist only available registered worktree selections. Unavailable and orphaned terminal presentation remains governed by the existing navigation lifecycle.
- Preserve native keyboard access and expose errors and fallback notices to Narrator on Windows and VoiceOver on macOS.
- Test opening when the primary worktree is checked out to a feature branch while `mainBranch` remains `main`; this is the regression case that motivated the correction.

## Milestones

1. **Contract correction** — Replace `project.branch` with required `project.mainBranch` in the authoritative requirements and language-neutral workspace fixtures; define local-branch validation and worktree-independent selection outcomes.
2. **Windows behavior** — Update the WPF workspace parser, creation flow, opening workflow, local selection persistence, visible recovery, and focused tests; validate on Windows.
3. **macOS behavior** — Independently update the Swift workspace parser, creation flow, opening workflow, local selection persistence, visible recovery, and focused tests; validate on macOS.
4. **Parity evidence** — Exercise equivalent workspace files and Git layouts on each target operating system, including remembered, missing-remembered, invalid-main-branch, startup, and file-association paths.

## Open Questions

None block this narrow correction. Automatic default-branch detection, post-release schema migration, and richer recovery for an inaccessible Git primary worktree are deliberately deferred until real usage requires them.
