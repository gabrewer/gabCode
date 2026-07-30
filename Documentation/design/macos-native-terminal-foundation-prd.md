# gabCode — macOS Native Terminal Foundation PRD

| Field | Value |
| --- | --- |
| Status | Draft — planning input |
| Platform | macOS |
| Parent direction | `Documentation/design/gabcode-initial-prd.md`, Milestone 1 |
| Issue | To be linked by `/pm-agent` |

## Purpose

Establish a production-foundation native terminal host in the gabCode macOS client. The increment proves that gabCode can host and retain two ordinary local shell terminals for one worktree without becoming an editor, a terminal multiplexer, or a Pi-session manager.

This is the macOS half of the native terminal foundation. It is independent of the Windows implementation and does not introduce a shared terminal abstraction or shared-core protocol.

## User Outcome

A developer can open the native macOS gabCode prototype, choose or receive a worktree directory for the terminal host, and use two independent terminals:

- **Pi** — an ordinary login shell where the developer may run `pi` or `pi --resume`.
- **Commands** — an ordinary login shell where the developer runs builds, tests, Git, and other commands.

The developer can switch which terminal occupies the main region and can move the Pi terminal between the main region and a bottom-panel host without restarting its shell or losing its visible session. gabCode labels the terminal regions, but never interprets terminal output or controls Pi’s session lifecycle.

## Product Boundary

### In scope

- A SwiftUI-native terminal foundation under `src/GabCode.MacOS/`.
- AppKit terminal hosting where SwiftUI cannot directly host the required native terminal view.
- Two independently launched local terminal sessions rooted in one selected worktree directory.
- A minimal native layout that demonstrates a main terminal region and a bottom terminal region.
- Moving the existing Pi terminal view between those regions without restarting the process.
- Retaining terminal sessions while switching between Pi and Commands views in this prototype.
- Login-shell resolution from the user environment and a clear local fallback chain.
- SwiftTerm acquisition through Swift Package Manager, version/revision pinning, licensing, packaging, lifecycle, accessibility, and maintenance evidence.
- Unix PTY-backed local process creation, input/output, resize, and cleanup behavior.
- Bounded in-memory scrollback.
- Native keyboard, selection, clipboard, search, hyperlinks, ANSI/VT, Unicode, input-method, and VoiceOver-host behavior to the extent supported by the selected terminal control.
- Mandatory application-close confirmation when terminal processes are active.
- Graceful shutdown followed by bounded process-group/descendant cleanup after explicit confirmation.
- Automated and target-Mac evidence for lifecycle, process retention, failure states, and native accessibility.

### Explicitly out of scope

- Windows, WPF, Windows Terminal control, ConPTY, and cross-platform terminal implementation.
- The C# NativeAOT sidecar, JSON protocol, Git, `gh`, filesystem watchers, worktree discovery, project registration, status, associations, and preferences.
- Creating, removing, selecting, or navigating worktrees in gabCode. The foundation may use a controlled test worktree directory supplied by the host or test harness.
- Reading, recording, parsing, summarizing, or otherwise interpreting terminal output.
- Starting Pi automatically, sending Pi prompts, detecting Pi sessions, recording sessions, or invoking `pi --resume`.
- Terminal tabs beyond the required Pi and Commands surfaces, terminal splitting, remote terminals, SSH, terminal collaboration, or persistence after application exit.
- Source editing, Git mutation, VS Code integration, PR/issue mutations, signing, notarization, distribution, or installer work.
- A shared Windows/macOS terminal abstraction. Each platform owns its native terminal implementation.

## Required macOS Technology Direction

- **Host UI:** the existing SwiftUI app, with AppKit `NSView` hosting where required.
- **Terminal engine/control:** SwiftTerm’s macOS `TerminalView` / `LocalProcessTerminalView` through Swift Package Manager.
- **PTY/process transport:** local Unix PTYs and process groups owned by a gabCode macOS terminal-session abstraction.
- **Toolchain baseline:** the repository’s documented Apple Silicon, macOS, Xcode, macOS SDK, and Swift baselines.

Before implementation depends on SwiftTerm, the sprint must record the exact version or immutable revision, its license, package acquisition, supported API surface, AppKit/SwiftUI hosting approach, binary/package redistribution implications, release packaging considerations, maintenance/upgrade path, and known accessibility constraints. Do not assume API signatures from memory.

SwiftTerm and Unix PTY details must remain behind gabCode-owned session and hosted-terminal abstractions; they must not leak throughout SwiftUI views.

## Native Experience

### Initial state

The existing `gabCode` SwiftUI application window remains the application shell. This increment introduces a terminal-foundation surface with:

- A clearly named main terminal region.
- A clearly named bottom terminal region.
- Visible Pi and Commands selectors that identify which independent session is displayed in each region.
- A user-visible terminal lifecycle state: starting, ready, failed, exited, or closing.
- A non-terminal empty or failure surface that explains what failed and offers a safe retry when session creation has not succeeded.

