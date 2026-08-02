# gabCode — macOS Multiple Windows PRD

| Field | Value |
| --- | --- |
| Status | Draft |
| Platform | macOS |
| Parent direction | `Documentation/design/gabcode-initial-prd.md` |

## Product Name & One-Liner

**gabCode multi-window support** lets users open and use multiple independent gabCode windows in one macOS application process.

## Problem & Audience

A developer may need more than one gabCode workspace visible at the same time. The application should follow the familiar macOS behavior of Terminal and Ghostty: opening a new window creates another usable surface without replacing or disrupting existing windows.

This requirement applies before gabCode has a project model. A window does not yet need to represent a project, worktree, or other durable domain object.

## Core Features

1. **New Window — Must-have**
   `File → New Window` and `⌘N` create a new independent gabCode window.

2. **Independent Window Lifetime — Must-have**
   Closing one window does not close, reset, or otherwise disrupt any other open window.

3. **Multiple Simultaneous Windows — Must-have**
   Users can create and use multiple windows concurrently within the same running application.

4. **Native macOS Window Behavior — Must-have**
   Windows participate in standard macOS window management, including focus, minimize, restore, and window switching.

5. **Application Quit Semantics — Must-have**
   Quitting gabCode closes all open windows and terminates the application normally.

6. **Window-Local State — Should-have**
   State introduced by later features should be owned by its window unless explicitly defined as application-wide. This PRD does not define project persistence or shared state.

## Non-Goals

- Running multiple gabCode processes or installing multiple application versions side by side.
- Introducing projects, worktrees, repositories, or workspace identity.
- Defining terminal-session behavior beyond ensuring future window-local sessions are not accidentally shared.
- Window restoration, document-based state restoration, or cross-window synchronization.
- Cross-platform implementation requirements; Windows behavior is a separate increment.

## Technical Considerations

Use the native SwiftUI/AppKit application lifecycle and scene/window mechanisms rather than manually launching duplicate application processes. The implementation should use a separate window scene or equivalent native window allocation for each new window, with window-owned state rather than a singleton view model.

The design must avoid global mutable state for content that will later belong to a window, including terminal sessions. Application-wide services may remain shared only when their ownership is explicitly independent of a particular window.

Unknowns to resolve during implementation include the repository’s current SwiftUI lifecycle, minimum macOS deployment target, menu-command setup, and whether future terminal hosting requires AppKit window delegates for cleanup.

## Milestones

1. **Lifecycle baseline** — Confirm the existing macOS app can create and close a native window.
2. **New Window command** — Add the menu item and `⌘N` command.
3. **Isolation proof** — Add temporary or initial window-local state and demonstrate that closing or changing one window does not affect another.
4. **Native behavior verification** — Verify focus, minimize/restore, switching, and application quit with multiple windows on a target Mac.
5. **Foundation evidence** — Document the lifecycle decisions and boundaries for future terminal/project features.

## Open Questions

- Should gabCode restore previously open windows after relaunch?
- What window-local content should the first real window display before projects or worktrees exist?
- Which state, if any, should intentionally be shared across windows?
