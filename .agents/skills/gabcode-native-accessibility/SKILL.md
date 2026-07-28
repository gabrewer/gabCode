---
name: gabcode-native-accessibility
description: Defines and reviews native accessibility for gabCode on Windows and macOS. Use for product design, implementation, testing, destroyer, and review work involving keyboard/focus behavior, Narrator, VoiceOver, terminal views, high contrast, reduced motion, or dynamic status updates.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode Native Accessibility

Accessibility is part of product design, implementation, adversarial testing, review, and target-machine acceptance evidence. It is not a final polish phase.

## Cross-platform expectations

For every user-visible workflow, establish:

- keyboard-only completion with documented shortcuts and no pointer-only path;
- logical focus order, visible focus, and predictable focus restoration;
- meaningful accessible names, roles, states, values, and relationships;
- non-visual announcement of loading, errors, dirty state, branch/status changes, and terminal lifecycle changes;
- alternatives to color-only, icon-only, hover-only, drag-only, or spatial-only meaning;
- readable scaling and usable high-contrast/reduced-motion behavior;
- no focus loss when worktrees, panes, dialogs, or terminal regions change;
- a usable terminal experience without gabCode intercepting ordinary shell input unexpectedly.

## Windows evidence

Use WPF's native automation/accessibility surface and validate with keyboard navigation, Windows accessibility settings, and Narrator or another approved UI Automation client. Check hosted terminal-control integration, tab order, access keys, focus scopes, menus/dialogs, high contrast, scaling, and announcements.

## macOS evidence

Use native SwiftUI/AppKit accessibility semantics and validate with keyboard navigation, macOS accessibility settings, and VoiceOver. Check labels, roles, values, focus movement, menus/sheets, full keyboard access, reduced motion, contrast, and hosted SwiftTerm behavior.

## Boundaries

- Do not apply web-only HTML, ARIA, axe, or Playwright guidance to native clients.
- Do not claim screen-reader success from static code inspection alone.
- Do not force custom accessibility behavior when native controls already provide correct semantics.
- Report target-platform checks that did not run as `NOT CHECKED`.

## Assessment format

```markdown
## Native Accessibility Assessment

- **Keyboard-only:** PASS | FAIL | NOT CHECKED — evidence
- **Focus behavior:** PASS | FAIL | NOT CHECKED — evidence
- **Accessibility tree/screen reader:** PASS | FAIL | NOT CHECKED — evidence
- **Dynamic status and errors:** PASS | FAIL | NOT CHECKED — evidence
- **Contrast/scaling/reduced motion:** PASS | FAIL | NOT CHECKED — evidence
- **Terminal integration:** PASS | FAIL | NOT CHECKED — evidence
- **Human target-machine validation still needed:** yes | no — details
```

A primary workflow that cannot be completed with the keyboard or target screen reader is blocking.
