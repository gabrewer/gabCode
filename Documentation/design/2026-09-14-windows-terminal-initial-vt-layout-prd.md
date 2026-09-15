# gabCode — Windows Terminal Initial VT Layout PRD

| Field | Value |
| --- | --- |
| Status | Proposed |
| Date | 2026-09-14 |
| Platform | Windows |
| Parent direction | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related foundation | `Documentation/design/2026-07-30-windows-native-terminal-foundation-prd.md` |
| Sprint | [#95 — Windows Terminal Initial VT Layout](https://github.com/gabrewer/gabCode/issues/95) |

## Product Name & One-Liner

**Windows Terminal Initial VT Layout** — Ensure an interactive terminal application’s first screen renders at the correct location in an already-open gabCode Windows terminal, without requiring a user resize.

## Problem & Audience

A developer running Pi in an already-open gabCode Windows terminal sees Pi’s initial UI print, then subsequent Pi output and input overwrite text in the middle of that UI. Resizing the terminal immediately corrects the layout. The same Pi command, PowerShell profile, and working directory render correctly in standalone Windows Terminal and in gabCode for macOS.

gabCode hosts ordinary user-controlled shells and terminal applications. It must provide correct initial terminal geometry and standard VT behavior without recognizing, interpreting, or managing Pi. Pi is the reported and primary target-machine regression case, not a special runtime integration.

## Core Features

1. **Correct first-frame terminal rendering — Must-have**
   A standard-VT interactive application started in an already-open embedded Windows terminal renders its initial UI and all subsequent output at the correct location, without overlap, overwrite, or a user resize.

2. **Generic standard-VT compatibility — Must-have**
   Correct the Windows host/control/ConPTY initialization behavior at its actual failure point for the minimal standard VT control sequence involved; do not branch on executable name, command text, or Pi output.

3. **No-resize recovery requirement — Must-have**
   Starting Pi in an existing gabCode terminal after the terminal has been open for any duration must render and continue rendering correctly without manually resizing the terminal.

4. **Generic regression fixture — Must-have**
   Add a small controlled terminal fixture that produces the diagnosed standard VT sequence without depending on Pi. Automated coverage verifies gabCode-owned initialization and geometry propagation; target-Windows fixture evidence verifies the rendered result.

5. **Target-Windows Pi evidence — Must-have**
   Verify on Windows that a user can launch Pi with the configured PowerShell profile and worktree directory, view its initial UI, enter input, and receive subsequent output with no overwrite or position shift.

6. **Preserve existing terminal behavior — Must-have**
   Keep ordinary shell startup, terminal resize/reflow, input compatibility, scrollback, retained-view behavior, and process lifecycle/cleanup correct.

## Non-Goals

- Detecting Pi, changing Pi commands, parsing Pi output, or managing Pi sessions.
- Adding a Pi-specific resize, delay, redraw, profile, or executable-name workaround.
- Replacing the approved Windows Terminal WPF control or ConPTY solely to avoid diagnosis of the current host behavior.
- Changing macOS terminal production code; macOS remains a known-good parity reference and requires no evidence for this Windows-only increment.
- Persisting terminal output, commands, terminal dimensions, or diagnostic content that could expose user data.
- Adding terminal features unrelated to initial VT layout correctness.

## Technical Considerations

- The implementation is limited to the Windows C#/WPF terminal-host path under `src/GabCode.Windows/`, the gabCode-owned wrapper around the pinned Microsoft Windows Terminal WPF control, and its ConPTY lifecycle.
- Diagnose the full initialization path before changing behavior: WPF measure/arrange and control creation, initial terminal sizing, connection startup, ConPTY size propagation, and the terminal control’s handling of the relevant standard VT sequence.
- Establish the minimal generic reproducer from observed standard terminal behavior. The fixture must not embed, launch, inspect, or depend on Pi.
- Do not use a fixed startup delay or an application-identity branch. A generic correction is acceptable only when it establishes correct terminal state for every hosted interactive process, not when it imitates a user resize for Pi.
- Standalone Windows Terminal using the same PowerShell profile and working directory is the target-machine behavioral comparison. It is not a runtime dependency.
- Use existing Windows/.NET and pinned-terminal-control APIs only after inspecting their real surfaces and compatibility; preserve the existing control pin and package/build provenance unless diagnosis proves a separately approved dependency change is required.
- Automated tests should cover gabCode-owned geometry, initialization ordering, and connection logic where deterministic. Target-Windows fixture and Pi evidence must verify rendered behavior because WPF/native-control pixels cannot be fully proven by unit tests.

## Milestones

1. **Reproduce and isolate**
   Reproduce the failure in an already-open Windows gabCode terminal, including after it has remained open for several seconds, capture the minimal relevant standard VT behavior, and identify the responsible host/control/ConPTY initialization boundary.

2. **Fixture and failing coverage**
   Create the generic VT fixture, focused automated coverage for gabCode-owned initialization and geometry logic, and target-Windows baseline rendering evidence. Record baseline failure evidence before the repair where feasible.

3. **Generic host repair**
   Implement the smallest Windows host initialization correction at the diagnosed boundary, without Pi recognition or a timing-based workaround.

4. **Windows validation and regression review**
   Build and test the Windows client; validate the fixture, Pi’s first and subsequent frames without resize, normal shell behavior, ordinary resize/reflow, and terminal lifecycle retention/cleanup on a target Windows machine.

## Open Questions

- Which exact standard VT control sequence and initial terminal-state value expose the fault?
- Is the defect in gabCode’s WPF lifecycle/initial sizing, its ConPTY adapter, the pinned terminal-control integration, or an interaction across those boundaries?
- Which gabCode-owned initialization and geometry values can the automated fixture assert while target-Windows evidence confirms the rendered result?
- Does diagnosis identify an upstream terminal-control defect that needs a separately approved dependency upgrade or patch decision?
