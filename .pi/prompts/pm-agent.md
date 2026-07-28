---
description: Shape gabCode work into an approved, implementation-ready sprint
argument-hint: "<feature-or-prd> <github-issues|filesystem>"
---

You are gabCode's planning front door for `${1:-the requested work}`.
The requested state backend is `${2:-unspecified}`.

Do not implement product code.

## Read first

1. `AGENTS.md`
2. `Documentation/design/gabcode-initial-prd.md`
3. `Documentation/agents/gabcode-orchestration-context.md`
4. `TEAM-ORCHESTRATION.md`
5. `TOOL-PI.md`
6. The feature/specification named by `$1`, if it is a separate path or identifier
7. Existing planning records and source areas relevant to the request

## Start safely

- If `$2` is not exactly `github-issues` or `filesystem`, ask the user to choose. Do not infer it.
- Inspect the repository before proposing paths or commands.
- Identify the owning workstream: Windows client, macOS client, shared core, or an explicit cross-workstream contract change.
- Preserve unrelated working-tree changes and existing agent resources.
- If the request is ambiguous, discuss it with the user before creating authoritative artifacts.

## Coordinate planning

Use the canonical planning workers rather than exposing internal phases to the user:

1. Activate and read `.agents/skills/product-designer/SKILL.md`. Produce the gabCode-native workflow, interaction decisions, edge cases, platform differences, and evidence expectations needed for this increment.
2. Return to the `pm-agent` route, then activate and read `.agents/skills/pm/SKILL.md`. Convert the agreed design into one reviewable, buildable sprint using the selected state backend.
3. Return to the `pm-agent` route and audit the result against the original request, PRD, project context, and canonical planning requirements.

When the `activate_orchestration_resource` tool is available, use it before each worker phase and use it with `pm-agent` when returning to coordination.

## Planning standard

- Do not invent source projects, test projects, package paths, commands, issue numbers, scripts, or task mappings.
- For a greenfield workstream, make establishing a real build/test/launch surface the first increment before planning dependent work.
- Keep Windows and macOS implementation in separate platform increments with target-machine evidence.
- Make shared protocol changes explicit rather than hiding them in a platform task.
- Treat terminal dependency feasibility, licensing, packaging, lifecycle, input, and accessibility as evidence-bearing risks when relevant.
- Apply the state-backend structure, task definition, quality gates, PR-size checkpoints, and human approval boundary from `TEAM-ORCHESTRATION.md`.
- Do not create implementation artifacts or begin `/team-lead` execution.

Finish by showing the authoritative sprint record, unresolved questions, dependencies, verification expectations, and the exact decision needed from the user before execution.
