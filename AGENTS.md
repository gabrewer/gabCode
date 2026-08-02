# gabCode Project Instructions

## Authoritative context

- `Documentation/design/gabcode-initial-prd.md` owns product scope and architecture.
- `TEAM-ORCHESTRATION.md` owns planning, execution, state tracking, and quality gates.
- `TOOL-PI.md` owns Pi resource mechanics.
- `Documentation/agents/gabcode-orchestration-context.md` adapts the generic roles to gabCode.

Read the approved sprint record and the task-named files before changing code.

## Workflow routing

- Route product discovery, feature shaping, and planning through `/pm-agent`.
- Route execution of an approved sprint through `/team-lead`.
- Do not collapse, imitate, or bypass those front doors from the default session.
- Testing, building, adversarial review, review, and committing are internal worker phases rather than separate user-facing workflows.

## Product boundaries

- gabCode is a native Windows and macOS desktop application, not a web application.
- The Windows client is a complete C#/WPF application and owns Windows UI, terminal hosting, direct Git/`gh` integration, normalized state, watchers/reconciliation, preferences, and associations.
- The macOS client is a complete Swift/SwiftUI/AppKit application and owns the equivalent macOS behavior with native platform facilities.
- Windows and macOS share requirements, vocabulary, language-neutral fixtures, and expected outcomes—not production runtime code, a companion sidecar, or an internal client/core protocol.
- Plan and validate Windows and macOS implementation as separate target-platform increments; one platform's evidence does not prove the other.
- Git, the filesystem, and read-only `gh` queries remain authoritative. Do not turn local metadata into a competing source of truth.
- gabCode observes source, PRDs, issues, commits, and diffs. It does not edit them or interpret/manage Pi sessions.

## Working rules

- Inspect the repository before naming source paths or build, test, launch, or verification commands.
- If a workstream has no build surface, establish one before creating downstream scripts that depend on it.
- Validate platform behavior on its target operating system and report unavailable checks as `NOT CHECKED`.
- Do not introduce web frameworks, HTTP services, databases, Marten/event sourcing, Vitest, or Playwright from generic examples unless an approved design requires them.
- Use supported package-manager commands rather than manually editing generated dependency state.
- Preserve unrelated working-tree changes. Never use broad cleanup or reset commands to discard work you do not own.

## State, Git, and acceptance

- This repository's orchestration state backend is always `github-issues`.
- Planning and execution must use `github-issues` without prompting for a backend.
- Do not use filesystem-backed orchestration state unless a human explicitly changes this repository policy.
- Keep `.pi/tmp/` drafts and tool runtime state untracked.
- Never close GitHub issues or apply final completion labels; prepare evidence for human disposition.
- Never push directly to `main` or `master`. Use a feature branch and follow repository rebase-only synchronization rules.
- Passing tests and commits are implementation evidence, not human acceptance.
