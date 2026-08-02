# gabCode — macOS Terminal Font Selection PRD

| Field | Value |
| --- | --- |
| Status | Approved product direction — implementation planning pending |
| Platform | macOS |
| Date | 2026-08-01 |
| Parent direction | `Documentation/design/gabcode-initial-prd.md`, native terminal foundation |

## Product Name & One-Liner

**macOS Terminal Font Selection** lets a developer choose one installed terminal font and point size for every gabCode terminal so prompts such as Oh My Posh render their Nerd Font and Powerline glyphs correctly.

## Problem & Audience

A developer can configure Oh My Posh correctly in their login shell and still see missing-glyph boxes or malformed prompt separators inside gabCode. The shell emits the expected Unicode characters, but gabCode currently lets SwiftTerm use its system monospaced default, which may not contain the private-use glyphs expected by the prompt.

This increment is for a macOS gabCode user who already installed and configured their preferred terminal font. gabCode needs to let them select that installed font without becoming a font installer, terminal-profile manager, or shell-configuration tool.

## User Outcome

The developer opens gabCode Settings, selects an installed Nerd Font such as a Meslo Nerd Font variant, chooses a readable point size, and immediately sees both retained terminals use it. Existing shells continue running, visible scrollback remains available, and future terminal sessions use the same global choice. The setting survives relaunch.

## Core Features

### 1. Global terminal font and size — Must-have

- One macOS setting contains the selected installed font face and point size.
- The setting applies to both generic terminals and, when worktree navigation exists, to every worktree.
- There are no per-project, per-worktree, or per-terminal overrides in this increment.
- The initial value is SwiftTerm's system monospaced default; gabCode does not import Apple Terminal or iTerm settings.

### 2. Native Settings workflow — Must-have

- The standard macOS Settings command (`Command-,`) opens a native Settings window.
- A **Terminal** settings section exposes the effective font face, point size, and a keyboard-accessible way to choose another installed font.
- The chooser must surface installed fixed-pitch faces, including user-installed Nerd Font variants, without relying on the words “Nerd Font” or “Powerline” being present in the name.
- Point size is directly editable using a native control with a supported range of 8–72 points.
- A preview shows ordinary text plus representative Powerline/Nerd Font glyphs so the user can identify missing glyphs before returning to a terminal.
- Font and size changes take effect immediately; there is no separate Apply action.

### 3. Live retained-terminal updates — Must-have

- Every active terminal view receives the new font.
- A font change does not restart or replace a shell, process group, PTY, terminal view, or retained terminal identity.
- Existing scrollback and terminal output remain available.
- Font-metric changes may reflow visible text and cause a normal PTY row/column resize.
- Focus remains usable and predictable after the update. A transient text selection may be cleared if required by the pinned SwiftTerm control, but terminal content must not be discarded.
- Future terminal views receive the effective font before their first visible render.

### 4. Durable setting and safe fallback — Must-have

- The global selection survives app termination and relaunch.
- The durable identity uses a stable installed-font identifier, such as the PostScript face name, plus point size; it does not serialize `NSFont` objects.
- If the saved face is missing, invalid, or no longer fixed-pitch, gabCode uses SwiftTerm's system monospaced default instead of crashing or creating an unusable terminal.
- Settings exposes the effective fallback choice. The invalid persisted selection must not repeatedly fail on later launches.

### 5. Nerd Font and accessibility evidence — Must-have

- Target-Mac evidence covers representative Powerline separators, private-use Nerd Font glyphs, ANSI styling, regular/bold variants, and an actual Oh My Posh prompt using an installed font selected by the operator.
- The Settings workflow is usable with keyboard navigation and has meaningful VoiceOver names, values, and focus order.
- Font sizes remain readable at supported macOS display scaling settings.
- Automated checks validate preference resolution and terminal-session retention; visual glyph correctness remains target-machine evidence and is not inferred from a saved font name.

## Native Experience

### First use

With no saved selection, gabCode uses SwiftTerm's existing system monospaced font and size. Nothing is imported from another terminal application, and no setup alert interrupts terminal launch.

The developer opens **gabCode → Settings…** and selects **Terminal**. The section presents:

- the currently effective font face;
- the current point size;
- a native font-selection control restricted to terminal-suitable fixed-pitch faces; and
- a preview containing plain ASCII, Unicode, and representative prompt glyphs.

Selecting a face or changing the size updates the preview and every running gabCode terminal immediately.

### Running terminals

A live update is a renderer change, not a terminal-session change. Commands continue running, terminal identities and process IDs remain stable, and swapping retained terminal regions continues to work. A metric change can alter rows and columns and therefore send the ordinary PTY resize notification expected by terminal applications.

### Missing-font recovery

If the selected face is removed between launches, gabCode starts with the system monospaced fallback. Settings identifies that effective fallback without a modal launch interruption. The user can choose any other installed fixed-pitch face.

## Non-Goals

This increment will not:

- Read, decode, import, or synchronize Apple Terminal preferences.
- Import settings from iTerm or another terminal emulator.
- Bundle, download, install, update, license, or recommend a specific third-party font.
- Claim that a font is a Nerd Font based only on its name.
- Patch missing glyphs, combine multiple user-selected fallback fonts, or implement a custom font renderer.
- Configure, install, inspect, or interpret Oh My Posh or shell startup files.
- Parse terminal output to detect prompt frameworks or missing glyphs.
- Add per-project, per-worktree, or per-terminal font overrides.
- Add terminal color themes, cursor themes, profiles, opacity, line spacing, or other appearance settings.
- Implement Windows font selection; Windows requires a separate native-client increment.
- Introduce a shared cross-platform terminal implementation.

