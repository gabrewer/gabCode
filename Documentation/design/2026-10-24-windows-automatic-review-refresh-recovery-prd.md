# Windows Periodic Review Refresh

| Field | Value |
| --- | --- |
| Status | [Sprint #131](https://github.com/gabrewer/gabCode/issues/131): periodic-only revision implemented and automated checks passed; human retest pending |
| Target | Windows C#/WPF Changes and commits sidebar |

## Problem

An agent may edit many files continuously. The user needs to inspect what the agent has done through Git status, not watch each write instantly. Watcher-triggered reads can repeatedly catch up, then pause automatic refresh after sustained changes even when Git reads succeed. This was confirmed by the human retest of the first optimization.

## Required behavior

- Refresh working changes from Git status. Read the selected worktree's HEAD and configured local-main commit IDs; reload branch-only commits and statistics **only when those IDs change** or there is no complete cached result for that worktree and comparison branch. Otherwise reuse the last complete commit list. Never present commits from another worktree or outdated commit IDs as current. Git remains authoritative; the cache is in memory only.
- For the selected worktree's review sidebar, refresh once on selection and then every 12 seconds using read-only Git. Do not watch filesystem events or immediately catch up after each write. If a periodic tick arrives during a read, skip it; the next tick can read the latest state. No overlapping reads or sustained-change safety pause solely because edits continue. Other app watchers remain outside this change.
- Preserve bounded Git reads, output, cancellation, owned-child cleanup, eight-second aggregate limit, and window/terminal responsiveness. An actual budget/unsafe-cleanup stop still shows the existing paused state and manual Retry; explicit user Cancel still pauses. Do not increase timeouts or add automatic retry/backoff.
- Do not change worktree-switching, terminal input, focus, diff activation, Git comparison semantics, or Git files/refs.

## Verification

Test that first selection reads Git, periodic ticks eventually show working changes and HEAD/local-main updates, repeated ticks during a slow read do not queue or overlap work, and switching/closing discards obsolete reads. Existing cache, budget stop, Cancel/Retry, and read-only Git tests remain valid. Do not inject keystrokes into the user's desktop or alter the running app. Human terminal-input/Narrator validation remains **NOT CHECKED** until performed.

This preserves the safeguards of [Windows Git Tree Refresh Safety](2026-10-15-windows-git-tree-refresh-safety-prd.md). The [focus preservation PRD](2026-10-24-windows-refresh-focus-preservation-prd.md) is separate.
