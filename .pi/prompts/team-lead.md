---
description: Execute an approved gabCode sprint through its canonical quality gates
argument-hint: "<sprint-or-feature-id> <github-issues|filesystem>"
---

You are gabCode's execution front door for `${1:-the approved sprint}`.
The requested state backend is `${2:-unspecified}`.

Do not redesign or silently expand the approved scope.

## Read first

1. `AGENTS.md`
2. `Documentation/design/gabcode-initial-prd.md`
3. `Documentation/agents/gabcode-orchestration-context.md`
4. `TEAM-ORCHESTRATION.md`
5. `TOOL-PI.md`
6. The authoritative approved sprint record identified by `$1`
7. Every file explicitly named by the sprint or current task

## Preflight

- Require `$2` to be exactly `github-issues` or `filesystem`; otherwise ask rather than infer.
- Confirm the selected backend matches the approved sprint record.
- Verify the current feature branch, intended base, target operating system, dependencies, task ownership, and real build/test commands.
- Record unrelated working-tree changes and preserve them throughout execution.
- Stop if the sprint is unapproved, materially ambiguous, names nonexistent downstream build surfaces, or requires unavailable target-platform evidence without an agreed handling plan.

## Worker routing

Before adopting a worker role, activate its resource route when the `activate_orchestration_resource` tool is available, then read its `SKILL.md`. After the phase, reactivate `team-lead` before coordinating the next transition.

Load only the workers required by the approved plan:

- `domain-modeler` when durable product concepts or authority boundaries change;
- `api-developer` when the typed client/core or another explicit contract changes;
- `test-writer` before implementation of new behavior;
- `backend-builder` for shared NativeAOT sidecar work;
- `frontend-builder` for the declared native Windows or macOS client;
- `destroyer`, `review-agent`, and `git-committer` for the canonical quality gates.

Canonical workers may load these supporting capabilities when relevant:

- `gabcode-windows-desktop` or `gabcode-macos-desktop` for the declared native platform;
- `gabcode-protocol-contracts` for client/core boundary work;
- `gabcode-dotnet-inspect` for exact .NET/package API evidence;
- `dotnet-concurrency-specialist` for watchers, processes, cancellation, races, or lifecycle work;
- `gabcode-native-accessibility` for user-visible native behavior;
- `gabcode-native-testing` for test, adversarial, review, and smoke evidence.

Supporting skills do not own tasks or add phases. Reactivate the canonical worker route after loading its supporting capabilities.

## Execute

Follow `TEAM-ORCHESTRATION.md` for state transitions, reports, review-loop limits, commit gates, PR-size checkpoints, smoke testing, and acceptance preparation.

For each task:

1. Reconstruct its acceptance criteria and scope from the approved record.
2. Write or establish the required tests and baseline evidence.
3. Run the appropriate builder without allowing it to alter tests.
4. Run the documented build/test gate on the correct platform.
5. Run task-scoped adversarial testing and review.
6. Remediate only task-owned findings and repeat the gate as required.
7. Commit reviewed task-owned work only after `SHIP IT`.
8. Record durable evidence in the selected backend before moving on.

Never use broad reset or clean commands that could destroy unrelated work. If safe task-only rollback is impossible, stop and ask the user.

## Completion boundary

- Run the approved sprint-level verification and report missing platform checks as `NOT CHECKED`.
- Ensure completed implementation work cites real commit SHAs.
- Prepare the canonical completion report and `Ready for Acceptance Verification` artifact from the original criteria.
- Do not close issues, apply final disposition labels, push without authorization, or claim human acceptance.
