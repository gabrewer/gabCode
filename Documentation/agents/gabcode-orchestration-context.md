# gabCode Orchestration Context

## Purpose

This is a thin project profile for generating gabCode's Pi prompts and skills. It does not define another orchestration workflow.

Use these documents together:

1. `Documentation/design/gabcode-initial-prd.md` — product scope and architecture.
2. `TEAM-ORCHESTRATION.md` — roles, planning/execution flow, state backends, and quality gates.
3. `TOOL-PI.md` — Pi prompt, skill, extension, and model-routing mechanics.
4. This file — gabCode-specific interpretation of the generic roles.

If this file appears to redefine a canonical workflow or product decision, defer to the appropriate source above.

## Project profile

| Area | gabCode interpretation |
| --- | --- |
| Product | Native desktop navigator and observer for Git worktrees |
| Windows client | Complete C#/WPF application; owns Windows UX, terminals, direct Git/`gh`, normalization, watchers/reconciliation, local data, diagnostics, and cleanup |
| macOS client | Complete Swift/SwiftUI/AppKit application; owns equivalent behavior with native macOS facilities |
| Shared cross-platform artifacts | Requirements, vocabulary, language-neutral conformance inputs, and expected outcomes; no shared production runtime code |
| Internal runtime boundary | None; no gabCode companion sidecar or client/core protocol |
| External authorities | Installed `git`, authenticated read-only `gh`, and the filesystem |
| Terminal model | Ordinary user-controlled shells; gabCode does not interpret Pi output or manage Pi sessions |
| Repository state | Real Windows and macOS build/test surfaces exist; native data foundations and conformance fixtures require later approved increments |

The initial PRD remains authoritative for detailed behavior, non-goals, technology choices, and milestone content. Generated skills should reference it rather than copying it wholesale.

## Important adaptations of the generic workflow

`TEAM-ORCHESTRATION.md` contains examples that commonly describe a web frontend, HTTP backend, browser tests, authentication, and persistence. For gabCode, interpret those concepts as follows:

| Generic term | gabCode meaning |
| --- | --- |
| Frontend | The complete target native Windows or macOS application, including platform-owned data/tool behavior |
| Backend | No default gabCode backend; use only if a future human-approved architecture decision introduces one |
| API contract | No internal client/core contract; use this term only for an explicitly approved boundary or language-neutral artifact schema |
| Runtime validation | Running the native application on its target operating system |
| Integration test | A target-owned test across relevant process, filesystem, Git, `gh`, PTY, or native-hosting boundaries |
| Browser validation | Not applicable unless a future approved feature introduces a browser surface |
| Authentication/tenancy | Not assumed for the initial local, single-user product |

Do not introduce web frameworks, HTTP services, databases, Marten/event sourcing, Vitest, or Playwright merely because they appear in generic orchestration examples.

## Workstream boundaries

Planning should identify one owning workstream:

- **Windows client** — the complete C#/WPF application, including Windows Terminal control/ConPTY, direct Git/`gh`, normalization, watchers/reconciliation, local data, native process behavior, and UX.
- **macOS client** — the complete Swift/SwiftUI/AppKit application, including SwiftTerm/Unix PTY, direct Git/`gh`, normalization, watchers/reconciliation, local data, native process behavior, and UX.
- **Shared specification/fixtures** — durable behavior vocabulary, language-neutral conformance inputs, and expected outcomes only; never a production source project or runtime service.

Windows and macOS implementation must be planned as separate platform increments and validated on their respective operating systems. Shared specification or fixture work must be explicit and cannot hide production implementation inside a cross-platform task. A new internal runtime boundary requires a separate human-approved architecture decision.

Because this is a greenfield repository, plans must inspect the repository before naming project paths or commands. If a workstream has no real build surface, its first task should establish and verify one. Do not create downstream verification scripts against hypothetical projects.

Terminal-control feasibility, licensing, packaging, lifecycle cleanup, input behavior, and accessibility are evidence-bearing risks from the PRD. The planning workflow should expose those risks without turning this profile into a second implementation specification.

## Front-door prompts

Generate the two primary Pi prompts described by the orchestration documents:

| Prompt | gabCode specialization |
| --- | --- |
| `/pm-agent` | Reads the initial PRD and this profile, identifies the target workstream/platform, and creates a buildable plan using the repository-configured `github-issues` state backend |
| `/team-lead` | Executes an approved plan using the canonical quality gates and loads only the worker skills relevant to its workstream |

Do not create separate user-facing prompts for testing, building, destroying, reviewing, or committing. Those are internal worker phases. Add an optional utility prompt only after its distinct purpose is reviewed.

## Worker skill specialization

Use the canonical worker names from `TEAM-ORCHESTRATION.md` unless a later resource-map review approves a different topology.

| Skill | gabCode specialization |
| --- | --- |
| `product-designer` | Native desktop workflows, layout, focus, keyboard, accessibility, and platform differences |
| `pm` | Platform-aware increments or explicit shared-specification/fixture increments with real paths, commands, dependencies, and evidence |
| `domain-modeler` | Shared product vocabulary plus platform-owned project, worktree, status, association, preference, and authority concepts |
| `api-developer` | Explicitly approved typed boundaries or language-neutral artifact schemas; there is no default internal client/core API |
| `test-writer` | Repository-native tests across target-owned process, filesystem, Git, `gh`, watcher, PTY, accessibility, and shared logical fixture boundaries as applicable |
| `backend-builder` | Reserved guardrail for a future explicitly approved backend architecture; it owns no current gabCode production work |
| `frontend-builder` | Complete native-client work for the task's declared Windows or macOS platform; not a web frontend |
| `destroyer` | Adversarial checks focused on task-owned native lifecycle, Git safety, parity drift, and product-boundary risks |
| `review-agent` | Triage against the PRD, approved plan, platform evidence, and canonical review rules |
| `git-committer` | Canonical reviewed-commit and branch-size checkpoint behavior without project-specific expansion |

