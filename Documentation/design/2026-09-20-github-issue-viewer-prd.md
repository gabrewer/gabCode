# gabCode — Live GitHub Issue Viewer PRD

| Field | Value |
|---|---|
| Date | 2026-09-20 |
| Status | Product design; implementation requires `/pm-agent` planning |
| Foundation | `Documentation/design/2026-09-20-worktree-references-prd.md` — required |
| Dependency | The associated-reference flow must exist before this feature can open an issue tab |

## Product Name & One-Liner

**Live GitHub Issue Viewer** lets a developer watch the selected sprint issue’s full execution comments inside gabCode while an LLM builds the work.

## Problem & Audience

A developer supervising an LLM-driven sprint needs to read the issue’s task board and every progress, test, review, and blocker comment without repeatedly switching to GitHub in a browser.

## Core Features

1. **Explicit issue tab — must-have.** The user selects the associated issue from the references bar or worktree context menu; association alone never opens or monitors it.
2. **Overview — must-have.** Show title, number, repository/URL, state, labels, body, and updated time as read-only rendered Markdown.
3. **Activity stream — must-have.** Show every available issue comment with author, timestamp, and full rendered Markdown, initially focused on the newest updates. Newest comments appear first; older history is loaded as the user scrolls.
4. **Near-real-time refresh — must-have.** Poll only while the issue tab is selected/open, using a bounded interval such as 15 seconds, with manual refresh and visible last-updated state. Stop polling when the tab closes, the user leaves the issue view, gabCode loses focus, the app is minimized, or connectivity is unavailable.
5. **Safe new-comment behavior — must-have.** Follow new comments automatically only when the user is already at the bottom; otherwise preserve reading position and show a New comments action/count.
6. **Browser fallback — must-have.** Open the issue in the default browser at any time; read-only rendering remains useful when `gh` is unavailable or authentication fails.

## Non-Goals

- No issue creation, editing, commenting, closing, labeling, or other GitHub writes.
- No background polling, desktop notifications, automatic issue opening, or interpretation of LLM output.
- No embedded GitHub web page or hosted webhook service in v1.
- No script execution or unsafe embedded content from issue Markdown.
- No collapsing or summarizing comments in the initial execution view.

## Technical Considerations

Use the existing read-only `gh`/GitHub query boundary and parse issue metadata, body, and comments into native view models. Poll only on active view lifetime, handle rate limits/network errors with backoff and a visible stale/error state, and avoid duplicate comments across refreshes. Use stable comment identifiers and timestamps to append or update content. Windows and macOS must implement native scrolling, tab, focus, and accessibility behavior independently.

## Native UX and Evidence

The main area hosts the issue tab while retained terminals remain below. Overview and Activity navigation must be keyboard reachable; comments need meaningful accessible headings, author/time metadata, and announced new-content state without unexpectedly moving focus. Target evidence must cover initial load, empty comments, long Markdown, polling, new comments while at bottom and while reading, offline/rate-limit recovery, browser opening, screen readers, scaling, and high contrast on both platforms.

## Milestones

1. Establish read-only issue URL parsing and issue/comment query models.
2. Add explicit main-area issue tab with Overview and Activity rendering.
3. Add active-view polling, deduplication, errors, and new-comment behavior.
4. Add browser fallback and target-platform accessibility treatment.
5. Validate live observation and parity on Windows and macOS.

## Open Questions

- Exact GitHub query fields, pagination strategy, and rate-limit thresholds must be confirmed against the supported `gh` surface during planning.
- The exact stale/offline presentation and retry timing must be established during platform planning.