## Technical Considerations

### Existing macOS terminal boundary

The current macOS client owns SwiftUI/AppKit terminal hosting through gabCode-owned session and workspace types. It pins SwiftTerm `1.15.0` at revision `dd2fb8ac5b861e7bf617c872895e338f38165648`.

The pinned macOS `TerminalView` publicly exposes `font: NSFont`. Assigning it rebuilds SwiftTerm's normal, bold, italic, and bold-italic font set and resets terminal font metrics. The implementation should apply the setting through the gabCode-owned terminal-session abstraction rather than allowing Settings views to reach into hosted SwiftTerm views directly.

Applying an `NSFont` requires no new terminal dependency, renderer, web surface, shared terminal abstraction, or client/sidecar message.

### Font identity and validation

- Enumerate installed fonts through supported AppKit/Core Text APIs.
- Resolve a selected face by stable PostScript name and point size.
- Verify that a choice is suitable for a fixed terminal cell grid; do not exclude a valid user-installed patched face through name-based filtering.
- Use `NSFont.monospacedSystemFont` at the effective system size as the fallback consistent with the pinned SwiftTerm default.
- Treat glyph coverage as preview/runtime evidence rather than as a promise inferred from font metadata.

### Retention and resize

Changing font metrics must trigger ordinary layout and terminal geometry updates while preserving the existing terminal process and scrollback model. Tests should capture process/session identity before and after the change. High-output, shutdown, and retained-view behavior from the terminal-foundation PRD must remain unchanged.

### Platform-owned preferences

Each native client owns and persists its platform-specific user settings. The macOS client stores the terminal font face and point size through supported macOS application preferences. Windows owns its settings independently and does not need to understand or resolve macOS font identifiers.

The shared NativeAOT sidecar does not provide a settings store or preference protocol. Font preferences remain local to the current platform and machine; gabCode does not synchronize them across platforms or devices.

## Acceptance Criteria

### Settings and persistence

- [ ] `Command-,` opens a native Settings window with a keyboard-reachable Terminal section.
- [ ] The current effective font face and point size have meaningful visible and VoiceOver values.
- [ ] An installed fixed-pitch Nerd Font face can be selected without editing source or shell configuration.
- [ ] The size can be changed within 8–72 points and invalid values cannot be persisted.
- [ ] The selected face and size survive a full app termination and relaunch.
- [ ] A removed or invalid saved font falls back to the system monospaced default and leaves Settings usable.

### Retained terminal behavior

- [ ] Both active terminals update immediately.
- [ ] Each terminal retains its process ID, process group, Unix session, PTY, terminal identity, and active child processes across the update.
- [ ] Existing visible output and bounded scrollback remain available after the update.
- [ ] Font metric changes produce correct layout/reflow and PTY sizing without clipping, a frozen renderer, or an unbounded resize loop.
- [ ] Terminal swapping, focus commands, close confirmation, cancellation, and bounded cleanup continue to satisfy the macOS terminal-foundation guarantees.

### Glyph and native evidence

- [ ] Controlled output demonstrates ordinary ASCII, Unicode, ANSI styles, Powerline separators, and representative Nerd Font private-use glyphs with the selected installed font.
- [ ] An actual Oh My Posh prompt renders without replacement boxes for glyphs provided by the selected font.
- [ ] Regular, bold, italic, and bold-italic terminal output remains legible or any dependency limitation is reported honestly.
- [ ] Keyboard-only selection, point-size editing, preview inspection, and return to terminal focus are validated on the target Mac.
- [ ] VoiceOver announces the Settings controls and effective values; checks not run are reported as `NOT CHECKED`, not PASS.
- [ ] No terminal output, prompt text, shell configuration, or environment value is persisted or logged as part of font validation.

## Milestones

### Milestone 1 — Establish native preference and retention evidence

Inspect the exact current macOS source and pinned SwiftTerm font API, then establish automated tests for native preference persistence, font resolution, fallback, and retained terminal identity.

### Milestone 2 — Deliver native Settings and live propagation

Add the global Terminal settings workflow, persistence through the approved owner, installed-font selection, preview, safe fallback, and immediate updates to active and future terminal views.

### Milestone 3 — Validate on the target Mac

Run the full macOS build/test surface and record manual evidence for Nerd Font/Powerline glyphs, an actual Oh My Posh prompt, renderer reflow, keyboard navigation, VoiceOver, scaling, focus, retention, and cleanup.

## Open Questions

None for the approved product scope. Implementation planning must still inspect the supported AppKit font-picker and persistence APIs before prescribing source changes.

## Decision Log

- 2026-08-01 — Use one global font setting rather than per-project, per-worktree, or per-terminal settings.
- 2026-08-01 — Skip Apple Terminal preference import because it depends on unsupported private preference structure.
- 2026-08-01 — Use only fonts already installed by the user; gabCode will not install or bundle fonts.
- 2026-08-01 — Apply font changes immediately to retained terminal views without restarting shells.
- 2026-08-01 — Keep Windows font selection out of this macOS increment.
- 2026-08-01 — Each native client owns its platform-specific settings; the shared sidecar will not provide a settings component or preference protocol.
