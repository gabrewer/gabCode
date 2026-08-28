# gabCode Worktree Actions PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-08-22 |
| Related baseline | `Documentation/design/projects-and-worktrees-prd.md` |
| Related navigation | `Documentation/design/worktree-navigation-prd.md` |
| Reliability follow-up | `Documentation/design/worktree-actions-reliability-prd.md` |

## Product Name & One-Liner

**gabCode Worktree Actions** — create and safely delete Git worktrees directly from gabCode.

## Problem & Audience

Developers working on multiple pieces of work need isolated project folders so they can switch tasks without stashing changes, reopening terminals, or disturbing another task. Git worktrees provide that isolation, but their command-line setup and cleanup are unfamiliar to many developers.

gabCode makes the common worktree lifecycle understandable: create a new worktree from the project's main branch, optionally create one from another branch, and safely delete worktrees when they are no longer needed. Git remains authoritative, but users interact with clear names, paths, previews, and confirmations instead of raw Git commands.

The audience is developers using gabCode who want parallel, isolated workspaces without needing to learn Git worktree commands or folder conventions.

## Core Features

### 1. Worktree context menu — Must-have

Right-clicking a worktree provides native, keyboard-accessible actions.

Primary lifecycle actions:

- **Create worktree from main** — primary creation action, based on the workspace's configured local main branch.
- **Create worktree from selected branch** — creates from the branch belonging to the selected worktree.
- **Create worktree from existing branch** — opens a branch picker for an existing local or remote-tracking branch.
- **Delete worktree** — removes the selected secondary worktree through Git.

Supporting convenience actions:

- **Open in VS Code** — opens the worktree in VS Code without creating or changing anything.
- **Reveal in Explorer/Finder** — opens the worktree folder in the platform file manager.

The primary worktree never offers Delete.

### 2. New worktree dialog — Must-have

The new-worktree dialog asks for a name, not a separate folder path by default. It shows a live preview of the resulting branch and folder:

- Input `billing-fix` defaults to branch `feature/billing-fix`.
- Existing `feature/`, `bugfix/`, or `hotfix/` prefixes are preserved according to the existing PowerShell convention.
- The generated branch name is shown as an editable preview.
- The generated location defaults beneath the configured `wt` directory using `wt-billing-fix`.
- The location is shown before creation and can be overridden through a native path field and Browse action.

The dialog validates branch names, paths, existing folders, branch conflicts, and worktree attachment conflicts before enabling Create. Branch and location previews remain visible and directly editable; they are not hidden behind an Advanced section.

### 3. Base branch and fetch choice — Must-have

**Create worktree from main** uses the workspace's configured local main branch. If that local branch may be behind its remote-tracking branch, the dialog offers **Use the latest remote version of the workspace main branch**.

Supporting text explains that gabCode will fetch before creating the new worktree and will not change any existing worktree checkout.

When selected:

1. gabCode fetches the configured workspace main branch from its configured remote.
2. The new branch is created from the updated remote-tracking ref, such as `origin/main`.
3. No existing worktree checkout is changed.

Fetch is the only update operation in this workflow. gabCode does not pull, merge, rebase, or otherwise modify the selected worktree as a side effect of creation. If fetch fails, the user may retry or explicitly continue from the configured local main branch. If no usable remote branch is configured, gabCode hides the latest-remote option and creates from the configured local main branch without treating the missing remote as an error.

**Create worktree from selected branch** uses the selected worktree's current branch as the base for the new branch. The selected branch is already checked out, so this action creates a new branch; it does not attempt to check out the same branch in two worktrees.

### 4. Existing branch picker — Should-have

The picker lists existing **local branches** and **remote branches** with search/filter support. Each entry shows whether the branch is already in use by a worktree and identifies that worktree when applicable.

