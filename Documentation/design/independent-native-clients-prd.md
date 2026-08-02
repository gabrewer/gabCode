# gabCode — Independent Native Clients PRD

| Field | Value |
| --- | --- |
| Status | Approved — architecture decision recorded |
| Date | 2026-08-01 |
| Approved | 2026-08-02 |
| State backend | `github-issues` |
| Planning issue | [#32 — independent native client architecture](https://github.com/gabrewer/gabCode/issues/32) |
| Feature branch | `feature/independent-native-clients` |
| Parent direction | `Documentation/design/gabcode-initial-prd.md` |

## Product Name & One-Liner

**Independent Native Clients** makes gabCode a complete C#/WPF application on Windows and a complete Swift/SwiftUI/AppKit application on macOS, sharing product behavior specifications and test fixtures but no production runtime code.

## Problem & Audience

The initial architecture proposed a shared C# NativeAOT sidecar for Git, GitHub, watchers, normalized state, preferences, and associations. On macOS, that would require the Swift application to package, launch, monitor, version, sign, and communicate with a second executable before gabCode has any shared-core implementation or user data to preserve.

The sidecar reduces duplicate business logic, but it also creates a permanent runtime boundary and makes the macOS application depend on .NET-produced infrastructure. For a native desktop product maintained platform by platform, that integration and packaging cost is not desired.

This architecture is for the developer building and using gabCode on both Windows and macOS. Each application should feel self-contained and native, while factual Git/worktree behavior remains consistent enough that moving between platforms does not change the meaning of project state.

## Approved Decision

Remove the planned C# NativeAOT sidecar and its JSON-over-standard-input/output client/core protocol before either is implemented.

- The Windows application owns its complete implementation in C# and WPF.
- The macOS application owns its complete implementation in Swift, SwiftUI, and AppKit.
- Neither application embeds, launches, packages, or requires the other platform's runtime or implementation.
- The repository shares requirements, vocabulary, language-neutral fixtures, and expected normalized outcomes—not production runtime code.

This is a foundational architecture change to `Documentation/design/gabcode-initial-prd.md`. Authoritative baseline and implementation guidance must follow this approved boundary.

## User Outcome

The user installs and launches one gabCode application for their platform. There is no visible or hidden companion sidecar process to locate, start, recover, update, or diagnose.

Both applications still present equivalent factual concepts—projects, registered worktrees, branch state, clean/dirty state, commits, diffs, PRD/issue associations, and tool availability—while using native platform storage, watching, process, terminal, UI, and accessibility behavior.

## Core Architecture Requirements

### 1. Complete platform-owned applications — Must-have

#### Windows

The Windows C#/WPF application owns:

- native windows, navigation, settings, accessibility, and terminal hosting;
- project registration and path configuration;
- direct `git` and read-only `gh` process execution;
- worktree discovery and normalized project/worktree state;
- filesystem and Git-reference watching plus reconciliation;
- local user-selected PRD/issue associations; and
- all Windows-specific persistence, diagnostics, cancellation, and recovery.

#### macOS

The macOS Swift/SwiftUI/AppKit application owns the equivalent responsibilities using native macOS APIs and conventions, including SwiftTerm/Unix PTY terminal hosting and macOS application preferences.

A feature is not implemented on a platform merely because the other platform has it. Each platform requires its own approved increment and target-operating-system evidence.

### 2. Direct external-authority integration — Must-have

Each native application invokes installed tools directly:

- use installed `git` so behavior matches the user's command line;
- use authenticated `gh` only for approved read-only GitHub queries;
- use structured command output where available, including `git worktree list --porcelain`;
- apply explicit executable resolution, arguments, working directory, environment handling, timeout, cancellation, output bounds, and error behavior;
- never construct tool commands through unsafe shell-string interpolation; and
- keep Git and the filesystem authoritative when cached or watched state differs.

A missing or degraded tool disables only affected capabilities and remains a native-client concern.

### 3. Shared behavior specifications and fixtures — Must-have

Cross-platform parity is maintained without a shared executable or library.

The repository must define language-neutral examples for behavior that should mean the same thing on both platforms. Each fixture contains representative external input and the expected normalized result or error classification. Candidate coverage includes:

- `git worktree list --porcelain` variants;
- branch, detached-HEAD, clean/dirty, ahead/behind, and changed-file state;
- spaces, Unicode, platform path separators, line endings, missing values, and malformed output;
- unique-commit and diff metadata normalization;
- read-only `gh` success, unauthenticated, missing-tool, timeout, and malformed-output states;
- association lifecycle when a worktree disappears; and
- reconciliation after watcher event loss, coalescing, or reordering.

Fixtures define observable facts and vocabulary, not implementation structure. C# and Swift tests consume the same logical cases through platform-owned test code.

Shared fixtures do not replace real integration tests. Each implementation must also exercise real temporary Git repositories, installed tool processes where required, filesystem behavior, cancellation, and cleanup on its target operating system.

### 4. Native watching and reconciliation — Must-have

- Windows uses supported Windows/.NET filesystem and process facilities.
- macOS uses supported macOS/Swift filesystem and process facilities.
- Each implementation treats watcher events as invalidation hints, not authoritative history.
- Both implementations perform bounded periodic reconciliation against Git and the filesystem.
- Event coalescing, overflow, rename patterns, atomic replacement, cancellation, app suspension, and shutdown receive platform-specific tests.

The two implementations need equivalent outcomes, not identical watcher algorithms.

### 5. Platform-owned local data — Must-have

Each client owns local data for that platform and machine:

- registered projects and path overrides;
- window, panel, sidebar, terminal, and other settings;
- selected worktree and navigation state where persistence is approved; and
- explicit user-selected PRD and GitHub issue associations.

No client treats this data as repository truth. Associations add user intent; Git, the filesystem, PRD files, and read-only `gh` results remain authoritative for the content they represent.

There is no built-in cross-platform or cross-machine settings/association synchronization in the initial product.

### 6. Parity evidence and intentional differences — Must-have

The two applications should match on factual product meaning and safety rules, including:

- worktree discovery and identity;
- Git state classifications;
- read-only GitHub capability/error classifications;
- association rules;
- worktree creation/removal safeguards; and
- no-mutation boundaries for source, history, issues, and pull requests.

Intentional differences are expected for native UI layout, menus, keyboard conventions, accessibility, terminal controls, process-tree handling, path presentation, watcher APIs, packaging, signing, and platform settings.

A parity review records whether a difference is:

1. an implementation detail with equivalent behavior;
2. an approved platform-specific product difference;
3. a temporary missing feature; or
4. an unintended drift requiring remediation.

## Product and Authority Boundaries

The existing gabCode boundaries remain:

- gabCode is native desktop software, not a web application.
- Git, the filesystem, and read-only `gh` queries remain authoritative.
- gabCode observes source, PRDs, issues, commits, and diffs; it does not edit or interpret them.
- Terminals remain ordinary user-controlled shells; gabCode does not manage Pi sessions.
- Worktree creation/removal uses guarded Git commands; the application does not silently force destructive operations.
- Local settings and associations do not become a competing project database.

The architecture change affects implementation ownership, not these product safety rules.

## Non-Goals

This architecture will not introduce:

- a C# NativeAOT sidecar or any other shared companion service;
- an internal JSON-over-stdio client/core protocol;
- Swift/.NET in-process interop, FFI, embedded runtimes, or generated language bindings;
- a shared production Git library compiled for both clients;
- HTTP services, local web servers, databases, Marten, event sourcing, or cloud synchronization;
- remote execution, team synchronization, or shared terminal sessions;
- a requirement that Windows and macOS use identical source layouts or platform APIs;
- implementation of both platform versions in one cross-platform sprint; or
- acceptance of one platform's tests as evidence for the other platform.

## Technical Considerations

### Repository organization

Production source remains platform-owned under the existing Windows and macOS client surfaces. Shared repository artifacts are limited to durable product/architecture documentation and deliberately language-neutral conformance inputs and expected outcomes established by later planning.

Do not invent a shared source project merely to avoid visible duplication. If repeated behavior becomes costly, improve the shared specification and fixtures first; any future runtime-sharing proposal requires a new approved architecture decision.

### Normalization vocabulary

Equivalent behavior requires precise shared terms even when model types are implemented twice. Durable specifications should define concepts such as project, primary worktree, linked worktree, worktree identity, branch state, detached state, clean/dirty state, ahead/behind, changed file, unique commit, association, degraded capability, and reconciliation.

The specification owns meaning. C# and Swift may use idiomatic platform types and concurrency models.

### Concurrency and process lifecycle

- Windows and macOS separately own process execution, cancellation, output bounds, watcher coordination, retained terminal resources, and shutdown.
- Each target must prove that cancellation and app exit leave no hidden `git`, `gh`, shell, watcher, or terminal process owned by gabCode.
- Platform-specific races are tested with the platform's native concurrency tools; a passing C# test does not establish Swift behavior, and vice versa.

### Security and privacy

- Do not log arbitrary terminal content, command output containing secrets, authenticated `gh` tokens, or full environments.
- Pass tool arguments without shell injection.
- Keep all settings and associations local unless a later approved feature explicitly introduces synchronization.
- Preserve the read-only GitHub boundary in both implementations.

### Packaging simplification

Removing the sidecar eliminates sidecar architecture selection, process discovery, protocol version skew, standard-output framing, crash recovery, embedded executable signing, and macOS notarization of an additional product binary. Each platform still owns ordinary packaging and signing evidence for its single application and any approved native terminal dependencies.

## Migration and Compatibility

No shared sidecar project, protocol, persisted sidecar state, or released sidecar-based client exists. Therefore:

- no runtime data migration is required;
- no protocol compatibility period is required;
- no dual architecture or fallback sidecar path should be added; and
- active design, orchestration context, and worker guidance must use this boundary before future native data-foundation work is planned.

Historical issue comments may continue to describe the superseded architecture as historical evidence. Current authoritative documents and new plans must use the independent-native-client architecture.

## Acceptance Criteria

### Architecture record

- [ ] The initial PRD identifies complete independent C#/WPF and Swift/SwiftUI/AppKit applications and no shared runtime sidecar.
- [ ] Active project instructions and orchestration guidance assign Git/`gh`, normalization, watchers, settings, and associations to the target native client.
- [ ] No active plan assumes an internal client/core JSON protocol or NativeAOT shared-core build surface.
- [ ] Historical records remain recognizable as historical rather than being rewritten to claim work that did not occur.

### Shared behavior contract

- [ ] A later approved increment establishes language-neutral behavior vocabulary and conformance fixtures before duplicated Git/`gh` normalization grows independently.
- [ ] Both platform implementations can consume the same logical fixture cases without sharing production runtime code.
- [ ] Fixture evidence is supplemented by real target-OS Git, filesystem, watcher, process, and cleanup tests.
- [ ] Intentional platform differences are documented rather than silently normalized away or reported as parity.

### Platform independence

- [ ] The Windows application builds, tests, launches, and performs its approved behavior without Swift or a companion service.
- [ ] The macOS application builds, tests, launches, and performs its approved behavior without .NET or a companion service.
- [ ] Packaging either application does not bundle or launch a gabCode sidecar.
- [ ] Settings and associations remain local and platform-owned.
- [ ] Each platform independently proves Git safety, read-only `gh` behavior, watcher reconciliation, cancellation, and process cleanup.

## Milestones

### Milestone 1 — Realign authoritative architecture

Update the initial PRD, active project/orchestration guidance, and relevant worker boundaries. Retire assumptions that future work should create a sidecar or client/core protocol. Keep this as a reviewable documentation architecture change.

### Milestone 2 — Define cross-platform conformance behavior

Create a focused specification and language-neutral fixture corpus for shared Git/worktree/`gh` vocabulary and normalized outcomes. Establish small fixture consumers in each real platform test surface through separate target-owned tasks.

### Milestone 3 — Build the Windows native data foundation

In a Windows-only approved increment, implement direct tool adapters, normalization, watching/reconciliation, platform persistence, associations, and target-Windows evidence in C#.

### Milestone 4 — Build the macOS native data foundation

In a separate macOS-only approved increment, implement equivalent behavior in Swift with target-Mac evidence. Use the shared conformance cases while preserving native implementation and UX.

## Risks and Mitigations

| Risk | Mitigation |
| --- | --- |
| C# and Swift behavior drifts | Shared vocabulary, language-neutral fixtures, parity review, and explicit accepted-difference records. |
| Twice the implementation and test work | Keep increments small and platform-owned; prioritize factual behaviors; reuse specifications and test data rather than runtime code. |
| Platform tool output differs | Cover path, encoding, line-ending, locale, and tool-version variations; verify with real target-platform integration tests. |
| Watcher behavior differs | Treat events as invalidations and reconcile with Git/filesystem authority on both platforms. |
| One platform advances faster | Mark parity gaps explicitly; never claim cross-platform completion from one platform's evidence. |
| Future pressure recreates a hidden shared service | Require a new human-approved architecture PRD before introducing any runtime-sharing boundary. |

## Open Questions

There are no unresolved questions about the independent-native-client boundary. Fixture schemas, exact paths, and platform implementation increments must be established by later repository-aware planning rather than invented in this PRD.

## Decision Log

- 2026-08-02 — The human approved this independent-native-client architecture as written in issue #32 and authorized the Milestone 1 realignment sprint in issue #36.
- 2026-08-01 — Each native client owns and persists its platform-specific settings.
- 2026-08-01 — The human proposed removing the planned C# NativeAOT sidecar and internal JSON-over-stdio protocol before implementation.
- 2026-08-01 — The proposed direction is complete C#/WPF on Windows and complete Swift/SwiftUI/AppKit on macOS.
- 2026-08-01 — The proposed parity mechanism is shared specifications and language-neutral test fixtures rather than shared runtime code.
- 2026-08-01 — The proposed boundary keeps native UI, watchers, process handling, accessibility, persistence, terminal hosting, and packaging platform-owned.
- 2026-08-01 — This proposal lives on `feature/independent-native-clients` with focused issue #32, separate from `feature/mac-fonts` and its terminal-font PRD.
