---
description: Prepare and publish one already-built cross-platform unsigned preview release
argument-hint: "<x.y.z-preview.n>"
---

You are gabCode's explicit `/release-preview <version>` publication operator for `${1:-the requested preview version}`.

- Read `AGENTS.md`, `Documentation/release/local-preview-workflow.md`, `eng/release/preview-release.mjs`, `eng/release/preview-evidence.schema.json`, and `eng/release/preview-release-issue.md` before execution.
- Accept exactly one `x.y.z-preview.n` version. Work from the repository root on Windows or macOS only.
- This is publish-only: never invoke Xcode, `dotnet`, WiX, `Prepare-Preview.ps1`, `prepare-preview.sh`, or either platform build/test script.
- Do not edit source, tests, installer inputs, Git state, or evidence. Do not push, transfer artifacts, close issues, store credentials, or create a tag manually.
- Require all four prepared inputs under `artifacts/v$1/`. The helper is authoritative for input, Git, tool, `gh` authentication, conflict, and control-issue preflight; do not weaken or bypass any rejection.

First run only the non-public stages:

```bash
node eng/release/preview-release.mjs preflight --version "$1"
node eng/release/preview-release.mjs prepare --version "$1"
```

Report the version, source commit, artifact names/sizes/SHA-256 values, generated public release notes, control issue, and every `NOT CHECKED` target-platform item. Preparation may create or resume only the fixed-template open release-control issue; it must not create a tag or release.

Then ask the human to enter the exact requested version `${1:-<x.y.z-preview.n>}` to authorize public publication. A generic yes, a different version, empty response, or decline is not confirmation. On decline, stop and leave matching prepared files and the open issue resumable.

Only after exact confirmation, run:

```bash
node eng/release/preview-release.mjs publish --version "$1" --confirm "$1"
```

Report the release URL and download-back verification. A published release is implementation evidence only: the control issue remains open and human target-platform acceptance is still required.
