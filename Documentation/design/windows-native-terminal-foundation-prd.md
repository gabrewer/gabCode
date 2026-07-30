# gabCode — Windows Native Terminal Foundation PRD

| Field | Value |
| --- | --- |
| Status | Approved — dependency decision recorded |
| Platform | Windows |
| Parent direction | `Documentation/design/gabcode-initial-prd.md`, Milestone 1 |
| Issues | [#13 — Windows Terminal WPF Dependency Gate](https://github.com/gabrewer/gabCode/issues/13); [#15 — Windows Terminal Runtime Foundation](https://github.com/gabrewer/gabCode/issues/15) |

## Purpose

Establish a production-foundation native terminal host in the gabCode WPF client. The increment proves that gabCode can host and retain two ordinary local shell terminals for one worktree without becoming an editor, a terminal multiplexer, or a Pi-session manager.

This is the Windows half of the native terminal foundation. It is independent of the macOS implementation and does not introduce a shared terminal abstraction or shared-core protocol.

## User Outcome

A developer can open the Windows gabCode prototype, choose or receive a worktree directory for the terminal host, and use two independent terminals:

- **Pi** — an ordinary shell where the developer may run `pi` or `pi --resume`.
- **Commands** — an ordinary shell where the developer runs builds, tests, Git, and other commands.

The developer can switch which terminal occupies the main region and can move the Pi terminal between the main region and a bottom-panel host without restarting its shell or losing its visible session. gabCode labels the terminal regions, but never interprets terminal output or controls Pi’s session lifecycle.

## Product Boundary

### In scope

- A WPF-native terminal foundation under `src/GabCode.Windows/`.
- Two independently launched local terminal sessions rooted in one selected worktree directory.
- A minimal native layout that demonstrates a main terminal region and a bottom terminal region.
- Moving the existing Pi terminal view between those regions without restarting the process.
- Retaining terminal sessions while switching between Pi and Commands views in this prototype.
- Windows Terminal profile discovery where practical, with a clear local fallback chain.
- Windows Terminal WPF control acquisition, reproducible build, licensing, and redistribution evidence for the exact pinned source revision.
- ConPTY-backed local process creation, input/output, resize, and cleanup behavior.
- Bounded in-memory scrollback.
- Native keyboard input, selection, clipboard, ANSI/VT, Unicode, resize, bounded scrollback, and the terminal control's existing UI Automation provider.
- Mandatory exit confirmation when any terminal process is still active.
- Graceful terminal shutdown followed by bounded descendant-process cleanup after explicit confirmation.
- Automated and target-Windows evidence for lifecycle, process retention, failure states, and native accessibility.

### Explicitly out of scope

- macOS, SwiftTerm, Unix PTYs, and cross-platform terminal implementation.
- The C# NativeAOT sidecar, JSON protocol, Git, `gh`, filesystem watchers, worktree discovery, project registration, status, associations, and preferences.
- Creating, removing, selecting, or navigating worktrees in gabCode. The foundation may use a controlled test worktree directory supplied by the host or test harness.
- Reading, recording, parsing, summarizing, or otherwise interpreting terminal output.
- Starting Pi automatically, sending Pi prompts, detecting Pi sessions, recording sessions, or invoking `pi --resume`.
- Terminal tabs beyond the required Pi and Commands surfaces, terminal splitting, remote terminals, SSH, terminal collaboration, or persistence after application exit.
- Source editing, Git mutation, VS Code integration, PR/issue mutations, signing, distribution, or installer work.
- A shared Windows/macOS terminal abstraction. Each platform owns its native terminal implementation.

## Required Windows Technology Direction

- **Host UI:** WPF with C# on the existing `net10.0-windows` application surface.
- **Terminal engine/control:** a gabCode-pinned build of Microsoft Windows Terminal’s WPF control from upstream tag `v1.24.11911.0`.
- **PTY/process transport:** ConPTY.
- **Windows SDK:** `10.0.22621.0`.
- **Native toolchain:** supported Visual Studio C++/UWP tooling needed to reproduce the pinned terminal-control build.

The implementation must not depend on an unofficial repackaged terminal-control NuGet package. gabCode must isolate the upstream control behind gabCode-owned Windows terminal hosting and lifecycle abstractions so upstream APIs do not spread through the WPF UI.

The completed dependency gate is recorded in `Documentation/dependencies/windows-terminal-wpf.md`. It approves Windows x64 integration from the pinned source revision with an explicit x64 runtime-asset strategy and accepted limitations. Implementation must preserve the recorded license, notice, provenance, WPF hosting boundary, and upgrade constraints.

## Native Experience

### Initial state

The existing `gabCode` WPF application window remains the application shell. This increment introduces a terminal-foundation surface with:

- A clearly named main terminal region.
- A clearly named bottom terminal region.
- Visible Pi and Commands selectors that identify which independent session is displayed in each region.
- A user-visible terminal lifecycle state: starting, ready, failed, or closing.
- A non-terminal empty or failure surface that explains what failed and offers a safe retry when session creation has not succeeded.

This is a foundation UI, not the final worktree navigator. It may use a controlled worktree path appropriate for target-machine verification until project/worktree navigation exists.

### Pi and Commands behavior

- Each terminal is an independent child process and independent terminal session.
- Both receive the same selected working directory at creation.
- Both receive the selected/default shell profile independently.
- Commands entered in one terminal do not appear in or affect the other terminal.
- The app does not reserve ordinary shell input for Pi-specific behavior.
- The labels **Pi** and **Commands** describe intended use only; either terminal can run any local shell command.

### Layout retention

- The Pi terminal process has identity independent of the WPF region currently displaying it.
- Moving Pi between the main region and bottom panel reparents or otherwise retains the same hosted terminal view/session; it must not create a replacement shell.
- Moving or selecting a terminal preserves its scrollback, cursor state, and active child process when the selected terminal control supports those states.
- The Commands terminal remains available while Pi is shown in the main region, and vice versa.
- If retaining/reparenting a control is technically impossible without process restart, the sprint must stop and document the blocker rather than silently substitute a restart-based interaction.

### Shell and profile resolution

When a terminal is created, resolve the user’s configured Windows Terminal default profile when practical and safe. Preserve that profile’s command line, environment, and other terminal-relevant configuration where the embedded hosting boundary supports it. Override the initial directory with the selected worktree directory.

If the Windows Terminal profile cannot be resolved, is malformed, points to an unavailable executable, or cannot be represented safely in the embedded host, use this visible fallback order:

1. A gabCode-configured local shell command, when such configuration exists in a future approved increment.
2. `pwsh`.
3. Windows PowerShell.
4. `cmd.exe`.

The terminal surface must state which fallback was used when profile resolution fails. A missing shell executable produces a recoverable terminal-creation error; it must not crash the WPF process or silently run a different command without notice.

### Exit and cleanup

- When the user requests application exit while one or more terminal processes are active, gabCode always presents a native confirmation dialog.
- The dialog identifies how many active terminal processes will be stopped and explains that running Pi or shell work will be interrupted.
- **Cancel** leaves all sessions running and restores focus predictably.
- **Close and Stop Terminals** first requests graceful termination, waits for a bounded period, then terminates remaining terminal descendant process trees.
- Cleanup covers the terminal shell, ConPTY resources, output readers, event handlers, cancellation registrations, and hosted WPF views.
- A failed or timed-out cleanup is surfaced as diagnostic evidence; it must not leave the UI falsely reporting a clean shutdown.

## Accessibility and Keyboard Boundary

The gabCode-owned WPF chrome uses ordinary native names, roles, focus order, and dialog behavior. Pi/Commands selectors, lifecycle status, errors, retry actions, and exit confirmation must remain understandable without relying only on color or position.

The Windows Terminal WPF content surface is accepted as provided by the pinned dependency:

- ordinary terminal input remains owned by the terminal;
- `Tab` is not required to escape the terminal HWND to sibling WPF controls;
- terminal search and hyperlink activation are not product requirements;
- dedicated Narrator, IME, high-contrast, text-scaling, and reduced-motion qualification of terminal content is not required;
- the existing focusable `WPFTermControl` UI Automation text provider is retained without a gabCode-owned accessibility bridge.

These are explicit accepted product limitations, not deferred release blockers. They may be revisited only through a future human product decision.

## Failure, Recovery, and Resource Rules

| Condition | Required behavior |
| --- | --- |
| Terminal-control build/acquisition cannot be reproduced | Stop before relying on it; report the dependency blocker and do not replace it with an unofficial package without an approved PRD change. |
| Default profile cannot be resolved | Display the reason when safe, use the documented fallback chain, and identify the selected fallback. |
| Worktree directory is missing or inaccessible | Do not launch a shell in an unintended directory; show a recoverable error. |
| One terminal fails to start | Keep the other independent terminal usable; show retry only for the failed session. |
| Child process exits naturally | Preserve bounded terminal output and show an exited state; do not relaunch automatically. |
| Output or input transport fails | Mark the affected terminal failed/exited, release resources, and retain diagnostics without parsing or logging sensitive terminal content. |
| User declines application exit | Preserve active processes and return focus to the prior UI context. |
| Graceful termination times out | Escalate to bounded descendant-process termination only after explicit exit confirmation. |

No terminal output, commands, environment secrets, or Pi conversation data may be persisted by this increment. Diagnostics must avoid copying arbitrary terminal content into ordinary logs or UI status.

## Acceptance Criteria

### Dependency and build foundation

- [ ] The exact Windows Terminal upstream tag is pinned to `v1.24.11911.0`, its license is recorded, and reproducible acquisition/build instructions exist in repository-owned documentation or build configuration.
- [ ] The implementation uses a gabCode-owned abstraction around the Windows Terminal WPF control and ConPTY lifecycle.
- [ ] The existing Windows application and test projects continue to build and test with the repository-pinned .NET SDK.
- [ ] No unofficial terminal-control package, browser runtime, web framework, HTTP service, database, or shared terminal abstraction is introduced.

### Two-session terminal behavior

- [ ] The target Windows application can create independent Pi and Commands shell sessions rooted in a selected directory containing spaces and Unicode characters.
- [ ] The sessions have distinct process identities and do not share command input or output.
- [ ] The user can run a visible shell command in each session and observe independent results.
- [ ] The configured Windows Terminal default profile is used when it can be resolved; a broken/unavailable profile follows and visibly reports the documented fallback behavior.
- [ ] The application does not start Pi, resume Pi, inspect Pi, or parse terminal output.
- [ ] ANSI color, Unicode text, selection, clipboard, resize, and bounded scrollback have target-Windows evidence. Search and hyperlink activation are not required.

### Retention and lifecycle

- [ ] Moving Pi between the main and bottom regions does not change its process identity or restart its shell.
- [ ] The Commands session remains alive while Pi is moved or selected.
- [ ] Sessions retain bounded scrollback only in memory and do not survive application exit.
- [ ] The application always prompts before closing active terminal sessions.
- [ ] Cancelling the prompt leaves all processes alive and restores focus predictably.
- [ ] Confirming exit attempts graceful shutdown, then proves bounded descendant-process cleanup when necessary.
- [ ] Failure paths release PTY, process, reader, and WPF-host resources without leaving hidden background shells.

### Native host and evidence

- [ ] Automated tests cover pure/session lifecycle logic and controlled process behavior where testable.
- [ ] Target-Windows runtime evidence covers two sessions, working directory, process retention across layout movement, resize, natural exit, exit cancellation, confirmed cleanup, and failure recovery.
- [ ] GabCode-owned selectors, lifecycle status, errors, retry actions, and exit confirmation expose standard WPF names and roles.
- [ ] The existing terminal UI Automation text provider remains present. No additional terminal-content accessibility-mode qualification is required.

## Validation Expectations

The sprint created from this PRD must inspect actual source and dependency surfaces before finalizing commands. At minimum, its validation evidence must include:

- Repository-pinned `dotnet restore`, Release build, and tests for the Windows solution.
- A reproducible pinned Windows Terminal WPF control acquisition/build verification.
- Focused automated lifecycle and process-cleanup tests with bounded timeouts and diagnostics.
- Target-Windows launch evidence using a real local shell and a real temporary directory containing spaces and Unicode.
- Manual or approved native-host evidence for resize/reflow, ANSI/Unicode, selection/clipboard, the existing UI Automation text provider, live-view retention, and process cleanup.

macOS validation is **NOT CHECKED** for this Windows-specific increment.

## Risks and Deferred Decisions

- The Windows Terminal WPF control is approved for Windows x64 integration. Build, redistribution, and WPF-hosting changes at a future pin remain an evidence-bearing upgrade risk; do not substitute an ad hoc dependency.
- Reparenting a live native terminal control may conflict with WPF visual-tree, dispatcher, focus, or renderer ownership. Process preservation is a hard acceptance criterion.
- Profile settings may include dynamic profiles, custom command lines, environment values, or terminal features that cannot safely map to embedded hosting. gabCode must degrade visibly rather than pretend full fidelity.
- Keyboard focus escape, terminal search/hyperlinks, and dedicated Narrator, IME, high-contrast, scaling, and reduced-motion qualification are accepted limitations of the pinned WPF content surface.
- Final worktree navigation, user preferences, project configuration, panel persistence, app packaging, and signing are deferred to later approved increments.

## Decision Log

- Use WPF and Windows Terminal’s WPF control rather than a browser-hosted terminal or a custom C# terminal renderer.
- Pin Windows Terminal to `v1.24.11911.0` and target Windows SDK `10.0.22621.0` for this foundation.
- Treat Pi and Commands as ordinary independent shells; labels do not grant gabCode authority over their contents.
- Require process retention during terminal-view movement; restarting a terminal is not an acceptable substitute.
- Require explicit user confirmation before stopping active terminal processes.
- Approve Windows Terminal WPF tag `v1.24.11911.0` for Windows x64 integration with the limitations recorded in `Documentation/dependencies/windows-terminal-wpf.md`.
- Do not require keyboard focus escape, terminal search, hyperlink activation, or dedicated accessibility-mode qualification of the upstream terminal content surface.
- Treat `ITerminalConnection` and child-process lifetime as gabCode-owned responsibilities; the upstream wrapper is not required to call `Close()` automatically.
