# Local unsigned preview build and release workflow

This workflow produces and publishes unsigned/ad-hoc gabCode developer previews through three explicit Pi operator commands:

```text
/build-preview-dmg <version>   # Apple Silicon macOS only
/build-preview-msi <version>   # Windows 11 x64 only
/release-preview <version>     # Windows or macOS; publication only
```

The build commands prepare one platform artifact and its evidence. `/release-preview <version>` consumes already-prepared files and **never builds installers**. Git, the filesystem, installed tools, and GitHub remain authoritative throughout.

## Supported version and derived identities

Only `x.y.z-preview` is accepted, where `x`, `y`, and `z` are non-negative decimal integers. Each new preview advances the patch version. Stable versions, other prerelease labels, and ordinal preview suffixes are rejected.

Because the same version feeds Windows Installer, the parsed numeric components must fit its supported range:

- `x`: 0–255
- `y`: 0–255
- `z`: 0–65535

For `x.y.z-preview`, all commands derive these identities without editing source files:

| Identity | Value |
| --- | --- |
| Marketing/numeric version | `x.y.z` |
| Preview/build ordinal | `n` |
| Git tag | `vx.y.z-preview` |
| Windows artifact | `gabCode-x.y.z-preview-windows-x64.msi` |
| Windows evidence | `gabCode-x.y.z-preview-windows-x64.evidence.json` |
| macOS artifact | `gabCode-x.y.z-preview-macos-arm64.dmg` |
| macOS evidence | `gabCode-x.y.z-preview-macos-arm64.evidence.json` |
| macOS volume | `gabCode x.y.z Preview` |

The filename version, package metadata, evidence version, and later release tag must agree exactly.

## Authority and source preflight

A platform build is eligible for release evidence only when all of the following are true:

1. `git fetch origin main` succeeds and the local `origin/main` reference is current.
2. `HEAD` is the full commit identified by `origin/main`.
3. The tracked working tree is clean. Ignored output under `artifacts/` and `.pi/tmp/` does not make source dirty.
4. The required target-platform tools and reviewed package scripts are available.
5. The requested version satisfies the contract above.

A preparation command records the full lowercase 40-character source commit that it actually built. It cannot use a local evidence file, issue, tag, or release to override Git. Rehearsal from an unmerged feature commit can test rejection and pure contract behavior, but it is not releasable `origin/main` evidence.

## Prepare the DMG on macOS

On the declared Apple Silicon target Mac, run:

```text
/build-preview-dmg x.y.z-preview
```

The prompt validates the host, version, source authority, and owned output, then invokes `eng/release/macos/prepare-preview.sh`. The preparation command runs the reviewed package resolution, native tests, arm64 Release build, ad-hoc signing, expected Gatekeeper rejection, DMG verification, inventory/notices checks, prohibited-content checks, and adversarial verifier. It atomically writes only:

```text
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-macos-arm64.dmg
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-macos-arm64.evidence.json
```

It creates no GitHub issue, tag, or release.

## Prepare the MSI on Windows

On the declared Windows 11 x64 target, from a non-elevated session, run:

```text
/build-preview-msi x.y.z-preview
```

The prompt validates the host, version, source authority, and owned output, then invokes `eng/release/windows/Prepare-Preview.ps1`. The preparation command runs the reviewed restore/build/test/package and bounded MSI verification gates. Existing installations and processes remain protected by `Test-Preview.ps1`; the workflow never removes an installation it did not create. It atomically writes only:

```text
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-windows-x64.msi
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-windows-x64.evidence.json
```

It creates no GitHub issue, tag, or release.

## Evidence contract

Each `*.evidence.json` file conforms to `eng/release/preview-evidence.schema.json` schema version 1 and contains only reviewed release facts:

- platform, preview version, and full source commit;
- its own deterministic filename;
- artifact filename, byte length, and lowercase SHA-256;
- target operating system, architecture, and build-tool identity;
- a `PASS` verification result, named completed checks, and UTC completion time.

