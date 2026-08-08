# gabCode Projects and Worktrees PRD

| Field | Value |
| --- | --- |
| Status | Approved for Project Foundation; later milestones remain proposed |
| Date | 2026-08-06 |
| Related baseline | `Documentation/design/gabcode-initial-prd.md` |

This focused PRD refines the project-entry and worktree-lifecycle direction in the initial baseline. It is a product artifact, not an approved implementation sprint.

## Product Name & One-Liner

**gabCode Projects and Worktrees** — a Git-native project workspace that makes parallel branch work simple to open, create, switch between, retain, and safely clean up.

## Problem & Audience

A developer working across several branches or pull requests needs a clear way to return to a project, move between its worktrees, and clean up work after a pull request merges. Git worktrees are capable but make navigation, retained terminal processes, and safe removal cumbersome—especially when a terminal or external editor still holds a worktree open.

The initial audience is an individual developer using Git repositories, VS Code, and CLI coding tools such as Pi across Windows and macOS.

## Core Features

1. **Workspace-backed project entry** — A gabCode project is represented by a user-chosen `*.gabcode-workspace` file containing a required human-readable workspace name and one project-folder reference. The reference may be relative to the workspace file or absolute, matching VS Code workspace path behavior. The folder must belong to one Git repository. Creating a project from a new folder initializes Git; creating one from an existing non-Git folder offers to initialize Git.
   *Must-have*

2. **Direct return to the active project** — gabCode opens directly to the last project. Project selection remains available through application navigation rather than requiring a project-library home screen.
   *Must-have*

3. **Worktree discovery and navigation** — A project has a primary worktree and its Git-registered sibling worktrees. A compact switcher in the project header supports quick switching; an on-demand management panel exposes fuller worktree state and actions. The native window title is `<workspace name> — <selected context> — gabCode`; switching worktrees updates the selected context to the worktree directory name, with its folder name as the fallback.
   *Must-have*

4. **Retained terminals per worktree** — Each terminal is owned by the worktree for which it was created and starts in that worktree's mapped project folder. Switching worktrees retains its processes while its views are inactive. Running `cd` may change a terminal's current directory but never changes the project, selected worktree, or terminal ownership. A CLI tool such as Pi therefore continues running and is available when the developer switches back. gabCode hosts ordinary terminals; it does not inspect or manage Pi sessions.
   *Must-have*

5. **Advisory terminal location awareness** — When a shell or foreground application reports its working directory through supported terminal metadata, gabCode displays that last-reported directory and whether it is inside the terminal's assigned worktree. Unknown and outside-worktree states are explicit. If the reported directory belongs to another worktree in the same repository, gabCode may offer **Switch to This Worktree**; if it belongs to another repository, gabCode may offer **Open Current Directory as Project**, preferably in a new window. Both actions require explicit user intent.
   *Should-have*

6. **New-branch worktree creation** — The primary creation flow asks for a new branch name, creates that branch from the repository's configured primary branch, and creates its worktree.
   *Must-have*

7. **Existing-branch worktree creation** — An optional picker creates a worktree for an existing local or remote branch.
   *Should-have*

8. **Guided worktree cleanup** — gabCode helps remove stale or merged worktrees without silently killing work. It tracks its own terminals; for external tools, Git is the authority on whether removal can proceed. When Git rejects removal, gabCode presents that failure and tells the developer to close applications using the folder before retrying.
   *Must-have*

9. **Explicit PR association and status** — The developer can explicitly associate a worktree with a pull request. gabCode shows the associated PR's read-only open, merged, or closed status but never initiates cleanup automatically.
   *Should-have*

10. **PR review worktrees** — A later flow can create a worktree from a pull request for review. This follows, rather than precedes, the foundational project and branch-based worktree lifecycle.
   *Nice-to-have*

11. **Popped-out worktrees** — A later flow can open a worktree in its own project window. Closing the popped-out window asks for confirmation, then gracefully stops its terminals.
   *Nice-to-have*

## Workspace File v1 — Approved Project Foundation Contract

This language-neutral artifact is shared requirements only. Windows and macOS each implement their own parser and runtime behavior; it creates no shared production library, service, or protocol.

```json
{
  "version": 1,
  "name": "gabCode Development",
  "folders": [
    { "path": "../repository" }
  ]
}
```

- The file is UTF-8 JSON with exactly the properties `version`, `name`, and `folders`; unknown properties are rejected in v1.
- `version` is the integer `1`; unsupported versions are rejected.
- `name` is a required, non-empty human-readable string. It is workspace identity, not inferred from a filename or path.
- `folders` contains exactly one object with exactly one required, non-empty string property, `path`.
- `path` may be relative to the workspace file's directory or native-absolute. Runtime behavior resolves it to an absolute directory without rewriting a manually opened relative value.
- When creating a workspace file, gabCode writes a relative path if it is representable from the file location on the same filesystem root; otherwise it writes an absolute path.
- A successful Project Foundation open requires the resolved path to be an accessible directory inside an existing Git repository, established through a bounded, direct invocation of installed Git. This increment never runs `git init`.
- Malformed JSON, missing/unknown properties, wrong value types, empty name/path, multiple/empty folders, unsupported versions, inaccessible/non-directory paths, missing/unusable Git, and non-repository folders fail without starting a terminal.
- Worktree discovery/state, terminal state/output, selected worktree, Git status, PR data, and local preferences are not workspace-file fields. The user may keep the file local or commit/share it; gabCode does not edit `.gitignore`.
- The local last-workspace preference stores only a path hint and is revalidated with these same rules on every startup/open.

