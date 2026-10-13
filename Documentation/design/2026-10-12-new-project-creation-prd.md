# gabCode New Project Creation PRD

| Field | Value |
| --- | --- |
| Status | Product design drafted; implementation sprint not yet planned |
| Date | 2026-10-12 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related project foundation | `Documentation/design/2026-08-08-projects-and-worktrees-prd.md` |
| Workspace contract | `Documentation/design/2026-08-24-workspace-opening-prd.md` |

## Product Name & One-Liner

**gabCode New Project** — create a Git-ready project using either gabCode's suggested main/worktrees folder layout or a simple repository layout, then open it as a native workspace.

## Problem & Audience

A developer starting from nothing must currently create folders, initialize Git, establish the initial branch, and create a workspace descriptor manually before gabCode can open a project. An empty Git repository also has no local branch ref until its first commit, so configuring `mainBranch: "main"` fails validation even when Git's unborn `HEAD` symbolically points at `main`.

New Project guides this initial setup without requiring one universal directory convention. The developer can request gabCode's worktree-ready arrangement or choose a flat repository, and gabCode remembers the last layout choice locally. Git and the filesystem remain authoritative after creation.

## Decisions and Relationship to Existing Requirements

The user agreed to offering the main/worktrees pattern without requiring it, remembering the last layout choice, and automatically creating an empty initial commit for a new project. The defaults and safety details below are proposed for review; implementation is not authorized by this document.

This revisits the project-creation proposal previously deferred in GitHub #61 (Windows #62; macOS #63). Upon approval, this focused PRD supersedes the earlier prohibition on folder creation, `git init`, and a single empty initialization commit **only within New Project**. General commit/diff review remains read-only. Existing workspace parsing, branch validation, and arbitrary Git-authoritative layouts remain unchanged. Implementation planning must align the baseline and agent guardrails with this narrow exception before production work.

## Core Features

### 1. Separate New Project entry point — Must-have

- Add **New Project…** as a distinct action from **Create Workspace…**.
- Create Workspace remains descriptor-only and non-mutating for existing Git worktree sets.
- New Project explicitly creates the requested project folder/layout and initializes Git. The UI states that it will make these changes before execution.
- Existing-project opening behavior remains unchanged. This increment does not silently initialize an arbitrary folder selected through Open Workspace or Create Workspace.

### 2. Two creation layouts — Must-have

Offer two named, understandable choices:

1. **Worktree-ready (suggested)** creates:
   ```text
   <chosen parent>/<project name>/
   ├── main/     # primary Git worktree; folder is a layout label, not branch authority
   └── wt/       # initially empty; default destination for later linked worktrees
   ```
   Git metadata remains owned by Git in `main/.git`. `wt/` is an ordinary empty destination directory, not a worktree and not authority about which branch is primary.
2. **Simple repository** creates:
   ```text
   <chosen parent>/<project name>/  # primary Git worktree
   ```

The project-name field supplies a suggested new folder name and workspace display name; both remain editable before creation. The chosen parent folder must already exist and be writable. Do not silently merge with or overwrite an existing target directory. Validate the final target against path traversal, invalid names, symlink/reparse redirection and placement inside another Git worktree; reject ambiguous or unsafe targets rather than initializing a nested repository.

The layout is only a creation-time convention. Discovery, primary-worktree identity, branch identity, and later navigation continue to come from Git; no runtime behavior assumes folders named `main` or `wt`.

### 3. Remember the last layout choice — Must-have

- Store the last successfully created New Project layout in platform-owned per-user preferences. Merely toggling a choice, cancelling, or failing before creation completes does not replace the remembered choice. Opening an existing project does not change it.
- For a user with no saved choice, preselect **Worktree-ready (suggested)**.
- Show the selected layout clearly each time and permit changing it before creation.
- The preference is presentation state only: it is not written into the workspace descriptor or repository.
- Windows and macOS implement their own local preference mechanisms; no shared runtime or cross-device synchronization.

### 4. Initialize a usable repository — Must-have

- Initialize the primary worktree as a non-bare Git repository using Git's configured initial-branch behavior (including `init.defaultBranch` and Git-version defaults). Do not force or rename the branch to `main`. Resolve the actual unborn initial branch from the newly initialized repository's symbolic `HEAD` and validate it before writing the workspace descriptor.
- Create exactly one initial **empty commit** with a clear message such as `Initial commit`. It contains no generated/source files and is the only commit New Project creates.
- Configure no remote, GitHub repository, identity, starter files, `.gitignore`, license, template, package, editor, or terminal command.
- Before mutation, validate required Git availability and the selected destination, and present the exact project folder/layout and empty-commit behavior for confirmation.
- If Git author name/email are missing or unusable, stop before creating project content and offer actionable guidance to configure identity; do not invent or persist an identity.
- If the user cancels before creation starts, create nothing.

