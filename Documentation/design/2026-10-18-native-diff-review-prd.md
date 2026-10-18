# gabCode Read-Only Diff Review PRD

| Field | Value |
| --- | --- |
| Status | Product decision recorded; implementation not yet planned or accepted |
| Date | 2026-10-18 |
| Related baseline | `Documentation/design/2026-07-24-gabcode-initial-prd.md` |
| Related review workflow | `Documentation/design/2026-09-20-worktree-change-review-sidebar-prd.md` |

## Product Name & One-Liner

**gabCode Read-Only Diff Review** — a readable, read-only code review surface hosted inside the native Windows and macOS applications, replacing raw text patches with side-by-side, inline, and full-file views.

## Problem & Audience

A developer reviewing worktree changes currently sees a raw text diff. Patch notation is difficult to scan and does not match the code-centered review experience developers expect. The individual gabCode developer needs to understand changes in context without leaving the app or giving up the ability to inspect the entire resulting file.

## Core Features

1. **Side-by-side review — Must-have.** Show old and resulting code in aligned panes with source-side line numbers, synchronized vertical navigation, and clear addition, deletion, and modification highlighting.
2. **Inline review — Must-have.** Show one code view with additions and deletions clearly distinguished and line numbers that do not misrepresent old lines as new ones.
3. **Full resulting file — Must-have.** Show the entire resulting file in a read-only Monaco editor with subtle markers from the same comparison as the diff modes, in or near the gutter. Added and modified regions must be discoverable; deletion locations must be discoverable, including deletions at file boundaries. Validate marker placement and keyboard access in both webview hosts. Prefer an accessible read-only preview of removed lines; if that cannot be delivered reliably, provide a keyboard-accessible jump to the corresponding deletion in Inline mode. Do not ship a color-only or inaccessible indication.
4. **Consistent diff replacement — Must-have.** Replace raw text patches for uncommitted changes, individual commit-file diffs, and branch/cumulative file comparisons on both platforms. Preserve the selected comparison basis in a visible and accessible label: staged is `HEAD → index`, unstaged is `index → worktree`, untracked is `empty → worktree`, commit is first parent (or empty tree for a root commit) → selected commit, and branch comparison shows its actual Git base and target. Do not substitute the current worktree file for historical content.
5. **Change navigation and view state — Must-have.** Provide previous/next-change actions in all modes, preserve the selected change or nearest surviving line across mode switches and refreshes, and keep keyboard focus stable. Default to Side by side, remember the user's selected mode as local presentation preference, and never switch modes automatically merely because a window becomes narrow; allow horizontal scrolling or an explicit mode change instead.
6. **Code-centered readability — Must-have.** Use Monaco syntax highlighting where supported in all three modes, line numbers, selectable/copyable code without patch prefixes or alignment padding, and a readable plain-text fallback for unsupported languages. Highlight changed spans within modified lines. Diff modes collapse long unchanged regions with a keyboard-accessible way to expand them; File mode shows the full result within documented resource limits.
7. **Read-only review in a native app — Must-have.** Configure every Monaco model/view read-only, with no source-file save path or Git mutation action exposed to the viewer. Typing, paste, drop, replace, formatting and diff-revert commands cannot alter even the displayed snapshot. Selection, copy and Find remain usable. Keep navigation, menus, terminals, Git queries and window ownership native. Provide discoverable keyboard traversal into and out of the viewer, preserve native window/menu shortcuts, avoid intercepting terminal shortcuts outside it, restore focus on close, and expose accessible change descriptions through the embedded host. Keep a prominent native **Open in VS Code** action only where the baseline permits it.
8. **Scoped shared Monaco viewer — Must-have.** Bundle one pinned, shared HTML/CSS/JavaScript Monaco viewer implementation and language assets with each platform app; host only the code/diff document region in WebView2 on Windows and WKWebView on macOS. The shared viewer owns mode presentation, highlighting, markers, change navigation and position preservation; each platform independently owns Git/filesystem retrieval, native host integration, origin/message validation, lifecycle, packaging and accessibility evidence. Reuse Monaco's diff editor for Side by side and Inline, and its ordinary editor for File mode. No CDN, remote page, local HTTP server, general browsing or shared process/service. Validate the exact Monaco version and both hosts before rollout.

### Comparison and exceptional states

