---
name: gabcode-windows-desktop
description: Provides gabCode Windows implementation guidance for C#, WPF, Windows Terminal control, ConPTY, native focus/input/accessibility, process trees, and packaging. Use for Windows client planning, building, testing, destroyer, or review work.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode Windows Desktop

Use this supporting skill only for a task whose target platform is Windows. Validate runtime claims on a Windows machine.

## Architecture boundary

- The complete WPF client owns windows, menus, navigation, layout, focus, keyboard behavior, accessibility, terminal views, direct Git/read-only `gh`, normalization, watchers/reconciliation, and local metadata.
- Use shared requirements, vocabulary, and language-neutral fixtures as conformance inputs only; do not create or depend on a shared runtime.
- Keep the Windows Terminal control and ConPTY behind gabCode-owned abstractions so dependency details do not leak through the client.
- A terminal session/process has identity independent of the layout region displaying its view.

## Terminal dependency gate

Before depending on the Windows Terminal control, record evidence for the exact pinned upstream revision, license, reproducible acquisition/build, WPF hosting approach, redistribution, packaging, process lifecycle, accessibility, and maintenance path. Do not invent APIs; use `gabcode-dotnet-inspect`, upstream source, and the pinned revision.

## Native implementation concerns

As applicable, define and test:

- WPF dispatcher/UI-thread ownership and cancellation-aware background work;
- per-worktree shell startup directory and selected shell/profile behavior;
- lazy creation of two independent shells and retention across navigation;
- moving a terminal view without restarting its process;
- ConPTY resize/reflow and input/output lifecycle;
- Unicode, ANSI/VT, IME, selection, clipboard, hyperlinks, and search;
- focus transfer among navigation, repository views, dialogs, and terminal content;
- bounded scrollback and retained-session resources;
- graceful shutdown followed by bounded descendant-process cleanup;
- cancellation that leaves processes running when exit is declined;
- packaging/signing and dependency redistribution.

## Boundaries

- Do not interpret terminal output or Pi commands.
- Do not track, start, stop, or resume Pi sessions.
- Do not share a terminal implementation with macOS.
- Do not claim Narrator, IME, focus, resize, cleanup, or packaging behavior without target-machine evidence.

Return exact APIs/dependency revision used, files changed, Windows evidence, and `NOT CHECKED` items to the owning worker.
