# Windows unsigned developer preview

This document covers `gabCode-0.0.1-preview.1-windows-x64.msi`, the intentionally unsigned Windows artifact for gabCode `0.0.1-preview.1`. It is an unsupported developer preview for trusted testers, not a production-ready or trusted package.

## Supported target

- Windows 11 x64
- Per-user installation under `%LOCALAPPDATA%\Programs\gabCode`
- No administrator elevation, machine-wide service, updater, or custom action
- Self-contained .NET 10 desktop payload; the target does not need a preinstalled .NET 10 Desktop Runtime

Windows ARM64, Windows on ARM emulation qualification, Windows 10, and machine-wide installation are not supported by this preview.

## Trust and warning boundary

The MSI, `GabCode.Windows.exe`, the gabCode application assembly, and the pinned Microsoft Terminal DLLs are intentionally unsigned. `Get-AuthenticodeSignature` must report `NotSigned` for those files; that is the expected preview result, not evidence that the package is trusted. The self-contained payload also preserves valid Microsoft signatures on many .NET and Windows runtime files. Those vendor signatures authenticate only those runtime files—they do not sign or establish trust for the gabCode MSI or application.

A package downloaded from the public GitHub prerelease can carry Mark of the Web. Microsoft Defender SmartScreen or Windows Installer may show an unrecognized-app/publisher warning or may block execution according to local policy. Do not treat a bypass as generally safe. A trusted tester should first confirm all of the following:

1. the download came from the gabCode repository's public `v0.0.1-preview.1` prerelease;
2. its SHA-256 equals the release's reviewed `SHA256SUMS.txt` entry;
3. the MSI signature state is `NotSigned` (valid Microsoft signatures on bundled runtime files do not change that package status).

The exact downloaded-file SmartScreen path remains target-machine evidence and must be recorded as `PASS`, `FAIL`, or `NOT CHECKED`; a locally built MSI does not reproduce Mark of the Web.

```powershell
Get-FileHash .\gabCode-0.0.1-preview.1-windows-x64.msi -Algorithm SHA256
Get-AuthenticodeSignature .\gabCode-0.0.1-preview.1-windows-x64.msi | Format-List Status,StatusMessage
```

## Install and launch

Double-click the verified MSI, or run the native Windows Installer flow from a non-elevated PowerShell session:

```powershell
$package = (Resolve-Path .\gabCode-0.0.1-preview.1-windows-x64.msi).Path
$process = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/i', "`"$package`"" `
  -Wait -PassThru
$process.ExitCode
```

A successful install returns exit code `0`. Installation creates:

- one **gabCode** entry in the current user's Start menu;
- one **gabCode developer preview** entry in **Settings > Apps > Installed apps**;
- no desktop shortcut.

Launch **gabCode** from Start. For this unsigned preview, the Start entry deliberately uses `%LOCALAPPDATA%\Programs\gabCode` as the working directory, so both ordinary terminals start there. Choosing a worktree from a Start launch is deferred rather than added to this packaging increment. Use the existing close confirmation when terminals are active.

## Repair or reinstall

A same-package repair/reinstall is non-elevated and should return exit code `0`:

```powershell
$package = (Resolve-Path .\gabCode-0.0.1-preview.1-windows-x64.msi).Path
$log = Join-Path $env:TEMP 'gabCode-preview-repair.log'
$process = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/fa', "`"$package`"", '/norestart', '/L*v', "`"$log`"" `
  -Wait -PassThru
$process.ExitCode
```

The package uses stable upgrade identity and a distinct product identity for each full preview label. Both a later label with the same numeric MSI version (for example, `0.0.1-preview.2`) and a package with a higher numeric version replace this preview instead of installing an unintended side-by-side copy. The prerelease label is `0.0.1-preview.1`; Windows Installer receives numeric product version `0.0.1`.

## Uninstall

Use **Settings > Apps > Installed apps > gabCode developer preview > Uninstall**, or:

```powershell
$package = (Resolve-Path .\gabCode-0.0.1-preview.1-windows-x64.msi).Path
$process = Start-Process "$env:SystemRoot\System32\msiexec.exe" `
  -ArgumentList '/x', "`"$package`"" `
  -Wait -PassThru
