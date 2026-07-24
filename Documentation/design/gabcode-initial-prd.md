# gabCode — Initial Product Requirements Document

| Field | Value |
| --- | --- |
| Status | Initial baseline |
| Version | 0.1 |
| Date | 2026-07-24 |

This document defines gabCode's initial product boundary and direction. It should evolve when foundational product decisions change. Substantial new capabilities should receive focused PRDs under `Documentation/design` rather than turning this document into an exhaustive specification.

## Product Name & One-Liner

**gabCode** is a native Windows and macOS desktop application for navigating Git worktrees and observing the work happening inside each one without replacing Pi, GitHub, Git, or VS Code.

## Problem & Audience

A developer using Pi across multiple Git worktrees needs to move quickly between independent pieces of work. VS Code can open each worktree, but it does not provide one clear place to see all worktrees, their terminals, current changes, commits, requirements, and GitHub issues.

gabCode is initially for a single developer who:

- Works locally on Windows and macOS.
- Uses one Git repository per project with several worktrees.
- Runs Pi and command-line tools inside those worktrees.
- Uses PRDs under `Documentation/design` and GitHub issues to describe work.
- Uses VS Code when source files need to be edited.

gabCode makes worktrees easy to navigate and observe. It does not need to understand the process producing the work.

## Core Features

### 1. Worktree-Centered Project Navigation — Must-have

A gabCode project maps to exactly one Git repository. The default directory convention is configurable:

```text
project/
├── main/
└── wt/
    ├── wt-feature-one/
    └── wt-feature-two/
```

- Discover registered worktrees through `git worktree list --porcelain`.
- Present worktrees as the primary sections in the application sidebar.
- Allow the sidebar to move between the left and right sides.
- Show factual status such as branch, clean or dirty state, ahead or behind state, last commit, changed-file count, and running terminals.
- Scope terminals, files, commits, changes, PRDs, and issues to the selected worktree.
- Refresh when Git or the filesystem changes.

### 2. Guarded Worktree Creation and Removal — Must-have

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
- Never use forced removal silently.
- Stop associated terminals only after explicit confirmation.
- Never delete the associated branch.

A worktree disappears from gabCode when it is no longer returned by Git. gabCode keeps no archive of removed worktrees.

### 3. Two Native Terminals per Worktree — Must-have

Each opened worktree has two independent terminals:

- **Pi** — an ordinary shell intended for running the Pi CLI.
- **Commands** — an ordinary shell for builds, tests, Git, and other commands.

Both terminals:

- Start in the selected worktree.
- Are created lazily when the worktree is first opened.
- Remain alive while gabCode runs, including while navigating to other worktrees.
- Preserve their process when moved between the main area and bottom panel.
- Have bounded in-memory scrollback.
- Do not persist output after gabCode exits.

gabCode does not inspect terminal content, understand Pi commands, track Pi session identifiers, or run `pi --resume`. Both terminals are ordinary user-controlled shells.

On Windows, terminals use the user's configured Windows Terminal default profile when it can be resolved. The fallback order is a gabCode-configured shell, `pwsh`, Windows PowerShell, then `cmd.exe`. On macOS, terminals use the user's configured login shell.

gabCode always warns before exiting when terminal processes are active. After confirmation, it attempts graceful termination, waits briefly, and then terminates remaining process trees.

### 4. Worktree Changes and Commit Visibility — Must-have

For the selected worktree, gabCode shows:

- Uncommitted files as **Work in progress**.
- Added, changed, deleted, and renamed files.
- Commits unique to the worktree branch relative to the configured primary branch.
- Commit message, SHA, author, timestamp, and file statistics.
- A read-only diff for each commit.
- A cumulative comparison against the worktree's base.

For worktrees created by gabCode, the creation point may be recorded as additional local context. Git remains authoritative when history is amended, rebased, or otherwise rewritten.

gabCode does not stage, commit, amend, rebase, merge, push, or otherwise mutate repository history through this interface.

### 5. User-Selected PRD and GitHub Issue Associations — Must-have

The user can associate one or more PRDs and GitHub issues with a worktree.

- PRDs are selected from files under `Documentation/design` in that worktree.
- GitHub issues are selected from the repository's read-only GitHub issue data.
- Associations are stored as local gabCode metadata.
- gabCode does not infer, create, or maintain associations automatically.
- gabCode does not modify PRD files or GitHub issues when an association changes.
- Associated PRDs, issue bodies, and issue comments are readable inside gabCode.
- Associations are discarded when their worktree is removed.

Explicit links already present in PRDs and GitHub issues may be rendered as links, but gabCode does not treat them as workflow instructions.

### 6. Read-Only Files, Documents, and Diffs — Must-have

The selected worktree provides read-only navigation for:

- Repository files and directories.
- Markdown PRDs.
- Associated GitHub issues and comments.
- Uncommitted changes.
- Commits and diffs.

Opening a document replaces Pi in the main area and moves the same running Pi terminal into the bottom panel. Closing the document can promote Pi back to the main area.

### 7. Open in VS Code — Must-have

