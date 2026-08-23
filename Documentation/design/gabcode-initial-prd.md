# gabCode — Initial Product Requirements Document

| Field | Value |
| --- | --- |
| Status | Initial baseline |
| Version | 0.4 |
| Date | 2026-08-02 |

This document defines gabCode's initial product boundary and direction. It should evolve when foundational product decisions change. Substantial new capabilities should receive focused PRDs under `Documentation/design` rather than turning this document into an exhaustive specification.

The approved independent-native-client decision is recorded in `Documentation/design/independent-native-clients-prd.md`. Windows and macOS share product requirements and conformance cases, but no production runtime code or companion service.

## Product Name & One-Liner

**gabCode** is a native Windows and macOS desktop application for navigating Git worktrees and observing the work happening inside each one without replacing the user's CLI coding harness, GitHub, Git, or VS Code.

## Problem & Audience

A developer using CLI coding harnesses across multiple Git worktrees needs to move quickly between independent pieces of work. VS Code can open each worktree, but it does not provide one clear place to see all worktrees, their terminals, current changes, commits, requirements, and GitHub issues.

gabCode is initially for a single developer who:

- Works locally on Windows and macOS.
- Uses one Git repository per project with several worktrees.
- Runs a CLI coding harness and other command-line tools inside those worktrees.
- Uses PRDs under `Documentation/design` and GitHub issues to describe work.
- Uses VS Code when source files need to be edited.

gabCode makes worktrees easy to navigate and observe. It does not need to understand the process producing the work.

## Core Features

### 1. Worktree-Centered Project Navigation — Must-have

A gabCode project maps to one project root and one Git worktree set. The project root itself does not need to be a Git repository. A conventional layout is configurable:

```text
project/
├── main/
└── wt/
    ├── wt-feature-one/
    └── wt-feature-two/
```

- Discover the repository by searching the selected project root and supported descendants for exactly one `.git` repository, then discover its registered worktrees through `git worktree list --porcelain`; Git is authoritative for branch-to-worktree resolution.
- The repository's primary checkout is itself a worktree even when the project does not use additional worktrees. Only branches currently represented by registered worktrees are selectable in v1; ordinary local branches without worktrees are not silently treated as folders.
- Let the user select the branch/worktree to activate. List every branch returned by Git's worktree data, and default the picker to `main` when that exact branch is present; `main` otherwise has no special behavior.
- Present worktrees as the primary sections in the application sidebar.
- Allow the sidebar to move between the left and right sides.
- Show factual status such as branch, clean or dirty state, ahead or behind state, last commit, changed-file count, and running terminals.
- Scope terminals, files, commits, changes, PRDs, and issues to the selected worktree.
- Refresh when Git or the filesystem changes.

### 2. Workspace Identity and Branch Selection — Must-have

- Create or open a `*.gabcode-workspace` descriptor at the project root, including when that root is not itself a Git repository.
- A workspace records the project-root path and the user-selected branch/worktree identity; it does not infer identity from a folder named `main`.
- Resolve the selected branch to its current Git worktree using read-only Git queries before starting terminals. A normal repository checkout is a valid primary worktree; additional worktrees appear only when registered with Git.
- If the project root, repository discovery, or selected branch cannot be resolved, show actionable recovery and start no terminals.
- Keep the descriptor independent of terminal state, output, Git status, and local preferences.
- Opening or creating a workspace launches a separate native project window; it never stops or replaces another window's project or terminals. Existing terminal cleanup remains authoritative for close and quit.

### 3. Guarded Worktree Creation and Removal — Must-have

Users can create and remove secondary worktrees without leaving gabCode.

Creation supports:

- Creating a worktree for a new or existing branch.
- Selecting the base branch or commit.
- Choosing the branch and folder names.
- Defaulting the location under the project's configured `wt` directory.
- Using `git worktree add` as the source of behavior.

Removal must:

