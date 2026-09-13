# gabCode — Windows Terminal Font Selection and Menu Theme PRD

| Field | Value |
| --- | --- |
| Status | Approved product direction — implementation planning pending |
| Platform | Windows only |
| Date | 2026-08-02 |
| Reference | `Documentation/design/2026-08-02-macos-terminal-font-selection-prd.md` |
| Parent direction | `Documentation/design/2026-07-24-gabcode-initial-prd.md`, independent native clients |

## Product Name & One-Liner

**Windows Terminal Font Selection and Menu Theme** restores the macOS-equivalent terminal font controls on Windows and gives every application menu one consistent, readable theme.

## Problem & Audience

A Windows gabCode developer needs to select the installed terminal font and point size used by all gabCode terminals, with the same controls and behavior already defined for macOS. The current Windows terminal hard-codes `Cascadia Mono` at 12 points. The View menu and its descendants can render light text on a light background, unlike the readable File menu.

## User Outcome

The developer opens Windows Settings, chooses an installed fixed-pitch terminal font and point size, and immediately sees all retained terminals use it without shell interruption. The choice survives relaunch and can be reset to the platform default. File, View, and every other application menu—including contextual application menus—remain readable and visually consistent.

## Source Delta Audit

| Behavior | macOS reference | Windows current state | Status | Required fix |
| --- | --- | --- | --- | --- |
| Global terminal font face and size | `Documentation/design/2026-08-02-macos-terminal-font-selection-prd.md`, Core Features 1–2 | `src/GabCode.Windows/Terminal/Hosting/TerminalHostedSession.cs` calls `Control.SetTheme(..., "Cascadia Mono", 12)` | gap | Add a Windows-owned global preference and native settings workflow matching the macOS controls and supported 8–72 point range. |
| Installed fixed-pitch font selection and preview | macOS PRD, Native Experience and acceptance criteria | No Windows font settings surface exists | gap | Enumerate installed fixed-pitch fonts through supported Windows/.NET/WPF APIs; provide keyboard-accessible selection and preview including ordinary, Unicode, Powerline, and representative Nerd Font glyphs. |
| Immediate update of retained terminals | macOS PRD, Live retained-terminal updates | Terminal sessions are retained by `WorktreeTerminalRegistry`; no font propagation API exists | gap | Propagate effective settings to every active terminal control without replacing sessions, processes, PTYs, views, or scrollback. |
| Persistence and reset | macOS PRD, Durable setting and safe fallback | Existing per-user preferences are platform-owned, e.g. `src/GabCode.Windows/Projects/SidebarSidePreference.cs`; no font preference | gap | Persist stable Windows font identity plus size under the existing local app-data convention; add reset-to-default and safe missing/invalid-font fallback. |
| Menu background and foreground consistency | File menu styling in `src/GabCode.Windows/MainWindow.xaml` | Root `Menu` resources define dark colors, but View child items do not explicitly inherit the same brushes; regression test documents the File styling | gap | Centralize and apply the File menu resources/style to all application menus and menu items, including View and context menus, without light-on-light states. |
| Terminal-only scope | macOS PRD, Non-Goals | No font preference currently | requirement | Do not change gabCode interface typography; settings affect terminal text only. |

## Core Features

### 1. Windows-equivalent terminal font settings — Must-have

- Provide the same user-facing controls as macOS: installed fixed-pitch font face, point size, preview, effective value, and reset to default.
- Use the Windows-native Settings/preferences experience and provide a keyboard-reachable path.
- Support point sizes from 8 through 72 and reject invalid values before persistence.
- Apply only to terminal text, not general application UI text.

### 2. Live propagation to retained terminals — Must-have

- Apply face and size changes immediately to all active terminals across all worktrees.
- Apply the effective choice before the first visible render of future terminals.
- Preserve shell/process identity, ConPTY, terminal view identity, focus usability, output, and bounded scrollback. Normal font-metric reflow and PTY resize are allowed.

### 3. Per-user persistence and fallback — Must-have

- Persist settings locally for the Windows user, independently of workspace, worktree, and terminal.
- Store a stable installed-font identifier rather than a framework font object.
- If a saved font is missing, invalid, or unsuitable for a fixed-pitch terminal grid, use the Windows terminal default and expose the effective fallback in Settings without a launch failure.
- Reset restores the Windows default face and default size and updates terminals immediately.

### 4. Font preview and glyph evidence — Must-have

- Preview plain text, Unicode, ANSI-style samples, Powerline separators, and representative Nerd Font private-use glyphs.
- Do not claim Nerd Font support from the font name alone; actual glyph rendering is target-machine evidence.
- Do not install, bundle, recommend, or modify fonts or shell configuration.

### 5. Consistent application menu theme — Must-have

- Use one shared readable menu resource/style for File, View, and all current and future application menus.
- Include submenu items, separators, keyboard focus/highlight states, and application-owned context menus.
- Preserve access keys, command behavior, automation names, and native keyboard navigation.
- Verify normal, hovered, focused, disabled, and high-contrast-relevant states do not produce unreadable contrast.

