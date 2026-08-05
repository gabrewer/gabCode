# Windows unsigned developer preview

This document covers `gabCode-x.y.z-preview-windows-x64.msi`, the intentionally unsigned Windows artifact for a gabCode `x.y.z-preview` release. It is an unsupported developer preview for trusted testers, not a production-ready or trusted package.

## Supported target

- Windows 11 x64
- Per-user installation under `%LOCALAPPDATA%\Programs\gabCode`
- No administrator elevation, machine-wide service, updater, or custom action
- Self-contained .NET 10 desktop payload; the target does not need a preinstalled .NET 10 Desktop Runtime

Windows ARM64, Windows on ARM emulation qualification, Windows 10, and machine-wide installation are not supported by this workflow.

## Version and trust boundary

Only versions matching `x.y.z-preview` are supported. The Windows numeric components must fit `255.255.65535`. The full preview identifier determines a distinct ProductCode; every preview shares the stable UpgradeCode. Windows Installer receives numeric product version `x.y.z`.

The MSI, `GabCode.Windows.exe`, the gabCode application assembly, and the pinned Microsoft Terminal DLLs are intentionally unsigned. `Get-AuthenticodeSignature` must report `NotSigned` for those files. The self-contained payload also preserves valid Microsoft signatures on many .NET and Windows runtime files; those signatures do not sign or establish trust for gabCode.

A package downloaded from a public GitHub prerelease can carry Mark of the Web. Microsoft Defender SmartScreen or Windows Installer may warn or block according to local policy. Before continuing, a trusted tester confirms:

1. the download came from the expected gabCode `v<version>` prerelease;
2. its SHA-256 equals the reviewed `SHA256SUMS.txt` entry;
3. the MSI signature state is `NotSigned`.

The downloaded-file warning path remains target-machine evidence and must be recorded as `PASS`, `FAIL`, or `NOT CHECKED`; a locally built MSI does not reproduce Mark of the Web.

```powershell
Get-FileHash .\gabCode-<version>-windows-x64.msi -Algorithm SHA256
Get-AuthenticodeSignature .\gabCode-<version>-windows-x64.msi | Format-List Status,StatusMessage
```

## Install, repair, and remove

Run native Windows Installer operations from a non-elevated PowerShell session. Replace `<version>` with the exact preview label:

```powershell
$package = (Resolve-Path .\gabCode-<version>-windows-x64.msi).Path
$install = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/i', "`"$package`"" -Wait -PassThru
$install.ExitCode
```

A successful install returns `0` and creates:

- one **gabCode** current-user Start menu entry;
- one **gabCode developer preview** entry under **Settings > Apps > Installed apps**;
- no desktop shortcut.

The Start entry uses `%LOCALAPPDATA%\Programs\gabCode` as its working directory until project/worktree selection owns launch context.

A same-package repair/reinstall is non-elevated:

```powershell
$log = Join-Path $env:TEMP 'gabCode-preview-repair.log'
$repair = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/fa', "`"$package`"", '/norestart', '/L*v', "`"$log`"" `
  -Wait -PassThru
$repair.ExitCode
```

Remove through Installed apps or:

```powershell
$uninstall = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/x', "`"$package`"" -Wait -PassThru
$uninstall.ExitCode
```

Install, repair, upgrade, and uninstall must not modify repositories/worktrees or delete user data outside the application install directory. Verification refuses to replace an installation it did not create.

## Shipped licenses and notices

The installed payload contains:

- `licenses\gabCode\LICENSE.txt` — gabCode MIT license;
- `licenses\microsoft-terminal\LICENSE.txt` — Microsoft Terminal MIT license;
- `licenses\microsoft-terminal\NOTICE.md` — Microsoft Terminal third-party notices.

The approved Terminal DLLs come from tag `v1.24.11911.0`; installed hashes must match `third_party/microsoft-terminal/v1.24.11911.0/manifest.json`.

## Prepare a release input

WiX is pinned as repository-local .NET tool `7.0.0` in `.config/dotnet-tools.json`. WiX 7 requires the `wix7` OSMF EULA identifier for scripted builds; review <https://wixtoolset.org/osmf/> and confirm applicable maintenance-fee obligations before building.

After the workflow implementation has been reviewed and merged to current `origin/main`, use the explicit Pi command:

```text
/build-preview-msi x.y.z-preview
```

The prompt invokes the deterministic entry point:

```powershell
pwsh -NoProfile -File eng/release/windows/Prepare-Preview.ps1 `
  -Version x.y.z-preview
```

Preparation requires a clean working tree with no non-ignored changes and `HEAD == origin/main`. It runs repository restore/build/tests, builds the MSI through `Build-Preview.ps1`, and runs the bounded package/install/repair/later-patch upgrade/uninstall verification through `Test-Preview.ps1`.

Successful preparation writes exactly this Windows pair while preserving a matching Mac pair already present:

```text
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-windows-x64.msi
artifacts/vx.y.z-preview/gabCode-x.y.z-preview-windows-x64.evidence.json
```

Copy both files together. The evidence records the reviewed full source commit and recomputable artifact facts. Preparation creates no GitHub issue, tag, release, or transfer configuration. `/release-preview <version>` is a separate publish-only command that requires both platform pairs.

The lower-level package commands remain available for focused diagnostics:

```powershell
pwsh -NoProfile -File eng/release/windows/Build-Preview.ps1 `
  -Version x.y.z-preview `
  -OutputDirectory .pi/tmp/windows-package-diagnostic
pwsh -NoProfile -File eng/release/windows/Test-Preview.ps1 `
  -PackagePath .pi/tmp/windows-package-diagnostic/gabCode-x.y.z-preview-windows-x64.msi
```

`Build-Preview.ps1` refuses to remove unrelated output entries. `Test-Preview.ps1` uses bounded, noninteractive Windows Installer operations and records logs plus `artifact-report.json` under its selected evidence directory.

## Troubleshooting

- **Source authority failure:** fetch/review/merge the workflow and run from a clean checkout at current `origin/main`; do not bypass the guard.
- **`WIX7015` EULA error:** use the repository script with its reviewed `wix7` identifier; do not persist acceptance for another user or organization.
- **Hash mismatch:** stop and obtain the asset/checksum again.
- **MSI or gabCode/Terminal file reports `NotSigned`:** expected for this preview; vendor runtime signatures do not change that status.
- **Installer exit code other than `0`:** preserve the bounded verbose log; do not retry with elevation.
- **Existing installation blocks verification:** use Installed apps to remove it only after the user chooses to do so. The verifier intentionally refuses to remove it.
- **Partial/unknown artifact output:** preserve the files and resolve ownership; preparation never broadly cleans the artifact directory.

## Native accessibility assessment

Record target-machine results rather than inferring them from MSI source:

- **Keyboard-only installer/uninstaller:** NOT CHECKED — requires native interactive Windows Installer/Installed apps exercise.
- **Focus behavior:** NOT CHECKED — requires the native interactive flow.
- **Accessibility tree/screen reader:** NOT CHECKED — Narrator is not exercised by deterministic artifact verification.
- **Dynamic status and errors:** NOT CHECKED — requires an interactive failure/success flow.
- **Contrast/scaling/reduced motion:** NOT CHECKED — no settings-transition scenario is automated.
- **Terminal integration:** NOT CHECKED — requires Start-menu launch, both terminals rooted in the accepted installed application directory, close confirmation, and process cleanup.
- **Human target-machine validation still needed:** yes — downloaded-file warning path, clean Windows 11 x64 launch without a preinstalled desktop runtime, keyboard-only native flow, terminal/exit cleanup, and exercised accessibility settings must be reported separately.
