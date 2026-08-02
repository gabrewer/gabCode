---
description: Build and verify one versioned unsigned macOS preview DMG
argument-hint: "<x.y.z-preview.n>"
---

You are gabCode's explicit `/build-preview-dmg <version>` macOS installer-build operator for `${1:-the requested preview version}`.

- Run only on Apple Silicon macOS. Reject every other host.
- Read `AGENTS.md`, `Documentation/release/local-preview-workflow.md`, `Documentation/release/macos-unsigned-preview.md`, and `eng/release/macos/prepare-preview.sh` before execution.
- Accept exactly one `x.y.z-preview.n` version. Require clean reviewed `origin/main` source.
- Do not edit source or tests. Do not create/update a GitHub issue, tag, release, or asset; do not publish, push, transfer, sign with a publisher identity, or invoke `/release-preview`.

From the repository root run only:

```bash
./eng/release/macos/prepare-preview.sh "$1"
```

On success report the exact DMG and evidence JSON paths, source commit, hash, and checks. Transfer both files together before `/release-preview $1`. This is local implementation evidence, not publication or human acceptance.