This is a foundation UI, not the final worktree navigator. It may use a controlled worktree path appropriate for target-machine verification until project/worktree navigation exists.

### Pi and Commands behavior

- Each terminal is an independent child process, Unix PTY, and terminal session.
- Both receive the same selected working directory at creation.
- Both receive the selected login shell independently.
- Commands entered in one terminal do not appear in or affect the other terminal.
- The app does not reserve ordinary shell input for Pi-specific behavior.
- The labels **Pi** and **Commands** describe intended use only; either terminal can run any local shell command.

### Layout retention

- The Pi terminal process has identity independent of the SwiftUI/AppKit region currently displaying it.
- Moving Pi between the main region and bottom panel retains the same local process, PTY, and terminal view/session; it must not create a replacement shell.
- Moving or selecting a terminal preserves its scrollback, cursor state, and active child process when the selected terminal control supports those states.
- The Commands terminal remains alive while Pi is moved or selected.
- If retaining/rehosting the native terminal view is technically impossible without process restart, the sprint must stop and document the blocker rather than silently substitute a restart-based interaction.

### Login shell resolution

When a terminal is created, use the user’s configured login shell from `$SHELL` when it is an executable local shell. Launch it as a login shell so the user’s expected shell initialization applies, while overriding the initial directory with the selected worktree directory.

If `$SHELL` is absent, malformed, inaccessible, or not a safe executable path, use this visible fallback order:

1. A future approved gabCode-configured local shell command, when such configuration exists.
2. `/bin/zsh`.
3. `/bin/bash`.
4. `/bin/sh`.

The terminal surface must state which fallback was used when shell resolution fails. A missing shell executable produces a recoverable terminal-creation error; it must not crash the application or silently run a different command without notice.

### Exit and cleanup

- When the user requests application termination while one or more terminal processes are active, gabCode always presents a native confirmation alert or sheet.
- The alert identifies how many active terminal processes will be stopped and explains that running Pi or shell work will be interrupted.
- **Cancel** leaves all sessions running and restores focus predictably.
- **Close and Stop Terminals** first requests graceful termination, waits for a bounded period, then terminates remaining process groups/descendants.
- Cleanup covers the local processes, PTY descriptors, output readers, event handlers, tasks, cancellation registrations, and hosted AppKit terminal views.
- A failed or timed-out cleanup is surfaced as diagnostic evidence; it must not leave the UI falsely reporting a clean shutdown.

## Accessibility and Keyboard Requirements

The terminal foundation must preserve a usable native macOS experience without treating terminal content as web content.

- Keyboard-only users can reach the Pi/Commands selectors, switch displayed regions, enter and leave terminal focus, invoke app close/quit, cancel exit, and confirm termination without a pointer.
- Focus order is visible and logical. Switching a terminal view does not lose focus; focus moves to the active terminal content or the invoked selector as appropriate.
- The SwiftUI/AppKit terminal host has a meaningful accessible name, role, and state that distinguishes Pi from Commands without relying on color or position alone.
- Status changes such as starting, ready, shell fallback, terminal failure, natural exit, and closing are exposed through native accessible status/error behavior.
- The exit confirmation has a programmatic name, clear consequence text, a safe default, and focus restoration after cancellation.
- Selection, copy/paste, and search must not be blocked by gabCode keyboard shortcuts.
- The host remains usable with VoiceOver, Full Keyboard Access, increased contrast, text scaling, and reduced-motion settings. Terminal-specific VoiceOver limitations must be stated honestly and treated as a release blocker if they prevent the primary terminal workflow.

## Failure, Recovery, and Resource Rules

| Condition | Required behavior |
| --- | --- |
| SwiftTerm package acquisition/build cannot be reproduced | Stop before relying on it; report the dependency blocker and do not replace it with a web terminal or an unapproved terminal library. |
| Login shell cannot be resolved | Display the reason when safe, use the documented fallback chain, and identify the selected fallback. |
| Worktree directory is missing or inaccessible | Do not launch a shell in an unintended directory; show a recoverable error. |
| One terminal fails to start | Keep the other independent terminal usable; show retry only for the failed session. |
| Child process exits naturally | Preserve bounded terminal output and show an exited state; do not relaunch automatically. |
| Output or input transport fails | Mark the affected terminal failed/exited, release resources, and retain diagnostics without parsing or logging sensitive terminal content. |
| User declines application exit | Preserve active processes and return focus to the prior UI context. |
| Graceful termination times out | Escalate to bounded process-group/descendant termination only after explicit exit confirmation. |

No terminal output, commands, environment secrets, or Pi conversation data may be persisted by this increment. Diagnostics must avoid copying arbitrary terminal content into ordinary logs or UI status.

