# Workspace-opening conformance fixtures

These fixtures define language-neutral inputs and expected outcomes for the unreleased workspace-v1 correction in `Documentation/design/workspace-opening-prd.md`. They are requirements/conformance data only: neither native client imports them at runtime.

## Format

`cases.json` contains a `schemaVersion` and an ordered `cases` array. Each case has:

- `id` — stable identifier for native test names.
- `descriptorJson` — UTF-8 JSON text exactly as supplied by the workspace file. It remains text so malformed JSON and incorrect JSON types can be represented.
- `repository` — the read-only Git/discovery facts after resolving the project path. `localRefs` contains local `refs/heads/...` names only; `remoteRefs` is deliberately separate so a remote-tracking ref cannot satisfy `mainBranch` validation. A registered worktree has a stable fixture `id`, whether it is Git's `primary` entry, and whether its normalized path is currently `accessible`.
- `rememberedWorktreeId` — the platform-owned, revalidated selected-worktree hint, or `null` when no hint exists.
- `expected` — validation and selection result. `heading` is the exact native recovery heading when opening fails. `fallbackNotice` is the exact user-visible and assistive announcement when stale memory falls back.

A native test substitutes real temporary-repository paths for fixture worktree IDs, but must preserve these outcomes. An `open` outcome means the descriptor and local main ref are valid and the listed `selectedWorktreeId` is the sole worktree eligible for subsequent lazy terminal creation. Fixture outcomes do not authorize terminal creation, descriptor rewriting, Git mutation, or preference writes.

## Required interpretation

- A descriptor accepts exactly top-level `version`, `name`, and `project`, and exactly `path` and camel-case `mainBranch` beneath `project`.
- `mainBranch` is valid only if `refs/heads/<mainBranch>` occurs in `localRefs`; `remoteRefs` never substitutes for it.
- A remembered worktree is usable only if it remains both registered and accessible. Missing remembered state silently selects an accessible primary worktree. Stale remembered state selects the accessible primary worktree and emits the exact fallback notice.
- An inaccessible primary worktree, no primary worktree, or no accessible registered worktree fails before ready publication with `Workspace could not be opened`.
- Descriptor-shape and configured-main-branch failures use `Invalid workspace file` and include the descriptor path in the native recovery surface.
