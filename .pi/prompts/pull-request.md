---
description: Create or update the current branch's pull request and post a full review
argument-hint: "[issue-or-pr-number]"
---

Create or update a pull request for the current Git branch, then review the committed PR in full.

Arguments supplied to this command: `$@`

## Ownership and authorization

This is a bounded PR publication/review utility, not a replacement for `/pm-agent` planning or `/team-lead` execution. Review workers remain internal phases; remediation belongs to `/team-lead`.

Invoking this command explicitly authorizes pushing the current committed branch HEAD, creating/updating its PR, posting a comment review, and recording actionable findings in related GitHub sprint issues. Do not ask again for approval of the title, publication, or handling a dirty working tree. This command does **not** authorize committing local changes, editing source/tests, merging, enabling auto-merge, closing PRs/issues, or applying final completion labels. Failed or unavailable checks do not prevent publication; they affect the review verdict, not permission to publish.

## Read first

1. `AGENTS.md`
2. `TEAM-ORCHESTRATION.md`, especially quality gates, issue reporting, and Pull Request Size Checkpoint
3. `TOOL-PI.md`
4. `Documentation/agents/gabcode-orchestration-context.md`
5. `.github/pull_request_template.md`
6. The available `gh` skill before GitHub CLI operations
7. The existing PR, related sprint issue bodies/comments, original acceptance criteria, applicable PRDs (including `Documentation/design/2026-07-24-gabcode-initial-prd.md` for product changes), and task-named files

Use `github-issues` for durable orchestration state. Compose PR/review/issue bodies under ignored `.pi/tmp/` and never commit them. Do not create a sprint issue merely to publish a documentation, release, or configuration PR without one.

When model routing is available, activate `pull-request` for coordination, activate each worker before adopting its role, and restore `pull-request` between phases and before the final report. Load capability skills only when relevant and restore the owning worker afterward.

## 1. Resolve the branch and target

- Run `git status --short` and show its output. If dirty, say that all staged, unstaged, and untracked changes will be left untouched and that only committed HEAD will be published/reviewed. Do not stage, commit, stash, reset, clean, or switch the user's working tree.
- Inspect the branch, HEAD SHA, remotes, upstream, and GitHub repository identity. Stop on detached HEAD, `main`/`master`, ambiguous repository/remote, missing authentication, or a missing PR template. Never push to mainline.
- Resolve an optional first argument as a GitHub issue or PR number in the confirmed repository, using metadata rather than treating every failed PR lookup as an issue. Reject invalid/unresolvable arguments instead of silently ignoring them.
- For a supplied PR: require an open PR whose head branch **and head repository** match the current branch and intended push destination. Stop on a closed/merged or mismatched PR; do not reinterpret it as an issue or modify another branch's PR.
- For a supplied issue: validate it, then search open PRs for the current head branch/repository. For no argument: perform the same search. Reuse the matching PR; create only when none exists. Stop if multiple matches make the target ambiguous.
- Use an existing PR's actual base branch. For a new PR, use the repository default branch unless an inspected repository convention clearly requires another. Fetch the relevant refs, inspect commits and the merge-base diff, and record base/head SHAs. Do not rebase or merge as a side effect of this utility. If synchronization is needed, hand it to `/team-lead` under the repository's rebase-only rules.
- Report the canonical `BELOW`/`ADVISORY`/`STRONG` checkpoint with branch/base, commit count, and unique changed-file count as defined in `TEAM-ORCHESTRATION.md`. A size checkpoint does not veto this authorized PR publication.

## 2. Publish committed HEAD and create/update the PR

- Inspect the committed scope for secrets/runtime artifacts before pushing. Stop for an actual safety problem, not unfinished quality gates.
- Ensure the committed HEAD is pushed to the verified feature branch on `origin`, setting upstream tracking when needed. Never use a blind force push. On a non-fast-forward rejection, inspect remote history; use `--force-with-lease` only for a known, authorized rebase without discarding remote-only work. Otherwise stop and report the divergence.
- Fill `.github/pull_request_template.md` from the actual committed changes and evidence. Retain applicable sections and remove only sections the template permits removing. Do not check unverified checklist items or platform boxes. Record missing checks as `NOT CHECKED` with reasons.
- Include a supplied issue reference and verified existing related work. Do not invent an issue/PRD reference when none is supplied or established by PR metadata/body. Use non-closing references such as `Related to #123`; do not add `Closes`, `Fixes`, or `Resolves` despite the template's suggestion. Do not silently rewrite human-authored acceptance decisions; flag existing automatic-closure directives for human disposition.
- Use a concise, accurate title without asking approval. Preserve an existing title unless it no longer describes the changes. Preserve useful human-authored body content, links, and decisions when refreshing template sections.
- Immediately before creation, search again for an open PR matching the head branch/repository. If one appeared, reuse it. If creation reports a duplicate, resolve and reuse that PR rather than retrying blindly.
- Create with `gh pr create` using the verified repository, head, base, and body file; update an existing PR with `gh pr edit` when needed. An unchanged existing PR still requires pushing the intended HEAD and the full review below. Do not change an existing PR's base or draft state without explicit direction.
- Confirm the PR's remote head SHA equals the intended committed HEAD before reviewing. If push/create/update fails, report the exact blocker and any partial result; never claim publication succeeded.