Preserve the canonical `frontend-builder` identity for both native platforms rather than creating `windows-builder` or `macos-builder` automatically. Do not create a `shared-core-builder`; shared specifications and fixtures are artifacts, not another production workstream.

Existing `brainstorm` and `inspire` skills are product-thinking utilities, not replacements for `/pm-agent` or `/team-lead`. Preserve them unless a separate review approves changes.

### Supporting capability skills

These skills provide gabCode-specific technical depth behind the canonical worker identities. They do not introduce new workflow phases or user-facing prompts.

| Skill | Capability |
| --- | --- |
| `gabcode-dotnet-inspect` | Evidence-based inspection of Windows/.NET, WPF, framework, and NuGet APIs without guessing signatures |
| `dotnet-concurrency-specialist` | Windows/.NET async coordination, races, cancellation, lifecycle, watchers, processes, and deterministic tests |
| `gabcode-native-accessibility` | Native keyboard, focus, Narrator, VoiceOver, terminal accessibility, contrast, scaling, and reduced-motion evidence |
| `gabcode-protocol-contracts` | Inactive architecture guard for proposed internal protocols; requires a new approved architecture decision before contract design |
| `gabcode-windows-desktop` | Complete Windows client: WPF, direct tools/data, Windows Terminal control, ConPTY, process trees, accessibility, and packaging |
| `gabcode-macos-desktop` | Complete macOS client: SwiftUI/AppKit, direct tools/data, SwiftTerm, Unix PTYs, process groups, accessibility, signing, and notarization |
| `gabcode-native-testing` | Native, process, PTY, filesystem, Git/`gh`, watcher, conformance-fixture, cleanup, and target-machine evidence strategy |

Workers load only the capabilities relevant to the approved task. Supporting skills provide expertise; canonical workers retain task ownership and reporting responsibility.

## Initial Pi resource map

This is the compact map to review before generating files:

```text
/pm-agent
  -> product-designer
  -> pm

/team-lead
  -> domain-modeler       when the approved plan requires domain/authority work
  -> api-developer        only for an explicitly approved typed boundary or artifact schema
  -> test-writer
  -> frontend-builder     for the declared Windows OR macOS production task
  -> backend-builder      only after a future approved backend architecture decision
  -> destroyer
  -> review-agent
  -> git-committer

supporting capabilities loaded by those workers as needed
  -> gabcode-dotnet-inspect and dotnet-concurrency-specialist for Windows/.NET work
  -> gabcode-native-accessibility
  -> gabcode-windows-desktop OR gabcode-macos-desktop
  -> gabcode-native-testing
  -> gabcode-protocol-contracts only as a guard when reviewing a proposed internal protocol
```

The generated prompts should coordinate this flow. The worker skills should remain narrow and point to the PRD, approved sprint record, and canonical orchestration rules instead of duplicating them.

## Proposed model assignments

Verify these model IDs with `pi --list-models` on each target machine before generating routing configuration.

| Resources | Provider/model | Thinking |
| --- | --- | --- |
| `/pm-agent`, `/team-lead` | `openai-codex/gpt-5.6-sol` | `max` |
| `product-designer`, `destroyer`, `review-agent` | `openai-codex/gpt-5.6-sol` | `max` |
| `pm`, `domain-modeler`, `api-developer`, `backend-builder`, `frontend-builder` | `openai-codex/gpt-5.6-sol` | `high` |
| `test-writer` | `openai-codex/gpt-5.6-terra` | `high` |
| `git-committer` | `openai-codex/gpt-5.6-luna` | `low` |
| `gabcode-dotnet-inspect`, `dotnet-concurrency-specialist` | `openai-codex/gpt-5.6-sol` | `high` |
| `gabcode-native-accessibility`, `gabcode-protocol-contracts` | `openai-codex/gpt-5.6-sol` | `high` |
| `gabcode-windows-desktop`, `gabcode-macos-desktop` | `openai-codex/gpt-5.6-sol` | `high` |
| `gabcode-native-testing` | `openai-codex/gpt-5.6-terra` | `high` |

Do not change the model assignments of existing `brainstorm` or `inspire` skills as part of orchestration setup unless they are included in the reviewed resource map.

## Resource-generation boundaries

When this profile is used to generate Pi resources:

- show the exact prompt, skill, and model map before creating files;
- preserve existing project skills and unrelated working-tree changes;
- keep `/pm-agent` and `/team-lead` as the cohesive user-facing workflows;
- use the state backend and quality gates defined by `TEAM-ORCHESTRATION.md` without restating them in every skill;
- use Pi paths and mechanics from `TOOL-PI.md`;
- reference the initial PRD for product details;
- do not invent source paths, build commands, issues, scripts, or mappings;
- route complete production behavior to one declared native platform and keep shared specifications/fixtures free of production runtime code;
- keep generated skills focused on role-specific deltas rather than generic personas or repeated project documentation.