$process.ExitCode
```

A successful uninstall returns exit code `0` and removes the Start menu entry and `%LOCALAPPDATA%\Programs\gabCode`. Install, repair, upgrade, and uninstall must not modify repositories/worktrees or delete gabCode/user data outside that installed application directory.

## Shipped licenses and notices

The installed payload contains:

- `licenses\gabCode\LICENSE.txt` — gabCode MIT license;
- `licenses\microsoft-terminal\LICENSE.txt` — Microsoft Terminal MIT license;
- `licenses\microsoft-terminal\NOTICE.md` — Microsoft Terminal third-party notices.

The exact approved `Microsoft.Terminal.Wpf.dll` and `Microsoft.Terminal.Control.dll` are shipped from tag `v1.24.11911.0`; their installed hashes must match `third_party/microsoft-terminal/v1.24.11911.0/manifest.json`.

## Reproducible repository build and verification

WiX is pinned as repository-local .NET tool `7.0.0` in `.config/dotnet-tools.json`. WiX 7 requires the `wix7` OSMF EULA identifier for each scripted build; review <https://wixtoolset.org/osmf/> and confirm the applicable maintenance-fee obligations before building.

From the repository root on Windows:

```powershell
dotnet tool restore
dotnet restore GabCode.slnx
dotnet build GabCode.slnx --configuration Release --no-restore
dotnet test GabCode.slnx --configuration Release --no-build
pwsh -NoProfile -File eng/release/windows/Build-Preview.ps1 `
  -Version 0.0.1-preview.1 `
  -OutputDirectory artifacts/v0.0.1-preview.1
pwsh -NoProfile -File eng/release/windows/Test-Preview.ps1 `
  -PackagePath artifacts/v0.0.1-preview.1/gabCode-0.0.1-preview.1-windows-x64.msi
Get-FileHash artifacts/v0.0.1-preview.1/gabCode-0.0.1-preview.1-windows-x64.msi -Algorithm SHA256
```

`Build-Preview.ps1` refuses to remove unrelated output entries and emits exactly the versioned MSI. `Test-Preview.ps1` uses bounded, noninteractive Windows Installer operations, including same-numeric and later-numeric upgrade probes; it records MSI logs and `artifact-report.json` under `.pi/tmp/windows-preview-verification/`.

## Troubleshooting

- **`WIX7015` EULA error:** use the repository script, which invokes the reviewed `wix7` EULA identifier. Do not persist acceptance on behalf of another user or organization.
- **Hash mismatch:** stop. Delete the download and obtain the asset/checksum again from the reviewed prerelease.
- **MSI or gabCode/Terminal file reports `NotSigned`:** expected for this preview. Valid signatures on bundled Microsoft runtime files are also expected, but they do not make the MSI or gabCode trusted.
- **Installer exit code other than `0`:** preserve a bounded verbose log with `/L*v <path>` and report the code plus log; do not retry with elevation as a workaround.
- **Existing installation blocks verification:** uninstall the prior developer preview through Installed apps. The verifier intentionally refuses to remove an installation it did not create.
- **Start menu launch fails:** verify `%LOCALAPPDATA%\Programs\gabCode\GabCode.Windows.exe` and the shipped `coreclr.dll`, `hostfxr.dll`, and Terminal DLL hashes before collecting a launch log.

## Native Accessibility Assessment

Record target-machine results rather than inferring them from the MSI source:

- **Keyboard-only installer/uninstaller:** NOT CHECKED — requires the native interactive Windows Installer/Installed apps flow.
- **Focus behavior:** NOT CHECKED — requires the native interactive flow.
- **Accessibility tree/screen reader:** NOT CHECKED — Narrator was not exercised by deterministic artifact verification.
- **Dynamic status and errors:** NOT CHECKED — requires an interactive failure/success flow.
- **Contrast/scaling/reduced motion:** NOT CHECKED — no settings-transition scenario is automated.
- **Terminal integration:** NOT CHECKED — requires Start-menu launch, both terminals rooted in the accepted installed application directory, active-terminal close confirmation, and process cleanup on the target.
- **Human target-machine validation still needed:** yes — downloaded-file warning path, clean Windows 11 x64 launch without .NET 10 Desktop Runtime, keyboard-only native flow, terminal/exit cleanup, and any accessibility settings exercised must be reported separately.