The empty commit is an explicit creation-time initialization exception. Opening and reviewing an existing workspace never commits or modifies Git state.

### 5. Create and open the workspace — Must-have

- After repository initialization and the empty commit succeed, create the existing workspace-v1 descriptor using the resulting primary worktree/project root and the actual configured initial branch as `mainBranch`. The descriptor's `mainBranch` means the configured conventional/mainline branch; it does not require the literal name `main`.
- In Worktree-ready layout, the workspace project root is the container directory so discovery finds the repository under the `main/` layout folder; in Simple layout, it is the repository directory.
- The `.gabcode-workspace` descriptor is gabCode's small settings file containing workspace name, project path, and configured main branch. Follow existing descriptor naming conventions; show its destination and allow choosing/editing its name/location before creation. Proposed default: `<project folder>/<project name>.gabcode-workspace`, outside the repository root in Worktree-ready layout and inside the repository in Simple layout. Publish it after the initial commit, never stage it automatically, and never modify `.gitignore`. In Simple layout it will appear as an untracked file.
- On successful descriptor creation, activate the project using the existing invoking-window routing contract: reuse an empty window or open a separate native window/instance when the current window is occupied. Never replace or stop another window's terminals.
- The project opens at Git's primary worktree and starts no extra linked worktrees. The `wt/` folder is simply ready for subsequent explicit worktree creation.

### 6. Partial failure, cancellation, and recovery — Must-have

Creation spans filesystem, Git initialization, commit, and descriptor publication, so failures must be visible and must not masquerade as success.

- Show a progress stage for folder creation, Git initialization, initial commit, descriptor creation, and activation. Keep cancellation available before mutation begins; after mutation begins, do not imply that cancellation rolls back already-created user data.
- If failure occurs after the project folder or repository has been created, never recursively delete it automatically. Leave user data intact and show the exact completed stages, target path, failure, and a safe next action (open the folder, retry a non-destructive remaining step, or dismiss).
- Do not automatically create a second initial commit on retry. Detect existing repository/commit/descriptor state and resume only a safe idempotent step, or stop and explain what the user can do manually.
- Do not overwrite a pre-existing path or workspace file. Resolve collisions before any mutation.
- Do not publish/update last-workspace preferences or claim the project opened until the descriptor exists and normal workspace validation succeeds.
- If activation fails after successful creation, preserve the created project and descriptor, present the opening error and a direct way to retry opening the descriptor.

### 7. Native accessibility and target behavior — Must-have

- All fields, layout choices, confirmation, progress, errors, and recovery actions are keyboard-accessible and expose native accessible names, roles, and state.
- Focus order is predictable; validation and errors are announced by Narrator/VoiceOver without relying on color or transient toasts.
- Validate high-contrast/contrast settings and 200%/platform-equivalent scaling; dialogs remain usable at supported minimum sizes.
- Windows and macOS have independent native interactions and target-machine evidence. One platform does not prove the other.

## Non-Goals

- A generic non-Git project, multi-repository workspace, bare-repository workflow, or custom repository/worktree layout editor.
- Initializing an existing arbitrary folder or completing setup for an existing empty/unborn repository. That recovery flow needs a separate explicit product decision; users may create the empty initial commit manually meanwhile.
- Creating linked worktrees automatically; `wt/` starts empty and later worktree operations remain explicit.
- Starter content, samples, `.gitignore`, license, branch other than `main`, remote, hosted/GitHub repository, or GitHub CLI operations.
- Git identity configuration or credential management.
- Automatic retries that could duplicate commits, overwrites, or destructive cleanup/rollback.
- Changing workspace-v1 schema, existing Open Workspace/Create Workspace behavior, or main-branch validation rules.
- Managing, interpreting, or starting Pi/other CLI sessions; terminals remain ordinary user-controlled shells.
- A shared production runtime, sidecar, internal client/core protocol, database, or web UI.

## Technical Considerations

