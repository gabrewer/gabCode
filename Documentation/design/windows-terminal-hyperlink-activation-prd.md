# gabCode — Windows Terminal Hyperlink Activation PRD

| Field | Value |
| --- | --- |
| Status | Approved for planning |
| Platform | Windows |
| Date | 2026-08-02 |
| Parent direction | `Documentation/design/gabcode-initial-prd.md`, Two Native Terminals per Worktree |
| Sprint | [#51 — Windows Terminal Hyperlink Activation](https://github.com/gabrewer/gabCode/issues/51) |

## Purpose

Make links emitted by agentic CLI harnesses actionable in gabCode's embedded Windows terminals. Hyperlinks are part of the ordinary terminal workflow: a developer must be able to open a web reference or local filesystem target without copying it out of the terminal.

This focused Windows decision supersedes the earlier accepted limitation that terminal hyperlink activation was not required. The macOS terminal already supports hyperlinks and is not changed or requalified by this increment.

## User Outcome

A developer using either retained Windows terminal can activate an OSC 8 hyperlink, a detected web URL, or a detected local filesystem reference through standard terminal gestures. Web targets open in the default browser. Existing local filesystem targets open through their Windows-associated application.

The terminal remains generic. gabCode does not interpret Pi sessions, commands, or arbitrary terminal output and does not infer workflow from activated links.

## Required Behavior

### Link sources

The terminal recognizes:

- OSC 8 hyperlinks whose targets pass the activation policy below;
- plain `http://` and `https://` URLs;
- `file://` URIs for existing local filesystem targets;
- existing absolute Windows paths;
- existing paths relative to the terminal's launch directory;
- optional `:line` or `:line:column` suffixes on local file references.

For this increment, a relative path is resolved against the directory in which that terminal session was launched. If the shell or a child process later changes directory, gabCode does not track that live directory. Cross-platform current-working-directory tracking is separate future work.

When opening through a Windows-associated application, gabCode removes a recognized line/column suffix. Passing line and column to a configured editor is deferred.

Plain local-path detection must be limited to unambiguous rendered path forms and must not cause broad parsing, persistence, logging, or interpretation of terminal output. OSC 8 remains the reliable mechanism for paths containing ambiguous separators or display text.

### Pointer interaction

- `Ctrl+Click` over a recognized hyperlink requests activation.
- Ordinary click continues to perform terminal selection/input behavior and never opens a target.
- Hover provides the terminal's native hyperlink affordance without adding a WPF overlay.
- Hyperlink activation must continue to take precedence over VT mouse reporting only for the explicit activation gesture, matching Windows Terminal behavior.

### Keyboard interaction

Match the pinned Windows Terminal Mark Mode interaction:

1. `Ctrl+Shift+M` enters or leaves Mark Mode.
2. `Tab` selects the next hyperlink and `Shift+Tab` selects the previous hyperlink while Mark Mode is active.
3. `Ctrl+Enter` requests activation of the selected hyperlink.
4. `Esc` clears the Mark Mode selection and returns to ordinary terminal input.

Outside Mark Mode, ordinary terminal key input remains unchanged. Pointer and keyboard activation use the same target-validation and launch policy.

### Activation policy

Only these target classes may launch:

- `http` and `https` URLs through the user's default browser;
- `file://` URIs that resolve to an existing local filesystem target;
- existing absolute or launch-directory-relative local filesystem targets through their Windows-associated application.

The client must normalize and validate the complete target before invoking Windows shell association. It must reject malformed targets, nonexistent local targets, unsupported URI schemes, executable command text, and ambiguous targets that cannot be classified safely. In particular, OSC 8 targets such as `ftp:`, `mailto:`, `shell:`, `javascript:`, custom application schemes, and command lines must not be passed to Windows shell activation.

A rejected target does not launch a process or show a modal dialog over the terminal. Diagnostics must not persist arbitrary terminal content or sensitive target values.

## Windows Terminal Dependency Direction

The approved dependency remains Microsoft Windows Terminal tag `v1.24.11911.0`, commit `5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc`, built for Windows x64 through the repository-owned process.

Upstream investigation found that both the pinned release and the latest inspected `v1.25.1912.0` release contain hyperlink behavior in the full terminal control but do not expose it through `WpfTerminalControl` or its C ABI. An upstream version upgrade alone does not provide the required WPF behavior.

The approved approach is therefore a narrow, reproducible source patch applied during the existing pinned build:

- enable the terminal core's existing URL detection for the WPF HWND host;
- add hyperlink lookup, hover, `Ctrl+Click`, and Mark Mode keyboard handling to the existing native `HwndTerminal` host using Windows Terminal's terminal-buffer and renderer behavior;
- export only the activation callback needed by the managed WPF wrapper;
- surface a managed activation event from `Microsoft.Terminal.Wpf.TerminalControl`;
- let the gabCode Windows client own validation and Windows-associated launch behavior;
- retain and clear native/managed callbacks safely across terminal creation, movement, disconnection, and destruction.

The patch must be stored as a reviewable repository artifact and applied to a clean checkout of the exact pinned commit. The manifest must identify the patch and the resulting managed/native asset hashes. The build must continue to ship the upstream MIT license and `NOTICE.md` and must reject source, patch, or binary drift.

Do not parse ConPTY output in gabCode, draw a second link overlay, use an unofficial terminal package, silently float to another Windows Terminal revision, or replace the native terminal control.

## Native Accessibility

This increment adds a keyboard-only route matching Windows Terminal Mark Mode. Selection and hover state must use the terminal renderer's existing visual behavior rather than color-only WPF chrome. Ordinary shell input must remain unaffected outside Mark Mode.

Target-Windows evidence must cover the keyboard sequence and focus behavior. Narrator announcement of terminal hyperlink selection remains `NOT CHECKED` unless exercised with Narrator on the target machine; static UI Automation inspection is not equivalent evidence.

## Failure and Lifecycle Rules

| Condition | Required behavior |
| --- | --- |
| Unsupported or malformed target | Do not invoke Windows launch facilities; keep the terminal usable. |
| Local target does not exist | Do not launch or fall back to interpreting it as a command or URI. |
| Associated application or browser launch fails | Keep the terminal session alive and surface bounded, non-content-bearing diagnostics. |
| Relative target follows a shell `cd` | Continue resolving against the launch directory in this increment. |
| Terminal is moved between retained regions | Preserve hyperlink behavior without replacing the control or process. |
| Terminal closes during or after activation | Clear callback ownership before native destruction; no callback may reach a disposed managed or gabCode object. |
| Patched dependency cannot be reproduced | Stop and report the dependency blocker; do not substitute untracked binaries. |

## Non-Goals

- macOS implementation or requalification;
- live shell current-working-directory tracking;
- configurable editor selection or line/column-aware editor launch;
- arbitrary URI scheme activation;
- remote, WSL, SSH, or container path translation;
- terminal search;
- parsing or interpreting CLI-harness output in gabCode;
- link previews, history, persistence, telemetry, or workflow inference;
- changing terminal process ownership, retention, ConPTY behavior, or safe multiline paste behavior.

## Acceptance Criteria

### Dependency and build

- [ ] A clean checkout of Windows Terminal `v1.24.11911.0` at commit `5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc` accepts exactly the repository-owned hyperlink patch and builds the approved x64 WPF target.
- [ ] The dependency manifest records the patch identity and exact hashes/sizes of the resulting `Microsoft.Terminal.Wpf.dll` and `Microsoft.Terminal.Control.dll`.
- [ ] Verification rejects a changed patch, unexpected source modification, mismatched binary pair, or unapproved asset.
- [ ] Upstream `LICENSE` and `NOTICE.md` obligations remain satisfied.

### Terminal behavior

- [ ] A target-Windows terminal renders and activates an OSC 8 `https` link with `Ctrl+Click`.
- [ ] A plain detected `http` or `https` URL can be activated.
- [ ] A supported OSC 8 or detected local reference resolves an existing absolute, `file://`, or launch-directory-relative target.
- [ ] Optional `:line` and `:line:column` suffixes are removed before associated-application launch.
- [ ] Hover provides native link affordance, while ordinary click preserves terminal selection/input behavior.
- [ ] `Ctrl+Shift+M`, `Tab`/`Shift+Tab`, `Ctrl+Enter`, and `Esc` provide keyboard hyperlink navigation and activation without changing ordinary input outside Mark Mode.
- [ ] Both retained terminal controls preserve hyperlink behavior after exchanging main and lower regions.

### Safety and lifecycle

- [ ] `ftp`, `mailto`, `shell`, `javascript`, custom schemes, malformed targets, nonexistent paths, and command text produce no process launch.
- [ ] Pointer and keyboard paths use one validation and launch policy.
- [ ] Browser/association launch failure does not crash, stop, or replace the terminal session.
- [ ] Callback registration remains rooted for the native terminal lifetime and is cleared before native terminal destruction.
- [ ] Repeated create, activate, move, close, and application-shutdown scenarios produce no callback-after-disposal failure or retained terminal process.
- [ ] No terminal output or activated target content is persisted in ordinary logs or metadata.

### Evidence

- [ ] Repository Windows restore, Release build, and automated tests pass.
- [ ] The patched dependency is rebuilt and verified on the target Windows toolchain.
- [ ] Target-Windows evidence covers OSC 8, plain web URL, supported local references, hover, `Ctrl+Click`, Mark Mode keyboard activation, rejection cases, retained-view movement, and teardown.
- [ ] Accessibility evidence reports keyboard-only, focus, terminal integration, and any Narrator checks as `PASS`, `FAIL`, or `NOT CHECKED` with reasons.

## Risks and Deferred Decisions

- Plain local paths are syntactically ambiguous in terminal text. Detection must remain conservative; CLI harnesses should use OSC 8 when the display form cannot be classified safely.
- The patch touches a native/managed callback boundary and terminal input handling. Teardown races and unintended key interception require target-Windows adversarial evidence.
- The patch becomes part of the Windows Terminal dependency maintenance burden and must be rebased and requalified for every future upstream pin.
- Live working-directory tracking and editor-aware line/column launch should be designed as later cross-platform behavior rather than hidden in this Windows-only increment.

## Decision Log

- 2026-08-02 — Make terminal hyperlinks required on Windows because agentic CLI harnesses use them as an ordinary navigation surface.
- 2026-08-02 — Support OSC 8, web URLs, existing absolute/relative/file-URI targets, and optional line/column suffixes.
- 2026-08-02 — Resolve relative paths against the terminal launch directory; defer live current-directory tracking.
- 2026-08-02 — Permit only `http`, `https`, and existing local filesystem targets; reject every other scheme.
- 2026-08-02 — Use `Ctrl+Click` and Windows Terminal's Mark Mode keyboard sequence.
- 2026-08-02 — Keep the approved Windows Terminal pin and carry a narrow reproducible wrapper/native-host patch because no inspected upstream WPF release exposes hyperlinks.
