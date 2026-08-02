# gabCode — Windows Terminal Safe Multiline Paste PRD

| Field | Value |
| --- | --- |
| Status | Proposed |
| Platform | Windows |
| Parent direction | `Documentation/design/gabcode-initial-prd.md` |
| Related foundation | `Documentation/design/windows-native-terminal-foundation-prd.md` |
| Related input compatibility | `Documentation/design/windows-terminal-input-compatibility-prd.md` |

## Product Name & One-Liner

**Safe Multiline Terminal Paste** — Warn before a Windows terminal paste containing line breaks can send or execute multiple commands.

## Problem & Audience

A Windows developer frequently pastes commands, scripts, and Pi-generated text into gabCode terminals. Pasting text containing a line break currently forwards that line break directly to the terminal, where the shell or active program may execute one or more commands immediately.

Windows Terminal protects this interaction with a multiline-paste warning. gabCode should provide the same safety expectation without modifying the clipboard text or weakening normal single-line paste.

## User Outcome

Single-line paste remains immediate. When a paste contains one or more line-break characters, gabCode shows a native confirmation with a short preview before sending any clipboard content to the terminal. The user can approve that individual paste or cancel it safely.

## Core Requirements

### 1. Detect multiline paste — Must-have

Treat clipboard text as multiline when it contains any carriage return or line feed (`\r` or `\n`), including a trailing line break that could submit a command. Detection occurs before any part of the text is written to the terminal connection.

### 2. Confirm every multiline paste — Must-have

Show a native confirmation for each multiline paste attempt. The dialog explains that pasting the text may run multiple commands and offers:

- **Paste** — approve this paste only.
- **Cancel** — send nothing.

Escape and closing the dialog are equivalent to **Cancel**. There is no “always allow,” “do not ask again,” preference, or session-level bypass.

### 3. Show a safe short preview — Must-have

The confirmation includes a read-only preview of the beginning of the clipboard text:

- preserve recognizable line boundaries;
- bound the number of displayed lines and characters;
- indicate when content has been truncated;
- prevent control characters or pasted markup from affecting dialog behavior;
- avoid requiring the dialog to render very large clipboard contents.

The preview is informational only. It must not become the source of the terminal input.

### 4. Preserve approved text exactly — Must-have

After approval, forward the original clipboard text unchanged through the terminal’s normal paste path. Do not normalize line endings, trim whitespace, remove a final newline, convert newlines to spaces, or rewrite bracketed-paste framing.

### 5. Cancel without side effects — Must-have

Cancellation sends no portion of the clipboard text to ConPTY, does not modify the clipboard, and returns focus predictably to the terminal that initiated the paste.

### 6. Cover every supported paste entry point — Must-have

The warning cannot be bypassed by another gabCode-supported paste gesture. Keyboard shortcuts, terminal context-menu actions, and accessibility-invoked paste commands must share the same safety decision before terminal input is written. Unsupported operating-system or third-party injection mechanisms are not part of this requirement.

### 7. Preserve terminal semantics — Must-have

Single-line paste remains immediate and unchanged. Approved multiline paste continues through the terminal control’s normal VT and bracketed-paste behavior. The feature must not inspect shell state, interpret commands, or depend on Pi-specific output.

## Native Interaction

Suggested dialog content:

> **Paste multiple lines into the terminal?**
> This text may run multiple commands.
>
> `[bounded read-only preview]`
>
> **Paste**  **Cancel**

The dialog is owned by the active gabCode window and associated with the terminal that initiated the paste. **Cancel** is the safe default. Keyboard users must be able to inspect and dismiss the dialog without the underlying terminal receiving dialog keystrokes.

If clipboard text cannot be read safely, gabCode must not paste partial or fallback content. It should cancel the operation and present a recoverable explanation.

## Non-Goals

- Warning for ordinary single-line text without `\r` or `\n`.
- Parsing or judging whether pasted commands are safe.
- Reformatting, sanitizing, or repairing clipboard text before paste.
- Remembering approval across paste attempts.
- Adding a preference to disable the warning.
- Persisting or logging clipboard contents or preview text.
- Implementing macOS paste behavior in this Windows increment.
- Replacing the pinned Windows Terminal WPF control or ConPTY.

## Technical Considerations

- Intercept paste at the Windows clipboard/terminal-command boundary before `ITerminalConnection.WriteInput` or an equivalent ConPTY write can occur.
- First inspect the pinned Microsoft Windows Terminal WPF control’s paste hooks and command routing. Avoid key-specific interception that misses context-menu or accessibility paste paths.
- Keep clipboard access and the confirmation UI in gabCode-owned Windows code; do not patch the pinned terminal binaries unless a separate dependency decision explicitly approves it.
- Hold the exact original text separately from the bounded preview and use only the original value after approval.
- Handle Windows clipboard contention and non-text clipboard formats as recoverable conditions.
- Ensure concurrent or repeated paste requests cannot reorder text, display a warning for one clipboard value and send another, or target a terminal different from the initiating terminal.
- Do not write clipboard content to diagnostics, telemetry, test snapshots containing user data, or persistent application state.

## Milestones

1. **Capability investigation** — Trace all supported paste routes through the WPF terminal control and identify the earliest shared interception point before terminal input.
2. **Safety behavior** — Add line-break detection, a bounded preview model, and one-time native confirmation with exact-text preservation.
3. **Automated evidence** — Test detection, preview truncation, approval/cancellation, exact forwarding, clipboard failures, and repeated/concurrent requests.
4. **Target-Windows validation** — Verify keyboard, context-menu, focus, clipboard, shell, Pi, bracketed-paste, accessibility, and large-content behavior on Windows.
5. **Adversarial review** — Attempt bypasses, partial writes, stale clipboard races, wrong-terminal delivery, control-character rendering, and content leakage.

## Acceptance Criteria

- [ ] Single-line clipboard text pastes immediately without a confirmation and without modification.
- [ ] Clipboard text containing `\r`, `\n`, `\r\n`, or a trailing line break triggers confirmation before any terminal write.
- [ ] Every multiline paste attempt requires a fresh confirmation.
- [ ] The dialog warns that multiple commands may run and shows a bounded, safely rendered preview.
- [ ] Truncated previews clearly indicate omitted content.
- [ ] Approving forwards the exact original clipboard text once to the initiating terminal.
- [ ] Cancelling, pressing Escape, or closing the dialog sends no terminal input and leaves the clipboard unchanged.
- [ ] Focus returns predictably to the initiating terminal after approval or cancellation.
- [ ] All gabCode-supported keyboard, context-menu, and accessibility paste paths use the same guard.
- [ ] Approved paste preserves the terminal control’s bracketed-paste behavior where active.
- [ ] Large, Unicode, empty-line, whitespace-only, and control-character-containing clipboard values are handled without UI corruption or unbounded rendering.
- [ ] Clipboard read failures and terminal closure during confirmation fail safely without partial input or application failure.
- [ ] No clipboard or preview content is persisted or written to diagnostics.
- [ ] Target-Windows evidence covers an ordinary shell and Pi.

## Open Questions

- Which public or hostable hook in the pinned Windows Terminal WPF control can intercept every supported paste route before input is written?
- What exact preview bounds best fit the native dialog after implementation measures the WPF layout? The implementation should choose conservative fixed limits and record them in tests.
