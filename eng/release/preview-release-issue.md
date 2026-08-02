<!-- gabcode-preview-release-control:v1 -->
## 🧪 Preview Release: v{{VERSION}}

**Status:** 🚧 prepared for publication review
**State backend:** github-issues
**Source commit:** `{{SOURCE_COMMIT}}`
**Release type:** unsigned/ad-hoc GitHub prerelease

## Goal

Publish the two already-prepared gabCode preview installers for `{{VERSION}}` from the reviewed source commit, without rebuilding either installer during this release operation.

## Prepared artifact evidence

| Platform | Artifact | Bytes | SHA-256 | Automated preparation |
| --- | --- | ---: | --- | --- |
| Windows x64 | `{{WINDOWS_ARTIFACT}}` | {{WINDOWS_BYTES}} | `{{WINDOWS_SHA256}}` | PASS |
| macOS arm64 | `{{MACOS_ARTIFACT}}` | {{MACOS_BYTES}} | `{{MACOS_SHA256}}` | PASS |

The publisher recomputed these facts from the local files after validating both versioned evidence sidecars. Git and the files remain authoritative.

## Publication checklist

- [ ] Local inputs still match the recorded names, byte lengths, hashes, version, and source commit.
- [ ] `origin/main` still resolves to `{{SOURCE_COMMIT}}` and the tracked working tree remains clean.
- [ ] Release notes and `SHA256SUMS.txt` were generated and reviewed.
- [ ] No conflicting tag or GitHub release exists for `v{{VERSION}}`.
- [ ] The operator supplied the exact version-named confirmation required for public mutation.
- [ ] The prerelease contains exactly the MSI, DMG, and `SHA256SUMS.txt`.
- [ ] Download-back metadata, sizes, hashes, and exact bytes match.

## Target-platform acceptance

| Check | Windows 11 x64 | Apple Silicon macOS |
| --- | --- | --- |
| Downloaded-file warning/trust path | NOT CHECKED | NOT CHECKED |
| Install/copy and launch | NOT CHECKED | NOT CHECKED |
| Keyboard/focus/accessibility exercise | NOT CHECKED | NOT CHECKED |
| Terminal startup and process cleanup | NOT CHECKED | NOT CHECKED |

Unrun checks remain `NOT CHECKED`. Automated tests, a published release, and implementation commits are evidence only; they are not human acceptance.

## Safety and disposition

- This control issue must remain open and without final completion labels for human disposition.
- The release tooling must not push a branch, alter source, store secrets, or bypass an installer ownership guard.
- A declined confirmation or recoverable failure leaves this issue and matching local inputs resumable.
- Any mismatch blocks publication and must be recorded with the observed evidence.