## 3. Review the PR URL in repository context

Review the published PR URL and its exact head SHA, not uncommitted local changes. Use `gh pr diff` as an input, never as the whole review.

- Read **every changed file** in surrounding context, including relevant callers, consumers, configuration, tests, packaging, and documentation. For generated/binary assets, inspect their provenance and consumers and state any inspection limits.
- Map behavior to original PR intent, linked task acceptance criteria, approved scope, and design decisions. Without a linked task, use the PR's stated intent and committed scope; do not invent an approved sprint.
- Inspect existing tests and discover the repository's actual verification commands before running them. Run the narrowest relevant tests/build/configuration checks and add validation evidence without modifying tracked source or tests. Ad hoc probes may live only in ignored temporary locations.
- If the working tree is dirty, use a separate clean snapshot/temporary checkout of the reviewed SHA for executable checks. Never attribute results from dirty files to the committed PR. If isolation or a required toolchain is unavailable, record `NOT CHECKED` and its impact; never alter the user's work to obtain a clean run.
- Validate native behavior on its target OS. Separate Windows and macOS results; one does not prove the other. Distinguish new checks from existing evidence, recording commands, exit codes/results, OS, and tested SHAs. Treat missing required evidence as blocking unless explicitly accepted by the human; do not fabricate an acceptance waiver.

Coordinate the existing workers within this read-only PR scope:

1. Establish relevant build/test evidence first. If it fails or required checks cannot run, record the gate failure; do not claim the destroyer passed. Continue contextual review and publish an honest `NO SHIP` report rather than abandoning review.
2. When build/planned-test prerequisites pass (or are explicitly not applicable for non-code scope), activate and read `.agents/skills/destroyer/SKILL.md`. Apply its adversarial checks to each task-owned portion of the PR, without editing tracked files. Load relevant native-testing, platform, concurrency, and accessibility capabilities. Review Git/worktree safety, filesystem authority, process/PTY lifecycle and cleanup, stale state/reconciliation, errors and resource bounds, packaging, keyboard/focus/accessibility, compatibility, shared-first dependencies, and the narrowly approved Monaco viewer boundary when applicable. Do not import unrelated web/backend assumptions.
3. Activate and read `.agents/skills/review-agent/SKILL.md`. Triage findings and evidence against original scope, including regressions, missing integration coverage, incomplete wiring, and unsupported platform/parity claims. If destroyer prerequisites failed, explicitly review that evidence gap rather than presenting a completed adversarial gate. Do not invoke builders or repair findings here.
4. Apply canonical severity/scope rules: critical/high findings are actionable; medium/low are non-blocking notes. Defer unrelated pre-existing defects unless they actively break this PR's guarantees. Give each finding a stable ID, severity, task/file/line, concrete impact, reproduction/evidence, required change, and expected verification. Preserve the worker's `SHIP IT`, `CHANGES NEEDED`, or `ESCALATE` verdict in the report.

## 4. Post review and sprint findings

As coordinator, publish the workers' reports; the review worker itself does not edit source, issue bodies, or labels.

- Post the **complete** review and validation evidence on the PR with `gh pr review <PR-URL> --comment --body-file <file>`. Use `🎉 SHIP IT! — Ready for Human Review` only when no blocking findings or unaccepted required-evidence gaps remain; otherwise use `🚫 NO SHIP`. Include reviewed base/head SHAs, scope, findings, non-blocking/deferred notes, exact check results, `NOT CHECKED` reasons, worker verdicts, and remaining human verification. This is a comment review, not a GitHub approval or human acceptance.
- Recheck the remote PR head before posting. If it changed, do not post a verdict as though it covers the new HEAD: review the new revision or stop and report the stale review. Associate the posted review with the reviewed commit and verify its returned `commit_id`/URL; disclose any concurrent-change mismatch.
- Identify related sprint/control issues from the supplied issue or verified PR metadata/body and read their existing task IDs/comments. For each actionable finding, add a remediation task in a comment using the canonical task-board format and required task fields from `TEAM-ORCHESTRATION.md`. Include finding ID/severity, task-owned file/line, assigned owner, required change, dependencies, acceptance criteria, and expected verification. Reuse an existing finding/task ID on repeated reviews instead of creating duplicate work. Put platform-specific findings in the owning child sprint when present and link the result from the parent as appropriate.
- Post applicable canonical `## 🔥 Destroy Report: ...`, `## 👀 Review Report: ...`, and `## 🧪 Test Report: ...` evidence in the related issue with the next actual round number. Explicitly mark skipped/failed gates. With no related issue, keep the evidence on the PR; do not invent one. Do not mark tasks complete, close issues, or apply final labels.
- If posting a review or issue comment fails, preserve the draft under `.pi/tmp/`, report what succeeded and what failed, and provide a retry action. Never silently replace the required posted review with a chat summary.

## Final report

Report branch, base, reviewed commit range/head SHA, canonical size checkpoint, PR title/URL, `SHIP IT` or `NO SHIP` verdict, blocking findings and evidence gaps, PR review comment URL, and any sprint-issue report/task comment URLs (or explicitly none). Passing tests/review is implementation evidence only, not human acceptance. Do not merge, close, remediate, or start another sprint task.