- Use `git worktree remove`; never delete the directory directly.
- Prevent removal of the primary `main` worktree.
- Refuse normal removal when uncommitted changes exist.
- Warn clearly when commits have not been pushed.
- Never use forced removal silently; force removal requires an explicit secondary recovery confirmation.
- Stop associated terminals only after explicit confirmation.
- Never delete a branch by default. An explicit worktree-removal flow may offer deletion of the associated local branch after successful worktree removal and confirmation; remote branches are never deleted.

A worktree disappears from gabCode when it is no longer returned by Git. gabCode keeps no archive of removed worktrees.

### 4. Two Native Terminals per Worktree — Must-have

Each opened worktree has two independent, generic terminals. gabCode does not prescribe a CLI harness, build, test, Git, or any other role for either terminal; the user may run any local command in either terminal.

Both terminals:

- Start in the selected worktree.
- Are created lazily when the worktree is first opened.
- Remain alive while gabCode runs, including while navigating to other worktrees.
- Preserve their process when moved between the main area and bottom panel.
- Have bounded in-memory scrollback.
- Do not persist output after gabCode exits.

gabCode does not inspect terminal content, understand commands issued to a CLI harness, track harness session identifiers, or start or resume harness sessions. Both terminals are ordinary user-controlled shells.

On Windows, terminals use the user's configured Windows Terminal default profile when it can be resolved. The fallback order is a gabCode-configured shell, `pwsh`, Windows PowerShell, then `cmd.exe`. On macOS, terminals use the user's configured login shell.

gabCode always warns before exiting when terminal processes are active. After confirmation, it attempts graceful termination, waits briefly, and then terminates remaining process trees.

### 5. Worktree Changes and Commit Visibility — Must-have

For the selected worktree, gabCode shows:

- Uncommitted files as **Work in progress**.
- Added, changed, deleted, and renamed files.
- Commits unique to the worktree branch relative to the configured primary branch.
- Commit message, SHA, author, timestamp, and file statistics.
- A read-only diff for each commit.
- A cumulative comparison against the worktree's base.

For worktrees created by gabCode, the creation point may be recorded as additional local context. Git remains authoritative when history is amended, rebased, or otherwise rewritten.

gabCode does not stage, commit, amend, rebase, merge, push, or otherwise mutate repository history through this interface.

### 6. User-Selected PRD and GitHub Issue Associations — Must-have

The user can associate one or more PRDs and GitHub issues with a worktree.

- PRDs are selected from files under `Documentation/design` in that worktree.
- GitHub issues are selected from the repository's read-only GitHub issue data.
- Associations are stored as local gabCode metadata.
- gabCode does not infer, create, or maintain associations automatically.
- gabCode does not modify PRD files or GitHub issues when an association changes.
- Associated PRDs, issue bodies, and issue comments are readable inside gabCode.
- Associations are discarded when their worktree is removed.

Explicit links already present in PRDs and GitHub issues may be rendered as links, but gabCode does not treat them as workflow instructions.

### 7. Read-Only Files, Documents, and Diffs — Must-have

The selected worktree provides read-only navigation for:

- Repository files and directories.
- Markdown PRDs.
- Associated GitHub issues and comments.
- Uncommitted changes.
- Commits and diffs.

Opening a document replaces the terminal currently occupying the main area and retains that same running terminal in the bottom-panel workflow without assigning it a special role. Closing the document can promote the retained terminal back to the main area.

### 8. Open in VS Code — Must-have

An **Open in VS Code** action is available from:

- The file explorer.
- Commit file lists.
- Read-only diffs.
- Uncommitted changes.
- Locations linked from displayed review findings or issue comments.

The action opens the current worktree's file, preferably at the relevant line and column. It uses an existing worktree `.code-workspace` file when available and otherwise opens the worktree folder. Historical commit contents remain read-only in gabCode; VS Code opens the current worktree version of the file. Deleted files do not offer this action.

### 9. Safe Multiline Terminal Paste — Must-have