- File mode displays the index version for staged changes, working-tree version for unstaged and untracked changes, the selected commit's version for commit review, and the target version for branch comparisons. A deleted target displays an explicit deleted-file state; old content remains available in Side by side and Inline modes. Never silently display today's file as the historical result.
- Renames identify both paths while comparing the same selected before/after versions. Conflicted files retain a distinct read-only conflict presentation rather than pretending to be an ordinary two-way diff.
- Binary content has an explicit non-text presentation. A genuinely empty text file must not be mistaken for a load failure. If a text comparison is too large or cannot be displayed, use the same native **Diff unavailable** state described below.
- Git/filesystem authority determines the before/after versions; Monaco computes their visual differences and may group changes differently from a Git patch. Each review selection captures one coherent pair of exact before/after contents. Both diff modes and File mode use that same snapshot; File-mode markers use the same computed comparison as the diff modes, not a separately grouped Git patch. Do not silently ignore whitespace, line endings or final-newline differences; if Monaco cannot visibly represent a difference, report it explicitly instead of claiming no changes. A refresh swaps the pair and its identity atomically, never just one side.
- On refresh, keep the last successfully loaded view visible while updating when safe, announce loading/errors without stealing focus, and apply results only to the still-selected worktree, file, comparison, and request generation. If content changes, preserve the nearest meaningful review position; never show a stale comparison as current without marking it.
- If the viewer cannot display a comparison, show one native **Diff unavailable** state with the reason, **Retry**, and **Open in VS Code** where a current file exists. Never silently show an incorrect or raw-patch substitute, or affect running terminals. A missing WebView2 runtime gets a clear prerequisite message within that state; no separate recovery workflow is required for each possible failure.

## Non-Goals

- Editing or saving files, staging, discarding, resolving conflicts, or mutating Git state. Lightweight source editing would require a separate product and safety decision, not merely switching Monaco out of read-only mode.
- Replacing gabCode's file explorer or review-sidebar navigation.
- A general browser-based application, web-based navigation/terminal/Git UI, remote assets, or a shared Windows/macOS service or core. Only the bundled Monaco viewer is shared production code.
- Reimplementing a full IDE; this is a focused read-only reviewer.
- Pixel-identical platform UI; behavior and review outcomes should be equivalent while respecting native conventions.

## Technical Considerations

### Hosting and ownership

Windows remains a complete C#/WPF client hosting the viewer with WebView2; macOS remains a complete Swift/SwiftUI/AppKit client hosting it with WKWebView. The **same packaged viewer source and pinned Monaco (MIT) assets** run in both applications. This is the human-approved, narrowly scoped exception to independent native production-code ownership: there is no shared native core, service, process, database, Git adapter or terminal implementation. Each platform packages its own copy, owns native integration and validates on its target OS. The viewer is presentation only: platform-owned code obtains bounded, exact Git/filesystem snapshots, never worktree authority from Monaco. Git and the filesystem remain authoritative. Record asset provenance and licenses and use supported package-manager installation/lockfile generation. The earlier Syncfusion/DiffPlex native-viewer proposal is superseded for this feature.

### Local assets and trust boundary

Bundle pinned Monaco and viewer assets offline under a controlled packaged-app origin, including workers and language assets. No CDN, local HTTP service, remote page or arbitrary navigation. Treat all repository content, paths and commit metadata as untrusted text, not executable HTML/script or privileged URIs. The shared viewer defines a minimal documented message schema for snapshots, mode, change position and status; each native host validates origin, type, size, identity and request generation independently, and allows only necessary viewer-to-host events. The viewer never receives arbitrary native file/Git access or save/execute commands. Harden asset loading and content security policy; reject unexpected messages. This viewer/host seam is not a client/core protocol or general-purpose extension bridge.

### Lifecycle, performance and accessibility

Set measured size/output and response limits on target machines; use the single **Diff unavailable** state above instead of unbounded Git output, diff computation or UI-thread work. Dispose viewer resources on selection/close; viewer failure must not touch retained terminals. Validate offline packaged asset and worker loading on Windows and macOS and check the missing-WebView2-runtime message; do not build distinct recovery subsystems for speculative failure types. Validate keyboard-only traversal and native shortcuts, focus restoration, high contrast/scaling, Monaco accessibility mode, Narrator through WebView2 and VoiceOver through WKWebView; neither platform's evidence substitutes for the other.

## Milestones

