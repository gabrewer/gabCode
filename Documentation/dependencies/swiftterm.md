# SwiftTerm macOS feasibility decision

**Decision: BLOCKED — do not begin the two-session product increment.**

SwiftTerm is feasible for the narrow macOS native-terminal foundation seam: a retained
AppKit terminal view accepted real login-shell input in a directory with spaces and Unicode,
kept its PID/process group/PTY identity while moving between two hosts, resized its PTY, and
performed bounded cleanup of an intentionally reparented descendant. The dependency is **not
approved for the later product increment** yet because the required human target-Mac
accessibility/terminal-control evidence has not been performed. In particular VoiceOver and
Full Keyboard Access are `NOT CHECKED`; visual ANSI/Unicode, IME, selection/clipboard,
search, hyperlinks, contrast, text scaling, and reduced motion are also `NOT CHECKED`.

The exact follow-up decision is: a human must exercise and record the outstanding primary
native accessibility and terminal interactions on the target Mac. If VoiceOver or Full Keyboard
Access prevents the ordinary shell workflow, select remediation or a different terminal control;
do not represent this gate as `GO` from automation alone.

## Candidate and acquisition

| Item | Evidence |
| --- | --- |
| Candidate | `https://github.com/migueldeicaza/SwiftTerm.git` |
| Requested Xcode rule | exact version `1.15.0` (`XCRemoteSwiftPackageReference` in `src/GabCode.MacOS/gabCode.xcodeproj/project.pbxproj`) |
| Resolved immutable revision | `dd2fb8ac5b861e7bf617c872895e338f38165648` in `src/GabCode.MacOS/gabCode.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` |
| Product linkage | Only the `gabCode` app target links the `SwiftTerm` product; tests exercise it through `@testable import gabCode`. |
| Resolved source closure | `SwiftTerm 1.15.0` and `swift-argument-parser 1.8.2` at `6a52f3251125d74daf04fcbd5e6f08a75d074382`. `swift-argument-parser` is resolved by SwiftTerm's package graph but is not a gabCode product link. |
| Reproducibility | `xcodebuild -resolvePackageDependencies -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode` passed. A fresh temporary DerivedData and clone directory with `-disablePackageRepositoryCache` also fetched both repositories and resolved the same versions. |

The package was added through Xcode's supported Swift Package Manager workflow. The checked-in
project reference and shared-workspace `Package.resolved` are the reviewable durable state; no
package checkout, DerivedData, or generated package build artifact is committed.

## License, attribution, redistribution, and release implications

The candidate's tagged `LICENSE` is MIT. Its notice names Miguel de Icaza, xterm.js authors,
SourceLair Private Company, and Christopher Jeffrey. Redistribution of SwiftTerm or substantial
portions must include that copyright and permission notice. A distribution/release artifact must
therefore retain the applicable MIT notice in its third-party notices/attribution material.

SwiftTerm is source-built by Xcode/SPM into the application; this gate does not select final
signing, Hardened Runtime, notarization, packaging, or distribution mechanics. Those release
items remain `NOT CHECKED` and require a later approved distribution decision.

## Inspected supported surface and gabCode boundary

The pin was inspected at the resolved revision in the SwiftTerm source:

- `Sources/SwiftTerm/Mac/MacTerminalView.swift`: `TerminalView: NSView`, `NSTextInputClient`,
  terminal/scrollback configuration, native color configuration, and resize callbacks.
- `Sources/SwiftTerm/Mac/MacLocalTerminalView.swift`: `LocalProcessTerminalView`,
  `startProcess(executable:args:environment:execName:currentDirectory:)`, `terminate()`,
  `LocalProcessTerminalViewDelegate`, PTY size updates, and terminal-to-process input.
- `Sources/SwiftTerm/LocalProcess.swift`: local process PID/PTY transport, output/termination
  delegate events, `send(data:)`, `running`, and `terminate()`.
- `Sources/SwiftTerm/Pty.swift`: Unix pseudo-terminal helper surface.

`TerminalSession` is the gabCode-owned seam for the working directory, login-shell launch,
process/session ownership, bounded in-memory scrollback (2,000 lines), view attachment,
input, lifecycle state, and cleanup. `RetainedTerminalHost` is the AppKit/SwiftUI bridge.
SwiftTerm and POSIX types do not leak into `ContentView` or a cross-platform abstraction.
The DEBUG-only `TerminalFeasibilityView`, entered only with
`--terminal-feasibility-directory <path>`, is a one-shell dependency test host, not Pi/Commands
product UI and does not inspect or persist terminal content.

## Sandbox and security finding

Effective Debug and Release build settings both report `ENABLE_APP_SANDBOX = NO`; code signing
remains enabled. This implements the human-approved ordinary-user local-terminal policy needed
for a shell to access worktrees, user tools, SDKs, and child processes. It grants no elevation.
Ordinary Mac App Store distribution is generally incompatible with this policy. Hardened Runtime,
notarization, signing identity choice, and direct-distribution policy are not decided by this gate.

## Target-Mac evidence

Target: Apple Silicon Mac, macOS 26.0+, Xcode 26.6, macOS SDK 26.5, Swift 6.3.3. The controlled
host used a temporary directory whose path contained spaces and `ünicode`; it used one real
local login shell and retained one `LocalProcessTerminalView` while moving that exact view between
AppKit regions. No arbitrary terminal output, commands, or environment values were retained in
this record.

