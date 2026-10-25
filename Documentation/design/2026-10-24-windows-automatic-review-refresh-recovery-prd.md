# Windows Review Refresh Efficiency

| Field | Value |
| --- | --- |
| Status | Implemented in [sprint #131](https://github.com/gabrewer/gabCode/issues/131); automated checks passed; human native acceptance pending |
| Target | Windows C#/WPF Changes and commits sidebar |

## Problem

Every review refresh runs Git status **and** reloads commit history with statistics, even when only a working file changed. A complete read has an eight-second budget; repeated overruns or sustained invalidations can leave the tree paused until someone clicks Retry.

## Required behavior

- Refresh working changes from Git status. Read the selected worktree's HEAD and configured local-main commit IDs; reload branch-only commits and statistics **only when those IDs change** or there is no complete cached result for that worktree and comparison branch. Otherwise reuse the last complete commit list. Never present commits from another worktree or outdated commit IDs as current. Git remains authoritative; the cache is in memory only.
- Coalesce frequent file-change notifications. Preserve bounded Git reads, output, cancellation, child-process cleanup, and window/terminal responsiveness. Keep the existing eight-second snapshot safety limit; do not increase it as a substitute for removing unnecessary work.
- Keep existing timeout, Retry, Cancel, and unsafe-cleanup behavior unchanged. This PRD removes redundant work; it does not introduce automatic retry or promise recovery if a read still exceeds the limit.
- Do not change worktree-switching, terminal input, focus, diff activation, Git comparison semantics, or Git files/refs.

## Verification

Test that file-only changes refresh status without rerunning commit history; HEAD or local-main changes and first reads reload it; failed or cancelled reads never populate the cache. Check that timeouts and cancellation keep their existing behavior. Validate terminal input on Windows with disposable repositories; native validation is **NOT CHECKED**.

This preserves the safeguards of [Windows Git Tree Refresh Safety](2026-10-15-windows-git-tree-refresh-safety-prd.md). The [focus preservation PRD](2026-10-24-windows-refresh-focus-preservation-prd.md) is separate.