Single-line clipboard text is pasted immediately. Multiline clipboard text requires a fresh native confirmation with a short preview before gabCode sends it to the terminal. Approval forwards the original text unchanged; cancellation sends nothing. See `Documentation/design/windows-terminal-safe-multiline-paste-prd.md`.

### 10. Local Tool Diagnostics and Layout Persistence — Should-have

- Detect `git`, `gh`, VS Code, and user-configured CLI harness executables without installing or updating them.
- Report executable paths, versions, and `gh` authentication state.
- Disable only actions affected by a missing prerequisite.
- Remember registered projects, path overrides, sidebar side and width, panel sizes, selected worktree, and user-selected associations.
- Keep all application metadata local to the user's machine.

## Non-Goals

gabCode will not:

- Edit source files or PRDs.
- Interpret CLI coding-harness output, prompts, skills, or session history.
- Start or resume CLI coding-harness sessions automatically.
- Understand or enforce product-design, sprint, review, or release workflows.
- Infer which PRD or GitHub issue belongs to a worktree.
- Create, update, close, or comment on GitHub issues.
- Create, review, merge, or otherwise mutate pull requests.
- Stage, commit, amend, rebase, merge, or push.
- Delete branches implicitly or as part of normal worktree removal. An explicit guarded worktree-removal action may offer local-branch deletion after confirmation; remote branches are never deleted.
- Force-remove dirty worktrees silently or without an explicit recovery confirmation.
- Archive worktrees after Git removes them.
- Support multiple repositories inside one project in the initial version.
- Provide hosted execution, team synchronization, or remote terminals.
- Replace VS Code, GitHub, Git, the user's CLI coding harness, or the user's shell.
- Install or update local developer tools.

## Technical Considerations

### Application Architecture

- **Windows application:** A complete C#/WPF application that owns native UI, terminal hosting, direct tool integration, normalized state, watching/reconciliation, local data, diagnostics, cancellation, and cleanup.
- **macOS application:** A complete Swift/SwiftUI/AppKit application that owns the equivalent behavior with native macOS APIs and conventions.
- **Shared repository artifacts:** Product requirements, vocabulary, language-neutral conformance inputs, and expected normalized outcomes—not production runtime code.
- **Local metadata:** Platform-owned per-user, per-project storage; no repository metadata files are required.

There is no gabCode companion sidecar or internal client/core protocol. Neither native application embeds, launches, packages, or requires the other platform's runtime. Each platform implements and validates its behavior independently.

### Workspace Descriptor Contract

The workspace descriptor is a language-neutral artifact owned by the user. The current project-root model is:

```json
{
  "version": 1,
  "name": "gabCode Development",
  "project": {
    "path": "..",
    "branch": "trunk"
  }
}
```

- `name` is required and non-empty.
- `project.path` is relative to the descriptor when relative, or a native absolute path.
- `project.path` may be a non-Git project root containing the repository's worktrees.
- `project.branch` is an ordinary branch name; `main`, `master`, and `trunk` have no special behavior.
- Git worktree data resolves the branch to the terminal working directory at activation time.
- Git and the filesystem remain authoritative; terminal state, status, output, and preferences do not enter the descriptor.
- This is the initial workspace descriptor contract; no existing workspace files require migration.

### Windows Terminal Implementation

Intended direction:

- WPF host.
- A pinned build of Microsoft's Windows Terminal WPF control.
- ConPTY for local processes.
- A small gabCode wrapper around the terminal dependency.
- Resolve the user's effective Windows Terminal default profile when practical.
- Override the profile's starting directory with the selected worktree.

The terminal dependency should be built and versioned by gabCode rather than relying on an unofficial third-party package. Windows Terminal WPF tag `v1.24.11911.0` is approved for Windows x64 integration under `Documentation/dependencies/windows-terminal-wpf.md`.

For the initial Windows product, keyboard-only focus escape from terminal content, terminal search, hyperlink activation, and dedicated Narrator, IME, high-contrast, text-scaling, or reduced-motion qualification of the upstream terminal surface are not requirements. GabCode still owns the ConPTY adapter and every shell/process resource that gabCode creates; automatic connection shutdown by the upstream wrapper is not expected.