On successful activation, the native and accessible title is `<workspace name> — <selected context> — gabCode`. The initial selected context is the resolved project folder's final path component; a later worktree-navigation increment changes it to the selected worktree directory name, falling back to its folder name. The full resolved path belongs in project chrome/help, not the title. Title and terminal state change only after complete activation succeeds; a failed open or replacement preserves the existing title and terminal pair. Both terminals are then created with the exact normalized resolved folder as their starting directory.

## Non-Goals

- General non-Git project types.
- Multiple project folders or repositories in one workspace file initially.
- A multi-project workspace in one application window.
- Automatically changing the window's project, selected worktree, or terminal ownership because terminal focus or directory changes.
- Guaranteeing a live terminal working directory for every shell and foreground process.
- Automatic or forced process termination during normal worktree cleanup.
- Automatic worktree deletion because a pull request merged.
- PR-derived creation in the initial project/worktree foundation.
- Detached worktrees, custom base commits, or other advanced Git creation modes initially.
- Interpreting, starting, resuming, or otherwise managing Pi sessions.
- Replacing Git, GitHub, VS Code, or the user's shell.

## Technical Considerations

- Implement each native client independently: C#/WPF on Windows and Swift/SwiftUI/AppKit on macOS. Shared artifacts are requirements and language-neutral expected outcomes, not runtime code.
- Resolve relative project-folder references from the workspace file's location and accept absolute references. Preserve the path form chosen by the user and use resolved absolute paths at runtime.
- The workspace file is the stable project identity. It may live anywhere and may be shared or kept local; gabCode does not require it to be stored in the repository.
- The workspace name is stored explicitly rather than inferred from the workspace filename or project folder. Before worktree navigation exists, the title's selected context is the project folder's final path component. The full resolved path remains available in project chrome/help rather than being placed in the native title.
- Invoke installed Git directly. `git worktree list --porcelain`, Git worktree add/remove operations, and filesystem observation are authoritative. Discovered worktree paths and terminal state do not belong in the workspace file; local application state records preferences such as the last-opened workspace.
- Determine the selected folder's offset within its containing worktree at runtime so the same project folder can be addressed in sibling worktrees.
- Model terminal/process lifetime by worktree. Switching UI context must not terminate a terminal; terminal navigation must not redirect gabCode actions; removing a worktree must first establish that removal is safe and permitted.
- Treat terminal working-directory reports as advisory metadata. Preserve and label the last valid report, expose an unknown state, and never use a stale or unavailable report as implicit authority for Git or filesystem actions.
- Track gabCode-owned terminals directly. For external tools, rely on Git's removal result, present its failure, and require the user to resolve the blocker rather than claiming gabCode can always identify the locking process.
- Determine the configured primary branch from Git repository configuration and refs. When it remains ambiguous, ask the developer once to choose a branch and remember that choice locally for the project.
- Validate Windows and macOS behavior separately on their target operating systems.

## Milestones

1. **Project Foundation** — Create and open single-folder `*.gabcode-workspace` projects, support relative and absolute folder references, offer Git initialization for existing non-Git folders, start terminals in the project folder, and reopen the last workspace.
2. **Worktree Navigation** — Discover Git worktrees, establish the primary-worktree concept, and implement header switching plus the management panel.
3. **Terminal Retention** — Retain terminal processes by worktree across navigation and restore their views on return.
4. **Terminal Location Awareness** — Display supported last-reported terminal directories and add explicit same-repository switch and other-repository open actions without allowing terminal focus or navigation to redirect the window implicitly.
5. **Branch-Based Worktree Lifecycle** — Create new-branch worktrees from the configured primary branch, add existing-branch selection, and implement guided blocker-first removal.
6. **Follow-on Workflows** — Consider PR review worktrees and popped-out worktree windows after the core lifecycle is proven.

## Project Context Decision

A gabCode window is bound to one explicit workspace rather than continuously following the focused terminal's directory. This model was selected because retained terminals, worktree cleanup, and Git actions require a stable repository context even when several terminals have different directories or a foreground tool such as Pi is running.

The following alternatives were considered:

- **Continuously follow the focused terminal** — This removes project setup and makes repository discovery feel immediate, but terminal working-directory reporting is shell- and process-dependent. Focusing another terminal could unexpectedly replace the entire worktree context, and stale directory information could misdirect Git or cleanup actions.
- **Discover from a terminal and automatically pin** — This avoids continuous context changes, but still makes reliable terminal reporting a prerequisite for entering a project and leaves the pinning transition implicit.
- **Explicit workspace with advisory terminal shortcuts — chosen** — The workspace provides stable identity and ownership. Terminal location reports remain useful for orientation and can offer explicit actions to switch to another known worktree or open another repository without becoming authoritative.

This decision prioritizes predictable retained-process ownership, safe lifecycle operations, and independent Windows/macOS behavior over zero-setup terminal-driven navigation.

## Resolved Product Decisions

- A project is identified by a user-chosen `*.gabcode-workspace` file, not by the active terminal's current directory.
- The initial workspace contains a required human-readable name and one project folder belonging to one Git repository.
- The native window title is `<workspace name> — <selected context> — gabCode`; the selected context begins as the project folder name and later follows the selected worktree directory name.
- Workspace folder references may be relative or absolute, matching VS Code's path model; relative references resolve from the workspace file.
- A terminal remains owned by its creating worktree when its current directory changes, and terminal navigation never changes gabCode's selected project or worktree.
- Closing a popped-out worktree window asks for confirmation and then gracefully stops its terminals.
- PR status is shown only for a developer-selected worktree-to-PR association; it is read-only and never triggers automatic cleanup.
- gabCode tracks its own terminals but does not promise to identify external lock holders. Git's removal result is authoritative and its reported failure is shown to the developer.
- gabCode uses Git configuration and refs to resolve the primary branch. If that remains ambiguous, it asks the developer once and stores the selection locally per project.