An **Open in VS Code** action is available from:

- The file explorer.
- Commit file lists.
- Read-only diffs.
- Uncommitted changes.
- Locations linked from displayed review findings or issue comments.

The action opens the current worktree's file, preferably at the relevant line and column. It uses an existing worktree `.code-workspace` file when available and otherwise opens the worktree folder. Historical commit contents remain read-only in gabCode; VS Code opens the current worktree version of the file. Deleted files do not offer this action.

### 8. Local Tool Diagnostics and Layout Persistence — Should-have

- Detect `git`, `gh`, `pi`, and VS Code without installing or updating them.
- Report executable paths, versions, and `gh` authentication state.
- Disable only actions affected by a missing prerequisite.
- Remember registered projects, path overrides, sidebar side and width, panel sizes, selected worktree, and user-selected associations.
- Keep all application metadata local to the user's machine.

## Non-Goals

gabCode will not:

- Edit source files or PRDs.
- Interpret Pi output, prompts, skills, or session history.
- Start or resume Pi sessions automatically.
- Understand or enforce product-design, sprint, review, or release workflows.
- Infer which PRD or GitHub issue belongs to a worktree.
- Create, update, close, or comment on GitHub issues.
- Create, review, merge, or otherwise mutate pull requests.
- Stage, commit, amend, rebase, merge, push, or delete branches.
- Force-remove dirty worktrees through normal application actions.
- Archive worktrees after Git removes them.
- Support multiple repositories inside one project in the initial version.
- Provide hosted execution, team synchronization, or remote terminals.
- Replace VS Code, GitHub, Git, Pi, or the user's shell.
- Install or update local developer tools.

## Technical Considerations

### Application Architecture

- **Windows UI:** WPF with C#.
- **macOS UI:** SwiftUI with AppKit where needed.
- **Shared core:** C# NativeAOT sidecar process.
- **UI/core communication:** Versioned, source-generated JSON messages over standard input and output.
- **Local metadata:** A small per-user, per-project local store; no repository metadata files are required.

The shared core owns project configuration, Git and GitHub command execution, normalized worktree state, file watching, and local associations. Each platform UI owns native windows, menus, keyboard behavior, and terminal views.

### Windows Terminal Implementation

Intended direction:

- WPF host.
- A pinned build of Microsoft's Windows Terminal WPF control.
- ConPTY for local processes.
- A small gabCode wrapper around the terminal dependency.
- Resolve the user's effective Windows Terminal default profile when practical.
- Override the profile's starting directory with the selected worktree.

The terminal dependency should be built and versioned by gabCode rather than relying on an unofficial third-party package.

### macOS Terminal Implementation

Intended direction:

- SwiftUI application with AppKit terminal hosting.
- SwiftTerm `LocalProcessTerminalView`.
- Unix PTY local processes.
- The user's configured login shell.

### Git, GitHub, and Filesystem Integration

- Use the installed `git` executable so behavior matches the user's command line.
- Use structured Git output, including `git worktree list --porcelain`.
- Use the installed `gh` executable and its authenticated session for read-only GitHub data.
- Use filesystem and Git-reference watchers for responsive updates, backed by periodic reconciliation.
- Treat Git and the filesystem as authoritative; local metadata only adds user-selected associations and preferences.

### Key Technical Risks

- Packaging and maintaining the Windows Terminal WPF control.
- Preserving terminal processes while reparenting their views.
- Running many worktree terminal pairs without unbounded memory use.
- Resolving Windows Terminal profiles, including dynamic profiles and unusual shells.
- Unicode, ANSI/VT, IME, clipboard, search, hyperlink, resize, and accessibility behavior.
- Reliable process-tree shutdown on Windows and macOS.
- NativeAOT trimming and serialization constraints in the shared core.
- Windows signing and macOS signing/notarization.

## Milestones

### Milestone 1: Native Terminal Implementation Sprint

Deliver production-foundation prototypes on both platforms:

- Windows WPF terminal using the Windows Terminal control and ConPTY.
- macOS terminal using SwiftTerm and Unix PTY.
- Two terminals rooted in a selected worktree.
- Several worktrees with terminal pairs alive simultaneously.
- Pi moving between the main and bottom areas without process restart.
- Default Windows Terminal profile and macOS login-shell startup.
- Unicode, ANSI color, selection, clipboard, hyperlinks, search, resize, and bounded scrollback validation.
- Mandatory exit warning and graceful process-tree shutdown.
- Documented accessibility findings.

### Milestone 2: Projects and Worktree Navigation

Deliver:

- Project registration and configurable `main`/`wt` discovery.
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
- Final document/Pi/bottom-panel layout behavior.
- Toolchain diagnostics.
- Performance and process-lifecycle hardening.
- Signed Windows package and notarized macOS package.

## Open Questions

- Do the intended native terminal components—Microsoft's Windows Terminal WPF control and SwiftTerm—meet gabCode's requirements for compatibility, process preservation, memory use, accessibility, and maintainability? Milestone 1 will answer this through implementation and validation.
