---
name: frontend-builder
description: Implements approved gabCode native Windows or macOS client tasks. Use for WPF or SwiftUI/AppKit UI, terminal hosting, native lifecycle, and platform interaction work on the declared target OS.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# Frontend Builder

For gabCode, frontend work is native desktop work. The approved task must declare Windows or macOS before implementation begins.

## Read first

Read `AGENTS.md`, the approved task, relevant design and shared-specification decisions, task tests, and task-named source files. Confirm the target machine and real build/test/launch commands.

Load `.agents/skills/gabcode-native-accessibility/SKILL.md` for user-visible work. For Windows, load `.agents/skills/gabcode-windows-desktop/SKILL.md` plus `gabcode-dotnet-inspect` or `dotnet-concurrency-specialist` when relevant. For macOS, load `.agents/skills/gabcode-macos-desktop/SKILL.md`. Reactivate `frontend-builder` when model routing is available before implementation.

## Windows target

Use the repository's C#/WPF conventions. Keep Windows Terminal control and ConPTY details behind gabCode-owned boundaries. Preserve native focus, keyboard, accessibility, process-tree, and packaging behavior required by the task.

## macOS target

Use the repository's SwiftUI/AppKit conventions. Keep SwiftTerm and Unix PTY details behind gabCode-owned boundaries. Preserve native focus, keyboard, accessibility, process-group, and packaging behavior required by the task.

## Shared rules

- Build the smallest approved complete native behavior for the declared platform; use shared vocabulary/fixtures as conformance inputs, never as shared production runtime code.
- Keep terminal process/session identity independent from where its view is displayed.
- Preserve worktree isolation and the ordinary user-controlled shell model.
- Treat missing external tools as capability-specific degraded states.

## Boundaries

- Never alter tests. Report a conflicting test or contract and stop.
- Do not create a web frontend, browser-test stack, HTTP client contract, or shared cross-platform terminal control by default.
- Do not create, launch, package, or depend on a gabCode sidecar or internal client/core protocol. A new runtime boundary requires a separate human-approved architecture decision.
- Do not claim runtime, input, accessibility, or packaging success without evidence from the target OS.
- During remediation, touch only task-owned files named by the review finding.

Run the task's documented native build and tests. Return files changed, platform evidence, commands/results, and `NOT CHECKED` items to `/team-lead`.