- A branch already attached elsewhere cannot be selected for another worktree.
- Selecting a remote branch creates a local branch and worktree from that remote branch; the user does not need to understand tracking refs or detached HEADs.
- The picker may offer **Refresh remote branches**, with supporting text explaining that gabCode will contact the remote for newly available branches. It does not contact the remote by default.
- PR-number and fork-specific review creation are explicitly outside this increment.

### 5. Optional workspace and editor setup — Should-have

Creation offers two independent, unchecked-by-default options:

- **Create a VS Code workspace file** — generates a VS Code `*.code-workspace` file using the user's current workspace conventions, including the established optional color and title settings where supported. This is distinct from gabCode's `*.gabcode-workspace` descriptor.
- **Open in VS Code after creation** — opens the new worktree. If a VS Code workspace file was created and both options are selected, VS Code opens that file; otherwise it opens the worktree folder.

Neither action happens automatically when its option is not selected. gabCode does not launch VS Code merely because a worktree was created. Failure to create the optional VS Code workspace file does not roll back a successfully created worktree; gabCode reports the failure and offers recovery.

### 6. Safe deletion — Must-have

Delete uses `git worktree remove`; gabCode never deletes a worktree directory directly.

The confirmation identifies the worktree, branch, path, dirty state, and any gabCode-owned active terminals/processes. The normal path attempts safe removal first.

- If safe removal succeeds, the user may optionally choose **Also delete the local branch**, unchecked by default. The branch is deleted only after successful worktree removal.
- If Git blocks safe removal, gabCode presents **Force delete this worktree** as a secondary recovery action rather than a peer option in the initial dialog. It clearly warns that uncommitted and untracked files may be permanently lost.
- If branch deletion is requested for an unmerged branch, gabCode requires an additional explicit confirmation before using force-delete behavior. A remote branch is never deleted by this action.

If active gabCode terminals/processes exist, the user must explicitly approve stopping them before removal proceeds. External applications may still cause Git to reject removal; Git's result is authoritative and the error explains what the user must resolve.

### 7. Conflict and recovery handling — Must-have

If the requested generated branch already exists but is not attached to a worktree, gabCode offers either:

- create a worktree for that existing branch; or
- return to choose another branch name.

If the branch is attached elsewhere, creation is blocked and the existing worktree is identified. If the target folder exists, gabCode does not overwrite it and asks the user to choose another location.

All Git operations are cancellable, bounded, and reported with actionable errors. A failed operation must not leave a partially represented worktree in gabCode; refresh/reconciliation confirms Git's resulting state.

## Non-Goals

This increment will not:

- Create repositories, initialize Git, create project layouts, or create the primary worktree.
- Create worktrees directly from PR numbers, fork refs, or GitHub review metadata.
- Discover branches through GitHub or other remote APIs beyond the local branches and configured remote-tracking refs visible to Git.
- Pull, merge, rebase, push, commit, stage, or otherwise rewrite repository history.
- Delete remote branches.
- Automatically open VS Code or create workspace files without the user's selected option.
- Automatically delete worktrees after a PR changes state.
- Allow deletion of the primary worktree.
- Silently force-remove dirty worktrees or silently terminate terminals/processes.
- Use a sidecar, web service, database, or shared runtime protocol.
- Treat local gabCode metadata as authoritative over Git or the filesystem.

## Technical Considerations

### Native clients

Windows and macOS implement this independently in their complete native clients: C#/WPF and Swift/SwiftUI/AppKit. They share vocabulary, behavior requirements, and language-neutral conformance cases, not production runtime code.

Both platforms must expose equivalent creation and deletion decisions, warnings, and outcomes while using native menus, dialogs, path pickers, process APIs, and accessibility surfaces. The UI need not look identical, but both clients must provide the same creation modes, branch/path previews, fetch choice, conflict handling, deletion safeguards, and local-branch deletion option.

### Git operations

Invoke the installed `git` executable directly with argument-safe process APIs. Relevant operations include:

- `git worktree list --porcelain` for discovery and reconciliation.