## Acceptance Criteria

### Dependency and build foundation

- [ ] SwiftTerm is pinned to an exact reviewed version or immutable revision; its license, Swift Package Manager acquisition, supported APIs, and maintenance path are recorded.
- [ ] The implementation uses gabCode-owned abstractions around SwiftTerm hosting, Unix PTY lifecycle, and local process-group cleanup.
- [ ] The existing macOS app and test targets continue to build and test with the repository’s documented selected Xcode toolchain.
- [ ] No browser runtime, web framework, HTTP service, database, alternative unreviewed terminal library, or shared terminal abstraction is introduced.

### Two-session terminal behavior

- [ ] The target macOS application can create independent Pi and Commands login-shell sessions rooted in a selected directory containing spaces and Unicode characters.
- [ ] The sessions have distinct process identities and distinct PTYs and do not share command input or output.
- [ ] The user can run a visible shell command in each session and observe independent results.
- [ ] The configured login shell is used when it can be resolved; a broken/unavailable `$SHELL` follows and visibly reports the documented fallback behavior.
- [ ] The application does not start Pi, resume Pi, inspect Pi, or parse terminal output.
- [ ] ANSI color, Unicode text, input methods, selection, clipboard, resize, scrollback, hyperlinks, and search have target-Mac evidence or are explicitly reported as `NOT CHECKED` with a reason. A missing primary requirement cannot be represented as pass.

### Retention and lifecycle

- [ ] Moving Pi between the main and bottom regions does not change its process identity, process group, PTY, or restart its shell.
- [ ] The Commands session remains alive while Pi is moved or selected.
- [ ] Sessions retain bounded scrollback only in memory and do not survive application exit.
- [ ] The application always prompts before closing active terminal sessions.
- [ ] Cancelling the prompt leaves all processes alive and restores focus predictably.
- [ ] Confirming exit attempts graceful shutdown, then proves bounded process-group/descendant cleanup when necessary.
- [ ] Failure paths release PTY, process, reader, task, and AppKit-host resources without leaving hidden background shells.

### Accessibility and evidence

- [ ] Automated tests cover pure/session lifecycle logic and controlled process behavior where testable.
- [ ] Target-Mac runtime evidence covers two sessions, working directory, process retention across layout movement, resize, natural exit, exit cancellation, confirmed cleanup, and failure recovery.
- [ ] Keyboard-only evidence covers selectors, terminal focus, view movement, exit confirmation, cancellation, and confirmation.
- [ ] VoiceOver, Full Keyboard Access, increased contrast, text scaling, and reduced-motion evidence is recorded on the target machine.
- [ ] Any terminal-control behavior unavailable to automation is marked `NOT CHECKED` until human target-machine evidence exists.

## Validation Expectations

The sprint created from this PRD must inspect actual source and SwiftTerm package surfaces before finalizing commands. At minimum, its validation evidence must include:

- Repository-documented Xcode project discovery, Debug build, and test commands for the macOS app.
- A reproducible pinned SwiftTerm package acquisition/build verification.
- Focused automated lifecycle and process-group-cleanup tests with bounded timeouts and diagnostics.
- Target-Mac launch evidence using a real local login shell and a real temporary directory containing spaces and Unicode.
- Manual or approved native-host evidence for resize/reflow, ANSI/Unicode, input methods, selection/clipboard, search, hyperlink activation safety, keyboard focus, VoiceOver, Full Keyboard Access, increased contrast, scaling, reduced motion, and process cleanup.

Windows validation is **NOT CHECKED** for this macOS-specific increment.

## Risks and Deferred Decisions

- SwiftTerm API behavior, Swift Package Manager/Xcode integration, sandboxing, or AppKit-hosting constraints may make the intended dependency unsuitable. This requires an evidence-based stop/review, not an ad hoc replacement.
- Rehosting a live `NSView` terminal may conflict with SwiftUI view identity, AppKit ownership, responder-chain focus, or renderer state. Process/PTY preservation is a hard acceptance criterion.
- Login-shell startup can execute user profile scripts. The implementation must preserve normal shell expectations while keeping cancellation, launch errors, and diagnostics bounded and safe.
- VoiceOver, input-method, focus, resize, cleanup, signing, and notarization behavior require target-machine validation.
- Final worktree navigation, user preferences, project configuration, panel persistence, app signing, notarization, and distribution are deferred to later approved increments.

## Decision Log

- Use SwiftUI with AppKit hosting and SwiftTerm rather than a browser-hosted terminal or a custom terminal renderer.
- Use local Unix PTYs and login shells for this foundation.
- Treat Pi and Commands as ordinary independent shells; labels do not grant gabCode authority over their contents.
- Require process and PTY retention during terminal-view movement; restarting a terminal is not an acceptable substitute.
- Require explicit user confirmation before stopping active terminal processes.
