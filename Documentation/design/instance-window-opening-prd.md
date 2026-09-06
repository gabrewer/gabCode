# Instance Window Opening PRD

## Product Name & One-Liner

**gabCode instance-aware workspace opening** — A new gabCode window opens the workspace explicitly requested by the user, while a plain additional window starts empty instead of duplicating the last restored project.

## Problem & Audience

A developer such as Gabrielle may have a gabCode window open for one repository and launch another gabCode window to work elsewhere. Today, a plain second process can restore the last workspace, creating a confusing duplicate of the existing project. Conversely, an explicit `.gabcode-workspace` request must not be discarded in favor of restored state.

## Core Features

1. **Explicit workspace launch routing** — A process launched with exactly one valid workspace-file argument opens that exact workspace in its own window. **Must-have.**
2. **First-window restore** — The first gabCode window in a process may use the local last-workspace preference, subject to existing validation/recovery rules. **Must-have.**
3. **Additional plain-window empty state** — A plain additional launch detects an already-live gabCode process and starts with the native Open Workspace recovery/empty surface; it starts no project terminals. **Must-have.**
4. **Existing-window isolation** — Creating or failing to create another window does not replace the existing window’s project, terminal sessions, title, or local selection. **Must-have.**
5. **Accessible startup context** — Empty additional windows expose a meaningful native title/accessible prompt and keyboard-accessible Open Workspace action. **Should-have.**

## Non-Goals

- Reusing an existing window for a selected workspace.
- Cross-process IPC, a companion service, shared runtime protocol, or persisted instance-count state.
- Changing descriptor parsing, Git worktree selection, terminal retention, or workspace-file associations beyond correctly routing launch context.
- Implementing macOS behavior in the Windows increment or treating Windows evidence as macOS evidence.

## Technical Considerations

- Windows owns the implementation using its existing WPF application lifecycle and command-line handling.
- Windows uses a `Local\\` named mutex scoped to gabCode and the current user SID. The first process owns it for its lifetime; a later process observes it and suppresses restoration. It requires no administrator permission or UAC elevation.
- macOS uses an exclusive non-blocking `flock` held on a per-user Application Support gabCode lock file for the process lifetime. Kernel release on process exit/crash is authoritative; the file is not persisted instance-count state.
- A valid explicit workspace argument has precedence over restoration. A plain additional launch, identified by the platform presence mechanism, suppresses last-workspace restoration only for that new window. An invalid explicit argument keeps the new window at the classified recovery surface with its specific reason and path; it does not restore a different workspace.
- Preserve existing local last-workspace preference and `WorkspaceProjectLoader` validation; do not modify descriptors to encode window state.
- Test process/window routing separately from target-Windows native lifecycle evidence. Exercise quoted paths, invalid explicit arguments, occupied-window failure, keyboard prompt, and terminal non-creation.

## Milestones

1. **Platform planning and routing tests** — Define executable first/explicit/additional launch cases and introduce failing independent Windows/macOS tests.
2. **Windows lifecycle implementation** — Add the user-session mutex and route startup using explicit launch context plus presence detection.
3. **macOS lifecycle implementation** — Add the per-user `flock` and independently route Swift startup with the same behavior.
4. **Native verification and delivery** — Validate each platform on its target OS, then run review gates and prepare combined human acceptance evidence.

## Open Questions

- The macOS parity increment is intentionally separate and must be planned and verified on macOS.
