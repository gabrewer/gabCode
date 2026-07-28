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
| Windows client | C# and WPF; native Windows UX and terminal hosting |
| macOS client | SwiftUI with AppKit where needed; native macOS UX and terminal hosting |
| Shared core | C# NativeAOT sidecar for Git/GitHub integration, normalized state, watchers, preferences, and associations |
| Client/core contract | Versioned JSON over standard input/output, not an HTTP API |
| External authorities | Installed `git`, authenticated `gh`, and the filesystem |
| Terminal model | Ordinary user-controlled shells; gabCode does not interpret Pi output or manage Pi sessions |
| Initial repository state | Product documentation exists; application build surfaces may still need to be established |

The initial PRD remains authoritative for detailed behavior, non-goals, technology choices, and milestone content. Generated skills should reference it rather than copying it wholesale.

## Important adaptations of the generic workflow

`TEAM-ORCHESTRATION.md` contains examples that commonly describe a web frontend, HTTP backend, browser tests, authentication, and persistence. For gabCode, interpret those concepts as follows:

| Generic term | gabCode meaning |
| --- | --- |
| Frontend | The target native Windows or macOS application |
| Backend | The shared NativeAOT sidecar when the task affects shared behavior |
| API contract | The typed client/sidecar JSON protocol and process lifecycle |
| Runtime validation | Running the native application on its target operating system |
| Integration test | A test across relevant process, filesystem, Git, `gh`, PTY, or native-hosting boundaries |
| Browser validation | Not applicable unless a future approved feature introduces a browser surface |
| Authentication/tenancy | Not assumed for the initial local, single-user product |

Do not introduce web frameworks, HTTP services, databases, Marten/event sourcing, Vitest, or Playwright merely because they appear in generic orchestration examples.

## Workstream boundaries

Planning should identify one owning workstream:

- **Windows client** — WPF, Windows Terminal control/ConPTY, native process and window behavior.
- **macOS client** — SwiftUI/AppKit, SwiftTerm/Unix PTY, native process and window behavior.
- **Shared core** — NativeAOT sidecar, protocol, Git/`gh`, watchers, and local metadata.

Windows and macOS implementation should be planned as separate platform increments and validated on their respective operating systems. Shared-contract work should be explicit rather than hidden inside a platform task.

Because this is a greenfield repository, plans must inspect the repository before naming project paths or commands. If a workstream has no real build surface, its first task should establish and verify one. Do not create downstream verification scripts against hypothetical projects.

Terminal-control feasibility, licensing, packaging, lifecycle cleanup, input behavior, and accessibility are evidence-bearing risks from the PRD. The planning workflow should expose those risks without turning this profile into a second implementation specification.

## Front-door prompts

Generate the two primary Pi prompts described by the orchestration documents:

| Prompt | gabCode specialization |
| --- | --- |
| `/pm-agent` | Reads the initial PRD and this profile, identifies the target workstream/platform, and creates a buildable plan using the user-selected state backend |
| `/team-lead` | Executes an approved plan using the canonical quality gates and loads only the worker skills relevant to its workstream |

Do not create separate user-facing prompts for testing, building, destroying, reviewing, or committing. Those are internal worker phases. Add an optional utility prompt only after its distinct purpose is reviewed.

## Worker skill specialization

Use the canonical worker names from `TEAM-ORCHESTRATION.md` unless a later resource-map review approves a different topology.

| Skill | gabCode specialization |
| --- | --- |
| `product-designer` | Native desktop workflows, layout, focus, keyboard, accessibility, and platform differences |
| `pm` | Platform-aware increments with real paths, commands, dependencies, and target-machine evidence |
| `domain-modeler` | Project, worktree, status, association, preference, and authority concepts when the sprint changes them |
| `api-developer` | Typed, versioned, NativeAOT-compatible standard-input/output protocol when a client/core boundary changes |
| `test-writer` | Repository-native tests across native, process, filesystem, Git, `gh`, protocol, and PTY boundaries as applicable |
| `backend-builder` | Shared NativeAOT sidecar work; not a web server by default |
| `frontend-builder` | Native client work for the task's declared target platform; not a web frontend |
| `destroyer` | Adversarial checks focused on task-owned native lifecycle, Git safety, protocol, and product-boundary risks |
| `review-agent` | Triage against the PRD, approved plan, platform evidence, and canonical review rules |
| `git-committer` | Canonical reviewed-commit and branch-size checkpoint behavior without project-specific expansion |

A platform may eventually warrant a dedicated builder skill, but do not create `windows-builder`, `macos-builder`, or `shared-core-builder` automatically. First determine whether focused references inside the canonical builder skills are sufficient.

Existing `brainstorm` and `inspire` skills are product-thinking utilities, not replacements for `/pm-agent` or `/team-lead`. Preserve them unless a separate review approves changes.

### Supporting capability skills

These skills provide gabCode-specific technical depth behind the canonical worker identities. They do not introduce new workflow phases or user-facing prompts.

| Skill | Capability |
| --- | --- |
| `gabcode-dotnet-inspect` | Evidence-based inspection of .NET, WPF, framework, and NuGet APIs without guessing signatures |
| `dotnet-concurrency-specialist` | Async coordination, races, cancellation, lifecycle, watchers, processes, and deterministic concurrency tests |
| `gabcode-native-accessibility` | Native keyboard, focus, Narrator, VoiceOver, terminal accessibility, contrast, scaling, and reduced-motion evidence |
| `gabcode-protocol-contracts` | NativeAOT-safe source-generated JSON-over-stdio framing, compatibility, lifecycle, and tests |
| `gabcode-windows-desktop` | WPF, Windows Terminal control, ConPTY, process trees, native input, accessibility, and packaging |
| `gabcode-macos-desktop` | SwiftUI/AppKit, SwiftTerm, Unix PTYs, process groups, native input, accessibility, signing, and notarization |
| `gabcode-native-testing` | Native, process, PTY, filesystem, Git/`gh`, protocol, cleanup, and target-machine evidence strategy |

Workers load only the capabilities relevant to the approved task. Supporting skills provide expertise; canonical workers retain task ownership and reporting responsibility.

## Initial Pi resource map

This is the compact map to review before generating files:

```text
/pm-agent
  -> product-designer
  -> pm

/team-lead
  -> domain-modeler       when the approved plan requires domain work
  -> api-developer        when the approved plan changes a typed boundary
  -> test-writer
  -> backend-builder and/or frontend-builder
  -> destroyer
  -> review-agent
  -> git-committer

supporting capabilities loaded by those workers as needed
  -> gabcode-dotnet-inspect
  -> dotnet-concurrency-specialist
  -> gabcode-native-accessibility
  -> gabcode-protocol-contracts
  -> gabcode-windows-desktop OR gabcode-macos-desktop
  -> gabcode-native-testing
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
- keep generated skills focused on role-specific deltas rather than generic personas or repeated project documentation.
