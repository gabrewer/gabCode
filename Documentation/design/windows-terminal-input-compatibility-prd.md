# gabCode — Windows Terminal Input Compatibility PRD

| Field | Value |
| --- | --- |
| Status | Proposed |
| Platform | Windows |
| Parent direction | `Documentation/design/gabcode-initial-prd.md` |
| Related foundation | `Documentation/design/windows-native-terminal-foundation-prd.md` |
| Related paste safety | `Documentation/design/windows-terminal-safe-multiline-paste-prd.md` |

## Product Name & One-Liner

**Native Terminal Input Compatibility** — Make gabCode terminal sessions accept keyboard input exactly like Windows Terminal.

## Problem & Audience

A Windows developer using gabCode cannot use core interactive-terminal keys. Arrow keys, Backspace, and Shift+Tab do not work in either shell terminals or Pi sessions. This breaks shell history, autocomplete, command-line editing, and Pi thinking-mode switching.

The issue is broader than three shortcuts: gabCode must not swallow or incorrectly translate terminal input.

## Scope

This PRD covers the Windows client’s WPF terminal host, the pinned Microsoft Windows Terminal WPF control (`v1.24.11911.0`), and its ConPTY input path. It applies to both ordinary shell sessions and Pi sessions.

The supported shell resolution is the configured Windows Terminal default profile when available, followed by `pwsh`, Windows PowerShell, and `cmd.exe`.

## Core Requirements

1. **Native key forwarding — Must-have**
   Forward printable keys, arrows, Backspace/Delete, Tab/Shift+Tab, Enter, Escape, function keys, and Ctrl/Alt/Shift combinations without WPF chrome consuming or rewriting them.

2. **Shell interaction — Must-have**
   Verify history navigation, autocomplete, line editing, interrupts, and common control-key behavior in the supported shell profiles.

3. **Pi interaction — Must-have**
   Verify arrow navigation, editing keys, and Shift+Tab thinking-mode switching in Pi.

4. **Standard terminal semantics — Must-have**
   Preserve the terminal control’s normal VT behavior, including application cursor-key modes, mouse input, clipboard, and bracketed paste where supported.

5. **Focus correctness — Must-have**
   Ensure keyboard events go to the active terminal surface and are not captured by surrounding WPF controls.

6. **Regression evidence — Must-have**
   Add automated coverage for translation/transport logic and target-Windows evidence for interactive shell and Pi behavior.

## Non-Goals

- Changing Pi keybindings.
- Adding gabCode-specific terminal shortcuts or per-key special cases.
- Replacing the approved Windows Terminal WPF control or ConPTY unless a separately approved decision requires it.
- Implementing macOS terminal behavior.
- Parsing, recording, or interpreting shell or Pi output.
- Defining multiline-paste confirmation behavior, which is owned by `Documentation/design/windows-terminal-safe-multiline-paste-prd.md`.

## Technical Considerations

- Host: C# WPF under `src/GabCode.Windows/`.
- Terminal control: gabCode-pinned Microsoft Windows Terminal WPF control `v1.24.11911.0`.
- Process transport: ConPTY.
- Diagnose the complete route from WPF keyboard events through the terminal control and ConPTY adapter before changing code.
- Prefer preserving standard terminal input bytes/escape sequences over translating individual reported keys.
- Target-machine testing is required because WPF focus/event routing and native terminal behavior may not be fully covered by unit tests.
- Mouse, clipboard, bracketed paste, and VT mode behavior are part of compatibility verification, not new gabCode features.

## Milestones

1. **Reproduce and trace** — Reproduce the failures in both shells and Pi; identify where input is lost.
2. **Repair native input path** — Correct WPF focus/event routing or terminal/ConPTY translation at the actual loss point.
3. **Automated coverage** — Add deterministic tests for representative key sequences and modifier combinations.
4. **Windows verification** — Validate shell history/autocomplete/editing, Ctrl+C/Ctrl+L, and Pi Shift+Tab plus standard terminal behavior.
5. **Adversarial review** — Check focus changes, paste, mouse input, application cursor modes, and regressions in terminal lifecycle/cleanup.

## Acceptance Criteria

- [ ] Arrow keys work in both shell terminals and Pi.
- [ ] Backspace and Delete edit command and Pi input correctly.
- [ ] Shift+Tab switches Pi thinking modes.
- [ ] Tab and ordinary modifier combinations reach the terminal correctly.
- [ ] Ctrl+C and Ctrl+L retain normal shell behavior.
- [ ] Shell history and autocomplete work in the configured/default shell and documented fallbacks.
- [ ] The fix is not implemented as a list of only the currently reported keys.
- [ ] Target-Windows evidence covers both an ordinary shell and Pi.
- [ ] Existing terminal focus, resize, clipboard, mouse, VT, process cleanup, and session-retention behavior remain intact.
- [ ] No terminal output, commands, environment secrets, or Pi conversation data is persisted by the fix.

## Open Questions

None for product scope. The implementation question is where the input is currently lost: WPF event routing, the Windows Terminal WPF control boundary, or the ConPTY input adapter. The diagnosis milestone must answer that before implementation is finalized.
