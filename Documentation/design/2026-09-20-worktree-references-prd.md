# gabCode — Worktree References PRD

| Field | Value |
|---|---|
| Date | 2026-09-20 |
| Status | Product design; implementation requires `/pm-agent` planning |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Follow-ups | `2026-09-20-markdown-review-editing-prd.md`, `2026-09-20-github-issue-viewer-prd.md` |

## Product Name & One-Liner

**Worktree References** lets a user attach one local Markdown file and one GitHub issue URL to each worktree, then access them from gabCode or their normal external tools.

## Problem & Audience

A developer running several worktrees needs to remember which document and sprint issue belong to each piece of work. The association should be explicit and local, not inferred from branch names or imposed by a particular workflow.

## Core Features

1. **Local associations — must-have.** Store at most one Markdown path and one GitHub issue URL per worktree in gabCode’s existing local user metadata store. Do not create repository files or modify the repository, Markdown file, or GitHub.
2. **Native assignment — must-have.** The worktree right-click menu provides Assign, Replace, Open, and Remove actions for the Markdown and issue references. Every action has a keyboard-accessible equivalent.
3. **References bar — must-have.** The selected worktree shows its current Markdown filename and issue identity in a compact bar above the main area; empty and unavailable references are explicit.
4. **Markdown selection — must-have.** A native file picker accepts any local Markdown file, not only files in the repository or `Documentation/design`.
5. **Issue selection — must-have.** The user enters or pastes an issue URL; gabCode validates its shape without requiring the issue to belong to the selected repository.
6. **External opening — must-have.** Open Markdown in the configured/default editor (with VS Code as the gabCode action where available) and open the issue in the default browser.
7. **Recovery — must-have.** Missing files, malformed URLs, unavailable `gh` authentication, inaccessible issues, and removed worktrees remain safely represented with Locate, Replace, Retry, Remove, or Open externally actions as applicable.

## Non-Goals

- No automatic inference, branch-name matching, syncing, or team sharing.
- No repository metadata or GitHub writes.
- No background issue polling or automatic opening.
- No in-app Markdown editing or issue rendering; those are follow-up PRDs.
- No requirement that the reference belongs to the worktree repository.

## Technical Considerations

Use the existing native per-user preferences/local metadata approach, keyed by stable worktree identity and cleaned up when Git no longer reports the worktree. Preserve Git, filesystem, and read-only `gh` authority. Validate paths without taking ownership of the files. Validate issue URLs and use the existing read-only GitHub/`gh` integration only when opening or later rendering an issue. Windows and macOS must implement native file pickers, menus, accessibility, and external-launch behavior separately.

## Native UX and Evidence

The context menu, references bar, and empty/error states must be keyboard complete, have native accessible names and roles, preserve focus when worktrees change, and remain usable at scaling/high contrast settings. Target evidence must cover assignment, replacement, removal, stale references, external opening, keyboard traversal, screen-reader exposure, and both platforms.

## Milestones

1. Define and test local association persistence and worktree cleanup.
2. Add native assignment/replacement/removal flows and context-menu commands on Windows.
3. Add the equivalent macOS native flow.
4. Add references-bar display, external open actions, and stale/error recovery on both platforms.
5. Validate parity and target-platform accessibility evidence.

## Identity and lifecycle decisions

Associations are keyed to the currently recognized worktree identity and are not migrated when the worktree is renamed or recreated. After a rename or replacement, the user must select the Markdown file and issue again. Removed worktrees do not retain usable associations.

## Open Questions

- Exact local metadata schema and platform storage location must be established from existing source during planning.
- The configured editor preference versus a VS Code-specific action needs confirmation during platform planning.