Evidence contains no command output, terminal content, environment dump, private path, authentication material, or transfer configuration. Evidence is not trusted blindly: the release helper reparses it, derives expected names from the requested version, and recomputes artifact bytes and hashes.

The artifact and matching evidence sidecar are one transfer unit. Copy the missing platform pair to the other machine by any trusted manual mechanism. The workflow does not configure or automate transport.

## Required publication input

Before `/release-preview` runs, one machine must contain these four regular, non-symlink inputs:

```text
artifacts/vx.y.z-preview/
├── gabCode-x.y.z-preview-macos-arm64.dmg
├── gabCode-x.y.z-preview-macos-arm64.evidence.json
├── gabCode-x.y.z-preview-windows-x64.msi
└── gabCode-x.y.z-preview-windows-x64.evidence.json
```

A missing sidecar, partial pair, unexpected entry, link/reparse point, mismatched commit/version/name/hash, or changed artifact blocks the initial run. On a safe resume, only the two known generated files described below may additionally exist, and their contents must match regeneration.

## Publish from either platform

Run only after all four inputs are in place:

```text
/release-preview x.y.z-preview
```

The cross-platform helper uses Node built-ins plus installed `git` and authenticated `gh`. It does not invoke Xcode, .NET, WiX, either platform build prompt, or either preparation script.

### Preflight before publication

The helper verifies, in order:

1. supported version and deterministic directory/file names;
2. four regular inputs and no unknown entries;
3. schema version, platform, version, source commit, and completed `PASS` evidence;
4. recomputed artifact byte lengths and SHA-256 values;
5. the same source commit in both sidecars;
6. clean tracked source with `HEAD` and current `origin/main` at that commit;
7. Node, Git, `gh`, GitHub authentication, and repository identity;
8. no existing tag or GitHub release for the requested version.

Every failed preflight is non-mutating. The publisher creates no GitHub control issue; the GitHub release and tag are the publication record.

### Preparation without public mutation

The helper deterministically creates:

- `SHA256SUMS.txt`, containing sorted lowercase hashes for the MSI and DMG only;
- `release-notes.md`, the public GitHub prerelease description. It deterministically identifies the preview version and target commit, summarizes reviewed commit subjects since the previous preview tag in **Highlights**, **Bug Fixes**, and **Other Changes**, links issue/PR references present in that history, and retains the unsigned/ad-hoc and `NOT CHECKED` disclosures. It never derives claims from local session input or evidence sidecars.

It displays the version, target commit, filenames, byte lengths, hashes, and release notes. This phase creates no GitHub issue, tag, or release.

If matching generated files already exist, regeneration must be byte-identical. The helper refuses to overwrite mismatched or unknown files.

### Explicit publication gate

Public mutation requires a human response naming the exact version requested by the prompt. A generic yes, a different version, empty input, or declined confirmation does not publish. Declining leaves the matching prepared files and open issue resumable.

Immediately before publication, the helper repeats input, Git, tag, and release conflict checks. It then creates a GitHub prerelease targeting the recorded commit, uses the generated `release-notes.md` as its public description, and uploads exactly:

1. the Windows MSI;
2. the macOS DMG;
3. `SHA256SUMS.txt`.

The evidence sidecars and `release-notes.md` are not release assets. The helper downloads all published assets to owned temporary storage and verifies release metadata, filenames, sizes, SHA-256 values, and exact bytes. It never pushes a branch or creates or updates an issue.

## Failure, cancellation, and acceptance

Any failure before `gh release create` is non-mutating. GitHub provider errors during publication are surfaced directly; the command does not create a separate release issue.

Installer ownership protections are never bypassed. Unknown local files are never deleted. Authentication remains in the operating system/`gh` store and is never copied into prompts, evidence, generated notes, or issues.

The published prerelease description is public-facing and contains only release information and relevant unsigned-preview warnings. The release itself is the durable publication record.