| Interaction | Status | Evidence / limitation |
| --- | --- | --- |
| Package pin and clean resolution | PASS | Standard and isolated `-disablePackageRepositoryCache` `xcodebuild -resolvePackageDependencies` resolution succeeded at the pin above. |
| AppKit-hosted local login shell | PASS | Controlled DEBUG host launched `/bin/sh -l` in the spaces-and-Unicode directory. |
| Dedicated cleanup ownership | PASS | Startup waits for and XCTest asserts the PTY shell invariant `SID == PGID == child PID` before session-wide signals are allowed; this prevents a `forkpty` startup race from capturing the host session. |
| Process/PTY/view identity across rehost | PASS | Runtime evidence: PID `62629`, PGID `62629` unchanged after moving the same AX text-area host from main to bottom. Focused XCTest also covers PID, PGID, PTY FD, and `NSView` identity. |
| Programmatic native focus transfer | PASS | The native Focus action focused the terminal; System Events reported `AXTextArea | SwiftTerm feasibility terminal in the bottom host region`. |
| Keyboard command input and working directory | PASS | System Events typed a controlled command into the focused terminal; it created the expected file in the spaces-and-Unicode directory. |
| Keyboard-only focus entry and return/escape | NOT CHECKED | A human did not exercise entering and leaving terminal focus using only keyboard navigation. Programmatic AX focus is not a substitute. |
| AX role/name for terminal host | PASS | The retained host exposed `AXTextArea` with a region-specific description; move, focus, and stop controls exposed AX identifiers/descriptions. This is not a VoiceOver result. |
| Full Keyboard Access | NOT CHECKED | Requires human target-Mac exercise. |
| VoiceOver | NOT CHECKED | Requires human target-Mac exercise; AX inspection is not a substitute. |
| Resize / PTY size propagation | PASS | Controlled `stty size` changed from `15 116` to `13 89` after native window resize. |
| Natural shell exit observation | PASS | `testSessionObservesNaturalShellExitAndReleasesOnStop` observed `.exited`, then released the session and PTY on bounded stop. |
| Direct and reparented descendant cleanup | PASS | Focused lifecycle suite creates a controlled reparented `sleep`; bounded session/process-group cleanup proves its PID disappears. Runtime Stop completed with no shell/zombie remaining. |
| Exit cancellation leaves the shell alive | NOT CHECKED | The later app-close confirmation UI is out of scope and no human cancellation flow was exercised in this gate. |
| Inaccessible-directory failure | PASS | Focused XCTest rejects both a missing directory and a readable-but-unsearchable directory before launch, with no PID, process group, or PTY. |
| Malformed `$SHELL` fallback | PASS | Focused XCTest supplies a directory as `$SHELL`; gabCode rejects the non-regular-file candidate and executes a controlled marker through the documented fallback chain. |
| PTY input transport | PASS | Controlled terminal input produced expected shell-side files without reading or persisting arbitrary terminal content. |
| Visual output transport/rendering | NOT CHECKED | The shell/PTY ran, but arbitrary terminal output was intentionally not extracted through AX or retained as evidence; human visual exercise remains required. |
| Scrollback bound configuration | PASS | The gabCode seam configures SwiftTerm to 2,000 in-memory lines. |
| Scrollback retention and eviction behavior | NOT CHECKED | The configured bound was inspected but runtime retention/eviction was not exercised. |
| ANSI visual rendering | NOT CHECKED | No human visual terminal rendering exercise. |
| Unicode text rendering | NOT CHECKED | Unicode was verified only in the working-directory path, not visual terminal rendering. |
| IME | NOT CHECKED | Requires human target-Mac exercise. |
| Selection and copy/paste | NOT CHECKED | Requires human target-Mac exercise. |
| Search | NOT CHECKED | Requires human target-Mac exercise. |
| Hyperlink activation safety | NOT CHECKED | Requires human target-Mac exercise. |
| Contrast / text scaling / reduced motion | NOT CHECKED | Requires human target-Mac exercise. |
| Signing, notarization, and distribution | NOT CHECKED | Explicitly deferred by this feasibility gate. |

### Commands and results

```text
xcodebuild -list -project src/GabCode.MacOS/gabCode.xcodeproj
# PASS — gabCode, gabCodeTests, gabCodeUITests; SwiftTerm 1.15.0; parser 1.8.2

xcodebuild -resolvePackageDependencies -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode
# PASS

xcodebuild -resolvePackageDependencies ... -derivedDataPath <fresh> \
  -clonedSourcePackagesDirPath <fresh> -disablePackageRepositoryCache
# PASS — fetched both sources and checked out SwiftTerm 1.15.0 / parser 1.8.2

xcodebuild -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode \
  -configuration Debug -destination 'platform=macOS,arch=arm64' \
  test -only-testing:gabCodeTests/TerminalSessionFoundationTests
# PASS — 6 tests, including inaccessible/search-permission failures, malformed-shell
# fallback, dedicated-session ownership, natural exit, and reparented-descendant cleanup

xcodebuild -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode \
  -configuration Debug -destination 'platform=macOS' build
# PASS — pre-existing manual target-order warning only

xcodebuild -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode \
  -configuration Debug -destination 'platform=macOS' test
# PASS — 6 lifecycle tests and 1 existing UI test

/tmp/mntf-runtime-check.sh
# PASS — launch, AX host/control exposure, retained identity, focus, controlled keyboard/cwd,
# PTY resize, and interactive Stop cleanup
```

## Maintenance rule

Treat SwiftTerm upgrades as a new dependency-gate review: update only through Xcode SPM, inspect
the exact resolved source/revision and license/closure changes, rerun clean resolution, focused
lifecycle tests, full macOS tests, and target-Mac retention/accessibility evidence. Any change to
PTY/process, AppKit hosting, keyboard/accessibility, sandbox, or redistribution implications
requires updating this record and explicit human review before a product increment depends on it.