### macOS Terminal Implementation

Intended direction:

- SwiftUI application with AppKit terminal hosting.
- SwiftTerm `LocalProcessTerminalView`.
- Unix PTY local processes.
- The user's configured login shell.

### Git, GitHub, and Filesystem Integration

- Each native application invokes the installed `git` executable directly so behavior matches the user's command line.
- Use structured Git output, including `git worktree list --porcelain`.
- Each native application invokes the installed `gh` executable directly and uses its authenticated session only for approved read-only GitHub data.
- Each platform uses native filesystem and Git-reference watching for responsive updates, backed by bounded periodic reconciliation.
- Treat watcher events as invalidation hints and Git and the filesystem as authoritative; local metadata only adds user-selected associations and preferences.
- Resolve executables and pass arguments without unsafe shell-string interpolation; bound output, timeouts, cancellation, diagnostics, and cleanup on each platform.

### Key Technical Risks

- Packaging and maintaining the Windows Terminal WPF control.
- Preserving terminal processes while reparenting their views.
- Running many worktree terminal pairs without unbounded memory use.
- Resolving Windows Terminal profiles, including dynamic profiles and unusual shells.
- Unicode, ANSI/VT, clipboard, resize, and native terminal hosting behavior. The approved Windows control's focus-escape, search, hyperlink, and dedicated terminal-accessibility limitations are accepted rather than release risks.
- Reliable process-tree shutdown on Windows and macOS.
- Preventing factual Git/`gh` normalization and safety behavior from drifting between the independent C# and Swift implementations.
- Windows signing and macOS signing/notarization.

## Milestones

Each milestone is delivered through separate Windows and macOS implementation increments with target-operating-system evidence. Shared behavior vocabulary and language-neutral conformance cases should be established before duplicated Git/`gh` normalization grows independently; one platform's evidence never proves the other platform.

### Milestone 1: Native Terminal Implementation Sprint

Deliver production-foundation prototypes on both platforms:

- Windows WPF terminal using the Windows Terminal control and ConPTY.
- macOS terminal using SwiftTerm and Unix PTY.
- Two terminals rooted in a selected worktree.
- Several worktrees with terminal pairs alive simultaneously.
- The same generic terminal moving between the main and bottom areas without process restart.
- Default Windows Terminal profile and macOS login-shell startup.
- Unicode, ANSI color, selection, clipboard, resize, and bounded scrollback validation.
- Mandatory exit warning and graceful process-tree shutdown.
- Recorded native-host and UI Automation evidence, with the approved Windows terminal-content limitations treated as non-blocking.

### Milestone 2: Projects and Worktree Navigation

Deliver:

- Project-root registration and configurable discovery of the repository/worktree set beneath it; no directory name is reserved for the primary branch.
- Branch/worktree selection and resolution through Git.
- Worktree-centered movable sidebar.
- Worktree status and automatic refresh.
- Selected-worktree context switching.
- Guarded worktree creation and removal.
- Persistent local project and layout settings.

### Milestone 3: Work Visibility and Associations

Deliver:

- Read-only file explorer.
- Work-in-progress changes.
- Worktree commit feed and cumulative diffs.
- PRD selection and rendering.
- Read-only GitHub issue selection, bodies, and comments.
- Local worktree-to-PRD and worktree-to-issue associations.

### Milestone 4: VS Code Integration and Initial Release

Deliver:

- File- and line-level **Open in VS Code** actions.
- Worktree workspace discovery.
- Final document/terminal/bottom-panel layout behavior.
- Toolchain diagnostics.
- Performance and process-lifecycle hardening.
- Signed Windows package and notarized macOS package.

## Open Questions

- Windows Terminal WPF `v1.24.11911.0` is approved for Windows x64 integration with the accepted limitations in `Documentation/dependencies/windows-terminal-wpf.md`. SwiftTerm's macOS suitability remains to be decided through the separate macOS dependency gate.
