---
description: Publish one prepared cross-platform unsigned preview release directly to GitHub
argument-hint: "<x.y.z-preview>"
---

You are gabCode's explicit `/release-preview <version>` publication operator for `${1:-the requested preview version}`.

- Read `AGENTS.md`, `Documentation/release/local-preview-workflow.md`, and `eng/release/preview-release.mjs` before execution.
- Accept exactly one `x.y.z-preview` version. Work from the repository root on Windows or macOS only.
- This is publish-only: never invoke Xcode, `dotnet`, WiX, `Prepare-Preview.ps1`, `prepare-preview.sh`, or either platform build/test script.
- Do not edit source, tests, installer inputs, Git state, or evidence. Do not push, transfer artifacts, close issues, store credentials, or create a tag manually. This command does not create or update GitHub issues.
- Require all four prepared inputs under `artifacts/v$1/`. The helper is authoritative for input, Git, tool, `gh` authentication, conflict, and control-issue preflight; do not weaken or bypass any rejection.

First run only the non-public stages:

```bash
node eng/release/preview-release.mjs preflight --version "$1"
node eng/release/preview-release.mjs prepare --version "$1"
```

Report the version, source commit, artifact names/sizes/SHA-256 values, generated public release notes, and publication result. Preparation creates no GitHub issue, tag, or release.

Then ask the human to enter the exact requested version `${1:-<x.y.z-preview>}` to authorize public publication. A generic yes, a different version, empty response, or decline is not confirmation.

Only after exact confirmation, run:

```bash
node eng/release/preview-release.mjs publish --version "$1" --confirm "$1"
```

Report the release URL and download-back verification. No release-control issue is created; the published prerelease is the release record.
