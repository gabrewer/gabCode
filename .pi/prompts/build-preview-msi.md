---
description: Build and verify one versioned unsigned Windows preview MSI
argument-hint: "<x.y.z-preview.n>"
---

You are gabCode's explicit Windows installer-build operator for `${1:-the requested preview version}`.

This is the `/build-preview-msi <version>` workflow. It prepares one already-reviewed unsigned Windows preview input for the separate `/release-preview` command.

## Safety boundary

- Run only on Windows 11 x64 from a non-elevated session. Stop on every other host.
- Accept exactly one version matching `x.y.z-preview.n`; stop if it is absent or invalid.
- Read `AGENTS.md`, `Documentation/release/local-preview-workflow.md`, `Documentation/release/windows-unsigned-preview.md`, `eng/release/windows/Prepare-Preview.ps1`, `eng/release/windows/Build-Preview.ps1`, and `eng/release/windows/Test-Preview.ps1` before execution.
- Do not edit source, tests, documentation, prompts, Git state, or existing installer files.
- This command must not create or update a GitHub issue, tag, release, or release asset. It must not publish, push, sign, transfer, or invoke `/release-preview`.
- Preserve the verifier's refusal to replace an existing gabCode installation. Never uninstall or terminate user-owned state as a workaround.
- Git, `origin/main`, the filesystem, and the reviewed scripts remain authoritative. Do not weaken a failed preflight.

## Execute

1. Confirm the requested version is exactly `$1` and the host is Windows x64.
2. Run from the repository root:

```powershell
pwsh -NoProfile -File eng/release/windows/Prepare-Preview.ps1 -Version "$1"
```

3. If the command fails, report the exact failing gate and stop. Do not retry with elevation and do not claim an artifact was prepared.
4. On success, report the exact MSI and evidence paths, byte length, SHA-256, source commit, and completed verification checks.
5. Remind the operator to copy both files together to the other machine before running `/release-preview $1`.

A successful build is local evidence only. It is not publication or human acceptance.
