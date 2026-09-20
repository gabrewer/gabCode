# gabCode — Markdown Review and Editing PRD

| Field | Value |
|---|---|
| Date | 2026-09-20 |
| Status | Product design; implementation requires `/pm-agent` planning |
| Foundation | `Documentation/design/2026-09-20-worktree-references-prd.md` — required |
| Dependency | The associated-reference flow must exist before this feature can open a document tab |

## Product Name & One-Liner

**Markdown Review and Editing** makes the selected worktree’s associated Markdown file a reviewable main-area tab with preview-first viewing and safe basic editing.

## Problem & Audience

A developer reviewing a sprint document should be able to fix a typo, update wording, or check a small item without leaving gabCode for an external editor or LLM.

## Core Features

1. **Main-area document tab — must-have.** The user explicitly opens the associated Markdown from the references bar or context menu; it does not open automatically. The retained terminal remains available below.
2. **Preview-first rendering — must-have.** Render the Markdown as a read-only preview by default, with headings, lists, links, code, tables, and task-list state represented accessibly.
3. **Edit toggle — must-have.** Switch to a plain Markdown text editor without replacing or reformatting source content.
4. **Explicit save/cancel — must-have.** Support Save, Cancel/Revert, native undo/redo, cut/copy/paste/select-all, find, and Ctrl+S/Cmd+S.
5. **Safe conflict handling — must-have.** Detect external changes while editing and never overwrite them silently; offer reload, compare/review, or save-as/retry recovery.
6. **Unsaved-state protection — must-have.** Confirm before closing the tab, changing worktrees, or quitting when edits are dirty.

## Non-Goals

- No WYSIWYG editor, rich-text controls, completion, multi-file editing, or automatic formatting.
- No automatic saves or edits from GitHub/LLM activity.
- No editing of arbitrary repository files through this feature; only the explicitly associated Markdown file.
- No claim that preview rendering executes embedded HTML, scripts, or unsafe content.

## Technical Considerations

Use native Windows and macOS text editing controls and a safe Markdown renderer appropriate to each platform. Preserve UTF-8/source text and line endings where supported. Saving remains a direct local filesystem mutation initiated by the user, with atomic write/error recovery defined during planning. File watching should invalidate preview and detect edit conflicts without stealing focus. No shared runtime or protocol is introduced.

## Native UX and Evidence

Tabs, Preview/Edit, Save, Cancel, Find, dirty state, conflicts, and errors require native names, roles, keyboard paths, focus restoration, scaling, contrast, and screen-reader announcements. Target evidence must cover preview fidelity, edits, undo/redo, shortcuts, external changes, failed saves, dirty close/quit, and terminal retention on Windows and macOS.

## Milestones

1. Establish platform Markdown rendering and safe local file loading.
2. Add preview-first main-area tab and retained-terminal behavior.
3. Add basic editor commands, explicit save/cancel, and dirty-state protection.
4. Add external-change conflict recovery and error states.
5. Validate native accessibility and cross-platform parity.

## Open Questions

- The exact renderer/library and supported Markdown extensions must be inspected during platform planning.
- Conflict UI wording and whether a three-way comparison is feasible remain implementation questions.