The PRD defines safety rules and observable outcomes, not a mandatory command sequence. Each native client may use equivalent Git commands as required by its implementation and supported Git versions, provided Git remains authoritative and the resulting behavior matches this PRD.
- `git worktree add -b <branch> <path> <base>` for new branches.
- `git worktree add <path> <existing-branch>` for existing local branches.
- A safe remote-branch flow that creates a local branch tracking or based on the selected remote-tracking ref.
- `git fetch <remote> <main-ref>` when Fetch first is selected.
- `git worktree remove <path>` and `git worktree remove --force <path>` for deletion.
- Branch deletion only after successful worktree removal, using normal deletion first and explicit force deletion only after the additional unmerged-branch confirmation.

The branch used by **Create worktree from main** is the workspace's configured local `project.mainBranch`. It is not required to be named `main`; it is validated independently of the selected worktree and is not inferred from the primary checkout.

### Post-operation reconciliation

After every create, fetch-and-create, or delete operation, gabCode re-runs `git worktree list --porcelain` and reconciles the sidebar from Git's result before showing success or selecting a worktree. The UI must not maintain a competing worktree database or claim that an operation succeeded based only on the process exit path.

### Path and naming behavior

The current PowerShell convention is the product's initial default:

- plain name → suggested `feature/<name>`;
- `feature/`, `bugfix/`, and `hotfix/` prefixes remain intact;
- generated folder → `wt-<sanitized-name>` beneath the configured `wt` directory.

The implementation must treat the generated values as defaults, not immutable rules. Users can edit the full branch preview and override the location. Branch names and folder names are validated independently: a Git branch may contain `/`, while the worktree folder must satisfy native filesystem rules. The default folder is generated from a sanitized form of the name, but users can edit it separately. A future project preference may configure the branch-prefix convention; that preference is out of scope for this increment. Windows and macOS apply native path validation and display conventions while preserving Git branch names accurately.

### VS Code workspace file behavior

The optional VS Code workspace file should reuse the existing workspace-generation conventions where available. Creation must be atomic and report failure without claiming success. The worktree itself remains valid if optional VS Code workspace-file creation fails; the user receives a clear recovery action.

### Accessibility and lifecycle

The complete workflow must be keyboard accessible with native menus, dialogs, branch pickers, checkboxes, Browse controls, progress, errors, and confirmations. Focus must return predictably after creation, cancellation, fetch, VS Code launch, and deletion. Destructive choices must identify their consequence clearly without relying on color or icons; technical Git terminology is acceptable for this developer audience when the consequence is clear.

Process execution, cancellation, terminal shutdown, Git reconciliation, and UI updates require platform-specific lifecycle tests. The application must not leave an owned Git/fetch/branch process running after cancellation or exit.

## Milestones

1. **Creation domain and Git adapters** — Implement normalized creation inputs, naming/path previews, configured workspace-main-branch resolution, branch conflict classification, direct Git worktree-add flows, and refresh reconciliation.
2. **Native creation and branch-picker UI** — Add context menus, creation dialog, latest-remote option, existing local/remote branch picker, validation, keyboard paths, and optional workspace/VS Code actions.
3. **Guarded deletion** — Implement blocker-first deletion confirmation, active terminal/process approval, force-removal warning, optional local branch deletion, unmerged-branch confirmation, and recovery/error handling.
4. **Cross-platform verification** — Test real temporary repositories with clean/dirty/untracked/unmerged branches, local and remote refs, spaces/Unicode, conflicts, fetch failures, cancellation, active processes, and optional editor/workspace actions separately on Windows and macOS.

## Open Questions

None at the product-definition level. Platform-specific generation details and implementation choices are planning work.
## Acceptance Boundary

This PRD defines product behavior for a focused worktree-actions increment. It is not an implementation approval. Planning must create target-platform increments and evidence separately for Windows and macOS. Passing tests and commits remain implementation evidence; human acceptance is required.
