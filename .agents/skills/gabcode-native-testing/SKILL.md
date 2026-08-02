---
name: gabcode-native-testing
description: Designs and evaluates gabCode automated and target-machine evidence across native clients, child processes, PTYs, filesystem watchers, Git, gh, shared fixtures, accessibility, and cleanup. Use during test writing, building, destroyer, review, and sprint smoke testing.
metadata:
  provider: openai-codex
  model: gpt-5.6-terra
  thinking: high
---

# gabCode Native Testing

Use the repository's selected test frameworks. This skill defines evidence strategy, not a mandatory framework or package.

## Evidence layers

Choose the smallest layer that proves the risk:

1. **Pure tests** — parsing, normalization, invariants, compatibility, and state transitions.
2. **Component tests** — adapters and lifecycle components with controlled process/filesystem boundaries.
3. **Process integration** — exercise a target native client's direct installed-tool adapter or controlled child process, including arguments, cancellation, exit, and stderr/stdout behavior.
4. **Temporary Git repositories** — create real repositories/worktrees/remotes for Git semantics rather than mocking command text.
5. **Native host tests** — exercise WPF or SwiftUI/AppKit integration where supported.
6. **Target-machine manual evidence** — terminal rendering/input, Narrator/VoiceOver, focus, IME, resize/reflow, packaging, signing, and cleanup that automation cannot prove.

## Required test qualities

- deterministic synchronization instead of sleeps;
- isolated temporary directories and processes;
- bounded timeouts with useful failure diagnostics;
- cleanup that verifies descendants and handles failed assertions;
- paths containing spaces and Unicode;
- malformed, partial, empty, large, and unexpected external output;
- cancellation and shutdown at each meaningful lifecycle point;
- stale-result and concurrent-operation coverage where state can race;
- clear distinction between `PASS`, `FAIL`, and `NOT CHECKED`.

## Boundary-specific guidance

### Git and `gh`

Use structured Git output and real repositories. Cover detached HEAD, missing upstream, dirty/renamed/deleted files, stale worktree registrations, rewritten history, and guarded removal. For `gh`, distinguish missing executable, unauthenticated, remote mismatch, permission, not-found, and transient failures while preserving read-only behavior.

### Processes and shared fixtures

Test direct installed-tool invocation, malformed/unknown/versioned external output, cancellation, child-process death, diagnostics separation, and pending-operation cleanup. Exercise shared logical fixtures from each platform's own test surface; a fixture pass never replaces target-OS Git, filesystem, watcher, process, or cleanup evidence.

### Native terminals

Verify two independent shells, worktree cwd, retained processes, view movement without restart, bounded scrollback, resize, Unicode/ANSI/IME, clipboard/selection/search, focus/accessibility, cancellation, and descendant cleanup. Require evidence from the target OS.

## Boundaries

- Do not introduce browser tooling for native surfaces.
- Do not force snapshot testing for simple assertions or before a framework is selected.
- Do not call a test integration-level if it mocks the boundary under test.
- Do not convert missing platform evidence into a pass.

Return commands, automated results, manual evidence, cleanup evidence, and remaining `NOT CHECKED` items to the owning worker.
