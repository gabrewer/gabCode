# Windows terminal: open the plain URL currently on screen

**Platform:** Windows x64 · **Status:** Planned; fix and browser launch not verified

**Sprint plan:** [#143 — Refresh plain URLs in Windows terminal output and verify Ctrl+Click](https://github.com/gabrewer/gabCode/issues/143)
**Feature baseline:** [Windows Terminal Hyperlink Activation](windows-terminal-hyperlink-activation-prd.md) · [original sprint #51](https://github.com/gabrewer/gabCode/issues/51) · [shipped patch PR #136](https://github.com/gabrewer/gabCode/pull/136)

## Problem and outcome

A developer runs a command in an already-open terminal. It prints a web URL as ordinary text, with no OSC 8 hyperlink metadata. The developer should be able to Ctrl+Click the printed URL and see that destination in the default browser without reprinting, scrolling, resizing or restarting the terminal.

On the shipped Windows control, a [hosted HWND regression test](https://github.com/gabrewer/gabCode/issues/51#issuecomment-7209646370) printed a plain HTTPS URL after initialization and delivered Ctrl+Click at its cell. It expected one activation event and received none. That is evidence of a failure **before** the browser launcher, not a browser diagnosis. PR #136's OSC 8 smoke and the target-validation tests exercise different paths. The leading explanation is that the WPF host enables pattern detection at startup but, unlike the full Terminal control, does not refresh its match locations after output.

## What the user must be able to do

| Scenario | Required result |
| --- | --- |
| A plain `http://` or `https://` URL appears after startup, including across two output writes | Once the complete URL is visible, native hover identifies it and one Ctrl+Click requests exactly one activation of that URL, even while unrelated output continues on another row. The producer need not pause. |
| Output overwrites the URL in place, first with ordinary text and then a different URL | A Ctrl+Click at the same cell immediately after ordinary text overwrites it produces no activation, even if a refresh is pending. Once the replacement URL is visible, activation uses only that URL. |
| Scrolling, resizing/reflow or swapping the two retained terminals changes what occupies a screen cell | Hit testing follows the visible text in that terminal, not the previous coordinates or the other terminal. No shell restart or focus grab to refresh recognition. |
| The user clicks without Ctrl, selects text, or types while output streams | No browser launch. Preserve ordinary terminal selection, shell input and VT mouse behavior. Recognition must not queue unbounded work or require scanning all scrollback for every write. |
| The user navigates without a mouse | Preserve Mark Mode: `Ctrl+Shift+M`, `Tab`/`Shift+Tab` to select, `Ctrl+Enter` to activate, `Esc` to leave. Use the same validation policy as pointer activation. |
| An OSC 8 link is shown, a target is rejected, or the terminal closes with a refresh pending | OSC 8 retains its explicit target; no new schemes or local path forms become launchable. No modal error, duplicate activation, callback after disposal or persisted terminal contents. |

## Implementation boundary

Keep the pinned Windows Terminal `v1.26.3133.0` (`c4e28e32c0c868474340eb4ae415096aab9026d7`) and the existing HWND → WPF event → session validation → Windows association path. Use TerminalCore's buffer and renderer; investigate its pattern invalidation and hover interval handling before choosing a refresh strategy. Coalesce work during bursts, handle a click racing with a pending refresh safely, and tie any scheduled work to the host lifetime. Rebuild and ship the matched DLL pair using the [repository build](../../eng/windows-terminal/README.md). Asset-layout verification alone does not show that the running app consumed the rebuilt control.

No macOS change, upstream upgrade, new parser/overlay, new ABI, broader URL grammar, plain local-path recognition, browser automation framework, telemetry, or change to launch policy. Existing safe local-file and OSC 8 behavior is a regression guard, not a new feature.

## Acceptance evidence (Windows)

1. **Hosted control:** Run the same regression against shipped and rebuilt binaries. Record RED then GREEN for the real WPF HWND receiving output and native mouse messages: exact event target and count, plain click, erase, replacement. Keep URL A visible at a fixed cell while a producer continuously updates a different row. Without pausing the producer, Ctrl+Click A and assert exactly one event for A. Overwrite A with ordinary text, send Ctrl+Click to the same cell without waiting for a refresh, then drain pending events and assert the count is still one. Also cover split output and viewport changes; bound waits and close the host. This proves detection and event delivery, **not** browser launch.
2. **Full application — blocking:** Run the Release gabCode built with the rebuilt assets. In a PowerShell terminal print a controlled plain URL, e.g. `Write-Output ('https://example.org/#' + 'gabcode-main')`. Click the **output**, not the command echo. Ordinary click must open nothing. Keep the printed URL visible while updating a separate status row continuously. Without stopping that output, physically Ctrl+Click the URL once and observe one navigation to its unique fragment in the default browser's address bar (an existing browser window is valid). Overwrite the URL with ordinary text and immediately Ctrl+Click the same cell; verify there is no second launch or navigation. Repeat with a different marker in the second terminal after a pane swap. Record build/asset identity, Windows version, gestures, expected and observed destinations. Use only controlled targets. No screenshot of unrelated terminal content is needed.
3. **Regression smoke:** Check scroll/resize, overwrite, keyboard activation and return to input, OSC 8, rejected target, output burst and close. Record observations separately from the hosted test. Run the Release suite and compare any failures to the shipped baseline. Record unexercised accessibility checks, including Narrator, as `NOT CHECKED` rather than inferring them from UI Automation.

A classifier pass, a managed event, an attempted shell launch, or an OSC 8 browser launch does **not** satisfy the plain-URL browser gate. If an interactive Windows desktop or default browser is unavailable, report the gap and leave acceptance pending.
