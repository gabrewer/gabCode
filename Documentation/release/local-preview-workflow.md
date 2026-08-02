# Local unsigned preview build and release workflow

This workflow produces and publishes unsigned/ad-hoc gabCode developer previews through three explicit Pi operator commands:

```text
/build-preview-dmg <version>   # Apple Silicon macOS only
/build-preview-msi <version>   # Windows 11 x64 only
/release-preview <version>     # Windows or macOS; publication only
```

The build commands prepare one platform artifact and its evidence. `/release-preview <version>` consumes already-prepared files and **never builds installers**. Git, the filesystem, installed tools, and GitHub remain authoritative throughout.

## Supported version and derived identities

Only `x.y.z-preview.n` is accepted, where `x`, `y`, and `z` are non-negative decimal integers and `n` is a positive decimal integer. Stable versions, other prerelease labels, and a zero preview ordinal are rejected.

Because the same version feeds Windows Installer, the parsed numeric components must fit its supported range:

- `x`: 0–255
- `y`: 0–255
- `z`: 0–65535

For `x.y.z-preview.n`, all commands derive these identities without editing source files:

| Identity | Value |
| --- | --- |
| Marketing/numeric version | `x.y.z` |
| Preview/build ordinal | `n` |
| Git tag | `vx.y.z-preview.n` |
| Windows artifact | `gabCode-x.y.z-preview.n-windows-x64.msi` |
| Windows evidence | `gabCode-x.y.z-preview.n-windows-x64.evidence.json` |
| macOS artifact | `gabCode-x.y.z-preview.n-macos-arm64.dmg` |
| macOS evidence | `gabCode-x.y.z-preview.n-macos-arm64.evidence.json` |
| macOS volume | `gabCode x.y.z Preview n` |

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
/build-preview-dmg x.y.z-preview.n
```

The prompt validates the host, version, source authority, and owned output, then invokes `eng/release/macos/prepare-preview.sh`. The preparation command runs the reviewed package resolution, native tests, arm64 Release build, ad-hoc signing, expected Gatekeeper rejection, DMG verification, inventory/notices checks, prohibited-content checks, and adversarial verifier. It atomically writes only:

```text
artifacts/vx.y.z-preview.n/gabCode-x.y.z-preview.n-macos-arm64.dmg
artifacts/vx.y.z-preview.n/gabCode-x.y.z-preview.n-macos-arm64.evidence.json
```

It creates no GitHub issue, tag, or release.

## Prepare the MSI on Windows

On the declared Windows 11 x64 target, from a non-elevated session, run:

```text
/build-preview-msi x.y.z-preview.n
```

The prompt validates the host, version, source authority, and owned output, then invokes `eng/release/windows/Prepare-Preview.ps1`. The preparation command runs the reviewed restore/build/test/package and bounded MSI verification gates. Existing installations and processes remain protected by `Test-Preview.ps1`; the workflow never removes an installation it did not create. It atomically writes only:

```text
artifacts/vx.y.z-preview.n/gabCode-x.y.z-preview.n-windows-x64.msi
artifacts/vx.y.z-preview.n/gabCode-x.y.z-preview.n-windows-x64.evidence.json
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
artifacts/vx.y.z-preview.n/
├── gabCode-x.y.z-preview.n-macos-arm64.dmg
├── gabCode-x.y.z-preview.n-macos-arm64.evidence.json
├── gabCode-x.y.z-preview.n-windows-x64.msi
└── gabCode-x.y.z-preview.n-windows-x64.evidence.json
```

A missing sidecar, partial pair, unexpected entry, link/reparse point, mismatched commit/version/name/hash, or changed artifact blocks the initial run. On a safe resume, only the two known generated files described below may additionally exist, and their contents must match regeneration.

## Publish from either platform

Run only after all four inputs are in place:

```text
/release-preview x.y.z-preview.n
```

The cross-platform helper uses Node built-ins plus installed `git` and authenticated `gh`. It does not invoke Xcode, .NET, WiX, either platform build prompt, or either preparation script.

### Preflight before issue creation

The helper verifies, in order:

1. supported version and deterministic directory/file names;
2. four regular inputs and no unknown entries;
3. schema version, platform, version, source commit, and completed `PASS` evidence;
4. recomputed artifact byte lengths and SHA-256 values;
5. the same source commit in both sidecars;
6. clean tracked source with `HEAD` and current `origin/main` at that commit;
7. Node, Git, `gh`, GitHub authentication, and repository identity;
8. no existing tag/release and no ambiguous, conflicting, or closed control-issue state.

**No control issue is created until** every preflight check succeeds. A missing or invalid input reports the exact failing path/fact and performs no GitHub mutation.

### Control issue lifecycle

After valid preflight, the helper searches for the exact title `🧪 Preview Release: v<version>` and marker `gabcode-preview-release-control:v1`:

- no matching issue: create one open issue from `eng/release/preview-release-issue.md`;
- exactly one matching open issue with the same version/source/artifact facts: resume it;
- multiple matches, a closed match, or conflicting recorded facts: stop for human disposition.

The generated issue declares `github-issues`, records artifact facts and remaining target-platform checks, and must remain open. Routine issue creation here does not replace feature planning through `/pm-agent` or implementation through `/team-lead`.

### Preparation without public mutation

The helper deterministically creates:

- `SHA256SUMS.txt`, containing sorted lowercase hashes for the MSI and DMG only;
- `release-notes.md`, describing the unsupported unsigned/ad-hoc preview and linking its control issue.

It updates the open control issue with the prepared facts and displays the version, tag, target commit, filenames, byte lengths, hashes, notes, and all remaining `NOT CHECKED` rows. This phase creates no tag or release.

If matching generated files already exist, regeneration must be byte-identical. The helper refuses to overwrite mismatched or unknown files.

### Explicit publication gate

Public mutation requires a human response naming the exact version requested by the prompt. A generic yes, a different version, empty input, or declined confirmation does not publish. Declining leaves the matching prepared files and open issue resumable.

Immediately before publication, the helper repeats input, Git, issue, tag, and release conflict checks. It then creates a GitHub prerelease targeting the recorded commit and uploads exactly:

1. the Windows MSI;
2. the macOS DMG;
3. `SHA256SUMS.txt`.

The evidence sidecars and `release-notes.md` are not release assets. The helper downloads all published assets to owned temporary storage and verifies release metadata, filenames, sizes, SHA-256 values, and exact bytes before posting the release URL and evidence to the issue. It never pushes a branch or closes the issue.

## Failure, cancellation, and acceptance

Before issue creation, any failure is non-mutating. After issue creation, preparation or publication failures are recorded on that issue with observed facts; matching state may be resumed, while mismatches are never replaced automatically.

Installer ownership protections are never bypassed. Unknown local files are never deleted. Authentication remains in the operating system/`gh` store and is never copied into prompts, evidence, generated notes, or issues.

Automated preparation and publication are implementation evidence only. Download warning paths, installation/copy, launch, keyboard/focus/accessibility, terminal behavior, and cleanup must be checked on their declared target operating systems. Unrun checks remain `NOT CHECKED`; a human decides acceptance and issue disposition.