- Git remains authoritative. Each native client independently invokes installed Git with structured arguments and bounded output/time/cancellation, without shell interpolation.
- Let installed Git resolve its configured default initial branch; do not impose a gabCode branch-name default. Before the first commit, the branch may exist only as an unborn symbolic `HEAD`, not as a `refs/heads/...` ref. After creating the empty initial commit, validate the resulting branch name, `HEAD`, local branch ref, empty root commit tree, and primary worktree using read-only queries before publishing the descriptor.
- A new directory and a Git repository are separate mutation stages. Failure recovery must account for partially created directories, `.git`, commits, and descriptors without unsafe broad rollback.
- A retry must distinguish: absent destination; empty directory created by this operation; initialized repository with unborn HEAD; initialized repository with the expected empty initial commit; and descriptor already published. Never infer ownership of an existing folder merely from its name.
- The initial empty commit requires the user's configured Git identity. It does not authorize gabCode to set local/global identity or to create any further commit. Respect effective signing and hook policy rather than bypassing it to force success; report failures and preserve resulting files. Bound Git children and their cleanup. Verify the initialization commit is a root commit with an empty tree, not a commit of template-generated or concurrently staged files.
- After a process restart, abandoned partial folders do not become implicitly owned by a new invocation. Offer manual recovery or normal Create Workspace once the repository is valid, not automatic adoption or deletion.
- Existing process/window routing must preserve occupied project windows and terminal identities.
- Validate with disposable real Git repositories and filesystem paths, including spaces and Unicode, collisions, absent Git identity, Git failure at each mutation stage, cancellation boundaries, and descriptor/activation failure.

## Acceptance Criteria

- [ ] A new project can be created in either layout without pre-running Git commands, assuming Git and usable identity/signing prerequisites are available.
- [ ] Worktree-ready produces a primary repository in `main/` and an empty `wt/`; Simple produces a repository directly at the project root. Neither preset creates a linked worktree.
- [ ] No saved preference selects Worktree-ready; a successful choice survives app restart. Cancellation, failure before creation completes, and opening an existing project do not overwrite the choice.
- [ ] Git reports exactly one empty root commit on the initial branch selected by the user's Git configuration; the workspace descriptor records that same branch. No source, workspace descriptor, or generated file has been staged or committed by gabCode.
- [ ] The existing workspace schema opens the resulting primary worktree. The descriptor records the actual Git-configured initial branch and resolves the chosen layout correctly; the remembered layout is absent from descriptor/repository metadata.
- [ ] Missing Git/identity, unsafe paths, destination and descriptor collisions fail visibly without modifying pre-existing data or global Git configuration.
- [ ] Injected initialization/commit/publication/activation failures report completed steps and leave recoverable data. Same-operation retry never creates a second initialization commit or overwrites externally changed state.
- [ ] Cancelling before mutation creates nothing. Cancellation or exit after mutation never claims rollback or deletes the project recursively; owned Git processes have bounded cleanup.
- [ ] Occupied windows retain their original project and terminal identities; a new project opens separately. An empty invoking window can activate the new project.
- [ ] Existing arbitrary-layout projects remain openable; Open Workspace and Create Workspace never initialize or commit. Existing unborn repositories are not silently repaired.
- [ ] Each native platform separately verifies keyboard/focus, progress/error announcements, screen-reader semantics, contrast, scaling, Git outcomes, retries and process cleanup. Missing checks are recorded as `NOT CHECKED`.

## Platform Delivery

This is genuinely cross-platform: the user-visible capability, Git mutation, recovery, workspace creation, and native UI must work on both supported operating systems. Plan one shared product/fixture prerequisite plus linked, independently verifiable Windows and macOS implementation sprints. Shared artifacts contain only requirements, vocabulary, conformance inputs, and expected outcomes; no shared production implementation. Merge the shared prerequisite through review and mainline before dependent platform work, then rebase each target branch and validate independently.

## Open Questions

1. **Resolved:** initial branch follows Git's configured default behavior; gabCode must not force `main`.
2. The parent directory must exist; New Project creates the project-name child. Should picking a nonexistent parent be supported by creating parent paths recursively? Default proposal: no, keep scope/collision behavior simple.
3. A workspace descriptor is gabCode's `.gabcode-workspace` settings file: it stores the workspace name, project path, and configured main branch so the project can be reopened. Proposed default: save `<project folder>/<project name>.gabcode-workspace`, with an editable location/name before creation. In the Simple layout this descriptor is consequently an untracked file inside the Git repository; in Worktree-ready layout it is outside the repository, in the containing project folder. The descriptor is never staged or committed automatically. Confirm this default or choose a different location.
4. Existing unborn-repository completion is explicitly excluded. Revisit only in a separate decision if a guided recovery action is still desired after New Project ships.

## Approval Boundary

This PRD describes intended product behavior and does not authorize implementation by itself. `/pm-agent` must create the GitHub-backed shared prerequisite and linked Windows/macOS implementation plan with exact current paths, build/test commands, failure-injection coverage, and target-OS evidence. Passing tests and commits are implementation evidence, not human acceptance.
