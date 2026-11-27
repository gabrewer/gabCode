# gabCode Dockable Panels PRD

| Field | Value |
| --- | --- |
| Status | Product definition; implementation not yet approved |
| Date | 2026-11-27 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related review sidebar | `Documentation/design/2026-09-20-worktree-change-review-sidebar-prd.md` |

## Product Name & One-Liner

**gabCode Dockable Panels** — let developers arrange the worktree navigation and change review panels around their workspace or float them on another monitor.

## Problem & Audience

A developer working across Git worktrees and terminal sessions wants to arrange navigation and review around the task and available screens. Fixed sidebars compete for space and cannot follow the developer's preferred layout. Visual Studio-style docking makes the panels movable without requiring a full IDE window manager.

## Core Features

Sections 1–6 describe the target experience. The platform acceptance rule in section 7 distinguishes core outcomes from details that may differ or be deferred on one platform.

### 1. Independently movable panels — Must-have

The worktree navigation and change review panels can each be moved independently by dragging their title bar. Each can dock on the left, right, or bottom of its owning workspace window, or float in a native window, including on another monitor. The terminal remains in the main workspace; docking a panel must not stop, restart, or replace terminal processes or lose their output.

### 2. Clear docking and cancellation — Must-have

Dragging toward a supported edge shows a visible preview of the destination before the user drops. Dropping outside a valid dock target floats the panel; cancelling a drag leaves it in its previous position. A floating panel can be dragged back to a supported dock target. Closing its floating window redocks it at its last docked position. Moving a panel preserves its current navigation, selected worktree, review context, and applicable loading or error state; Git and the filesystem remain authoritative.

### 3. Usable layouts — Must-have

Docked panels can be resized, and floating windows can be moved and resized with native controls. The workspace and panels remain usable at supported window sizes and display scaling. When both panels occupy the same edge, stack them vertically on the left or right, and place them side by side along the bottom; both remain accessible without tab groups. Existing default placement remains navigation on the left and review on the right.

### 4. Pin and auto-hide panels — Must-have

Each docked panel has a discoverable pin control. Pinned panels remain open; unpinning a panel collapses it to a labeled tab on its docked edge. Selecting or focusing the tab temporarily reveals the panel; it hides again when focus moves outside the panel and its tab. Pinning the revealed panel keeps it open. Both panels can be auto-hidden independently, including when docked on the same edge. Collapsing and revealing preserves panel state and does not disturb terminal sessions. Floating panels can be redocked before using auto-hide.

The pin control and edge tab are accessible by keyboard and native accessibility APIs, including their current state. Focus can move into a revealed panel without it closing prematurely, and keyboard users can dismiss it and return focus to its tab. Pointer hover is not the only way to reveal a panel.

### 5. Remember and recover — Must-have

Remember each panel's location, size, floating state, and pinned or auto-hidden state per workspace as local per-user presentation preferences, not in the shared workspace descriptor. Windows displaying the same workspace can have independent layouts while open; the last one closed saves the layout restored next time. Restore a usable layout on reopening. If a saved monitor is unavailable, a saved window is off-screen, or preferences are invalid, recover the affected panel to a visible usable location. A discoverable **Reset Layout** restores both panels to their default positions and sizes without changing workspace or terminal state.

### 6. Keyboard and accessibility — Must-have

Every docking destination, floating action, resizing operation, and reset is reachable without dragging or using a mouse, through discoverable native controls or menus. Expose panel names, current placement, and available actions to native accessibility APIs; make focus visible and predictable when panels move between windows. Preview and state must not depend on color alone. Respect platform text scaling, contrast, and reduced-motion settings.

### 7. Platform acceptance — useful docking, not feature parity

On each platform, a developer can independently reposition, dock and float either panel, keep it open or collapse and reveal it, and restore or recover a usable layout. Moving panels must preserve terminal sessions and review context. Supported workflows must work with the keyboard and native screen reader. Verify these outcomes on each target OS.

The libraries need not support identical features. For example, one platform may use explicit activation rather than focus to reveal a collapsed panel, or arrange panels sharing an edge differently. If a library covers most of the target experience and meets the core outcomes, prefer it to custom work solely for parity. Record any missing detail, the user-visible alternative or limitation, and target-machine evidence; do not report an omitted or untested feature as passing.

### Release decision — a useful subset can ship

The feature list describes finished docking, not the minimum for every release. Judge the build by what users can actually do. For example, if docking and floating work well but temporary auto-hide does not, offer the working controls, omit the unfinished one, and list auto-hide as deferred. The other client may retain its current layout. Neither an unchecked feature nor an open sibling sprint is itself a reason to hold a release.

For each release candidate, report what is enabled on each platform, what was tested, and what is deferred or NOT CHECKED. Identify a blocker by its concrete impact on the shipped build (for example, moving a panel kills a terminal or leaves the panel unreachable), not by counting unchecked boxes. Fix or exclude the affected path and verify what remains. The release decision belongs to the human; shipping a subset does not mark the whole PRD complete or waive packaging checks.

## Non-Goals

- A general-purpose operating-system window manager or a complete Visual Studio docking system.
- Tabbed panel groups, nested splits, or arbitrary panel creation in this first version. Auto-hide edge tabs are included; they are not tabbed panel groups.
- Moving or docking terminals as part of this feature, or changing their lifecycle.
- Synchronizing layouts between devices or making local layout preferences a source of Git or workspace truth.
- Editing source, Git state, diffs, or PRDs through the panels.

## Technical Considerations

- Implement independently in the existing Windows C#/WPF client and macOS Swift/SwiftUI/AppKit client. Share requirements and conformance outcomes, not a cross-platform docking runtime or service.
- Prefer a suitable native docking library on each platform if it meets the required behavior, licensing, packaging, accessibility, and maintenance needs; otherwise build the necessary native docking pieces. Evaluate Dirkster's AvalonDock as the Windows candidate. On macOS, prototype [Polyjuice DockKit](https://github.com/Polyjuice/swift-dockkit) before deciding whether to adopt it or build native docking pieces. Verify the exact revision, license, package integration, maintenance, and floating-window behavior; do not assume the current documentation matches a tagged release.
- The macOS prototype must exercise both panels, edge docking and same-edge placement, floating and redocking, auto-hide feasibility, saved layouts, multi-monitor recovery, keyboard and VoiceOver, and preservation of running terminal sessions and panel state. Record what works, what needs adaptation, and any blockers on a Mac before selecting the production approach.
- Preserve panel identity and live state across rehosting, including asynchronous refresh and focus. Test that changing layout does not affect terminal processes, selection, or review state.
- Test disconnected displays, display scaling changes, corrupt preferences, narrow windows, cancellation, reset, and closing or reopening a floating panel.
- Validate drag/drop, native window lifecycle, keyboard operation, and screen readers on each target operating system; evidence from one platform does not establish the other.

## Milestones

1. **Shared behavior and conformance:** define common scenarios and expected outcomes for both panels, including same-edge placement and floating-window close behavior.
2. **Windows increment:** implement and validate native docking, floating, persistence, recovery, and accessibility on Windows.
3. **macOS increment:** independently implement and validate the same outcomes on macOS.
4. **Platform acceptance review:** verify core outcomes independently, including multi-monitor recovery and uninterrupted terminals. Record supported behavior, alternatives and limitations; identical feature coverage is not a release gate.

The two platform increments may proceed independently once shared prerequisites are available; their ordering above does not imply one platform proves or blocks the other.

## Open Questions

None. The macOS DockKit prototype and platform-specific dependency validation are implementation research governed by the criteria above, not unresolved product decisions.