## Non-Goals

- macOS implementation changes or a shared cross-platform runtime/settings protocol.
- Per-project, per-worktree, or per-terminal font overrides.
- Application UI font customization.
- Font installation, downloading, bundling, fallback-font composition, or custom glyph rendering.
- Terminal colors, cursor, opacity, line spacing, profiles, or shell configuration.
- Interpreting terminal output or detecting Oh My Posh automatically.
- A broad visual redesign beyond menu consistency and the required settings workflow.

## Technical Considerations

- Target the complete native C#/WPF client under `src/GabCode.Windows`; assign production work to the Windows `frontend-builder` with `gabcode-windows-desktop`, `gabcode-dotnet-inspect`, `dotnet-concurrency-specialist`, `gabcode-native-accessibility`, and `gabcode-native-testing` support as needed.
- Inspect the pinned Windows Terminal WPF API and dependency documentation before prescribing the font setter or metric/resize behavior: `Documentation/dependencies/windows-terminal-wpf.md`, `src/GabCode.Windows/Terminal/Hosting/TerminalHostedSession.cs`, and `src/GabCode.Windows/Terminal/Views/TerminalSessionView.xaml.cs`.
- Keep preference ownership in a Windows app-local preference class, following existing patterns such as `src/GabCode.Windows/Projects/SidebarSidePreference.cs`; do not add repository metadata or an internal client/core protocol.
- Likely implementation areas are `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, terminal hosting/session classes, and new Windows preference/settings classes. Exact files must be confirmed during PM source inspection.
- Existing menu coverage begins in `src/GabCode.Windows/MainWindow.xaml` and `tests/GabCode.Windows.Tests/Projects/WorkspaceMenuTests.cs`. Consolidate resources rather than adding a View-only patch.
- Automated tests should cover preference round trips, validation, reset, fallback, live propagation/session retention, and menu resource/style declarations. Target-Windows evidence must cover actual font rendering, menus, keyboard navigation, UI Automation/Narrator, scaling, and high contrast. Unavailable checks are `NOT CHECKED`.

## Acceptance Criteria

### Font settings

- [ ] Windows exposes the same terminal font face, point-size, preview, effective-value, and reset controls as macOS.
- [ ] Installed fixed-pitch fonts, including user-installed Nerd Font variants, can be selected without source or shell edits.
- [ ] Point sizes 8–72 are supported; invalid values cannot be persisted.
- [ ] The setting affects terminal text only and is global per Windows user.
- [ ] Face and size changes apply immediately to every active terminal and future terminals.
- [ ] Existing process IDs, ConPTY/session identity, terminal views, output, and scrollback remain intact.
- [ ] Settings survive full termination and relaunch.
- [ ] Missing/invalid fonts fall back safely and reset restores the Windows default immediately.

### Menus

- [ ] File, View, submenu items, separators, and all application-owned context menus use the shared readable theme.
- [ ] Normal, hover, keyboard-focus, disabled, and submenu states have readable foreground/background contrast.
- [ ] Existing access keys, commands, automation names, and keyboard navigation continue to work.
- [ ] No View-menu item renders light text on a light background.

### Evidence

- [ ] Windows automated tests pass for preference, propagation, retention, and menu-style behavior.
- [ ] Target-Windows manual evidence demonstrates representative glyphs, live update, persistence/reset, menu states, keyboard-only use, UI Automation/Narrator, scaling, and high contrast; gaps are reported as `NOT CHECKED`.

## Milestones

1. **Parity inventory and API evidence** — Confirm the macOS control/default/reset contract and inspect the pinned Windows Terminal font and resize APIs.
2. **Preference and settings workflow** — Implement Windows per-user persistence, installed-font selection, preview, validation, fallback, reset, and keyboard-accessible settings UI.
3. **Live terminal propagation** — Apply settings to retained and newly created terminal controls while preserving session/process identity and existing terminal guarantees.
4. **Menu theme consolidation** — Centralize application menu resources and apply them to every menu surface, including context menus.
5. **Windows verification** — Run build/tests and record target-machine rendering, lifecycle, accessibility, menu, scaling, and high-contrast evidence.

## Open Questions

None for product behavior. PM execution must confirm the exact Windows Terminal WPF font API, Windows font enumeration/identity API, existing settings entry point (if any), and the Windows default face/size semantics before implementation.

## Decision Log

- 2026-08-02 — Windows is a separate native-client sprint; macOS is the behavioral reference, not shared production code.
- 2026-08-02 — Font settings are global per-user preferences and apply immediately to retained terminals.
- 2026-08-02 — Settings affect terminal text only.
- 2026-08-02 — Reset-to-default is required.
- 2026-08-02 — Menu styling is an application-wide consistency fix, not a View-menu-only patch.