1. **Shared-first architecture, viewer and feasibility** — Reconcile the authoritative native-client architecture and guidance through review, then define and package the shared Monaco viewer and language-neutral comparison fixtures together as a shared prerequisite. Prototype offline, read-only hosting on both target OSes with real content and keyboard/screen-reader checks; record license/asset provenance, packaging, bridge/origin safety, deleted-marker interaction, file-size/output limits and responsiveness criteria. Cover staged/unstaged/untracked, commits and branch comparisons, whitespace/final-newline differences, additions, boundary deletions, replacements, renames, empty files, conflicts, line endings, binary/oversized files and File-mode markers. Land shared prerequisites through one reviewed mainline PR before dependent platform increments.
2. **Windows hosted reviewer** — Replace raw patches across working changes, commits and branch comparisons with the shared bundled viewer in WebView2, platform-owned snapshot retrieval/validation, three modes, read-only and recovery states, automated coverage and Windows target-machine accessibility evidence.
3. **macOS hosted reviewer** — Implement equivalent behavior with the same shared viewer in WKWebView and a macOS-owned adapter, with macOS target-machine accessibility evidence.
4. **Cross-platform parity and resilience** — Exercise equivalent Git fixtures and review contexts on both systems; compare mode behavior, change mapping, navigation, deleted/unavailable states, position retention, keyboard access, and screen-reader semantics. Document intentional differences and remaining gaps.

## Acceptance Criteria

- [ ] On each target OS, working changes (staged, unstaged and untracked), individual commit files and branch/cumulative comparisons have reachable file-selection paths opening the correct Git comparison and resulting version in all three modes. If an entry point is missing today, planning must include supplying it. A missing entry point remains an unmet acceptance criterion, not an implicitly excluded context. Raw patches are no longer the normal review presentation.
- [ ] A historical commit result never displays the current worktree version. Root/merge comparison labels, rename paths, and deletion states accurately identify the basis and content.
- [ ] Side by side and Inline modes present additions/deletions, changed spans within modified lines and expandable unchanged context; File mode shows the full result within the documented size limit and makes added, modified and deleted locations discoverable without color-only meaning. All modes agree on one before/after snapshot; whitespace, line-ending and missing-final-newline differences are not silently hidden.
- [ ] Mode choice persists locally; switching modes and refreshing preserve meaningful review position and focus. Previous/next-change actions work by keyboard, including for deletion markers or their explicit Inline fallback.
- [ ] Keyboard-only users can discover how to enter and leave Monaco, use Find and change navigation, and return to the invoking control when the document closes. Native window/menu shortcuts remain usable; viewer shortcuts do not intercept terminal input when focus is outside the viewer.
- [ ] A user can select/copy code without patch prefixes and use Find. Supported syntax is highlighted in all modes; unsupported languages fall back to readable text. Monaco cannot be used to edit even its displayed snapshots through keyboard, paste/drop, replace, formatting or diff-revert actions; no save or Git mutation is exposed.
- [ ] Conflicts, binary content and empty text are identified correctly. An oversized or unavailable comparison shows the single native **Diff unavailable** state with reason, Retry and Open in VS Code when applicable. Failed refreshes remain visibly stale rather than claiming current data. Rapid file/worktree switching never applies an obsolete result; no review action mutates Git or source.
- [ ] Separate Windows and macOS evidence covers native build/tests, shared-viewer asset identity, offline page/worker/language loading, malicious repository text, unexpected message/origin rejection, representative **Diff unavailable** and Retry behavior, keyboard traversal and native shortcuts, Narrator/UI Automation or VoiceOver, high contrast/scaling, and recorded intentional differences. Viewer failure or close does not affect running terminals. Checks unavailable on either platform are marked `NOT CHECKED`, not inferred from the other platform.

## Open Questions

- Which pinned Monaco version and packaging method work safely offline on both platforms? Resolve with target-OS prototypes and reviewed dependency/asset provenance.
- Which deletion marker/preview interaction is feasible in File mode? Prefer an accessible read-only preview; use an accessible Inline-mode jump if Monaco/host cannot support a reliable preview. Record the selected interaction on both platforms.
- What supported languages, maximum file size, and responsiveness criteria can the chosen Monaco build and webview hosts deliver? Document explicit fallbacks during feasibility.
- The future lightweight-editing workflow is deferred to its own product decision and safety design; this increment does not authorize source editing.

## Approval Boundary

This PRD records the human's decision to share the read-only Monaco viewer bundle as a **limited architecture exception**. The coordinated updates to the initial/independent-native-client architecture and project guidance are proposals until reviewed and merged; do not treat an unmerged PRD as overriding mainline. `/pm-agent` should plan one shared-first reviewed PR for architecture, shared viewer and fixtures before dependent Windows and macOS increments; each needs its own target-platform evidence. Passing tests and commits are implementation evidence, not human acceptance.
