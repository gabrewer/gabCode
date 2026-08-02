---
name: gabcode-macos-desktop
description: Provides gabCode macOS implementation guidance for SwiftUI, AppKit, SwiftTerm, Unix PTYs, login shells, native focus/input/accessibility, process groups, signing, and notarization. Use for macOS client planning, building, testing, destroyer, or review work.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode macOS Desktop

Use this supporting skill only for a task whose target platform is macOS. Build and validate runtime claims on a macOS machine.

## Architecture boundary

- The complete SwiftUI/AppKit client owns windows, menus, navigation, layout, focus, keyboard behavior, accessibility, terminal views, direct Git/read-only `gh`, normalization, watchers/reconciliation, and local metadata.
- Use shared requirements, vocabulary, and language-neutral fixtures as conformance inputs only; do not create or depend on a shared runtime.
- Keep SwiftTerm and Unix PTY details behind gabCode-owned abstractions.
- A terminal session/process has identity independent of the layout region displaying its view.

## Terminal dependency gate

Before depending on SwiftTerm `LocalProcessTerminalView`, record evidence for the exact pinned version/revision, license, package acquisition, native hosting behavior, release packaging, process lifecycle, accessibility, and maintenance path. Verify APIs against that version instead of relying on memory.

## Native implementation concerns

As applicable, define and test:

- SwiftUI/AppKit ownership and main-actor boundaries;
- login-shell resolution and per-worktree startup directory;
- lazy creation of two independent shells and retention across navigation;
- moving/rehosting a terminal view without restarting its process;
- Unix PTY resize/reflow and input/output lifecycle;
- Unicode, ANSI/VT, IME/input methods, selection, clipboard, hyperlinks, and search;
- focus transfer among navigation, repository views, sheets, and terminal content;
- bounded scrollback and retained-session resources;
- graceful shutdown followed by bounded process-group/descendant cleanup;
- cancellation that leaves processes running when exit is declined;
- sandbox/entitlement implications, signing, notarization, and redistribution.

## Boundaries

- Do not interpret terminal output or Pi commands.
- Do not track, start, stop, or resume Pi sessions.
- Do not share a terminal implementation with Windows.
- Do not claim VoiceOver, input-method, focus, resize, cleanup, signing, or notarization behavior without target-machine evidence.

Return exact dependency version used, files changed, macOS evidence, and `NOT CHECKED` items to the owning worker.
