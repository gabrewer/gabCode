# Windows Terminal WPF x64 assets

GabCode consumes two runtime files built from Microsoft's Windows Terminal preview tag `v1.26.3133.0`, commit `c4e28e32c0c868474340eb4ae415096aab9026d7`:

- `Microsoft.Terminal.Wpf.dll`
- `Microsoft.Terminal.Control.dll`

The files, upstream `LICENSE`, `NOTICE.md`, and their manifest live under `third_party/microsoft-terminal/v1.26.3133.0/`. No unofficial NuGet package or patched upstream source is used.

## Verify the committed layout

From the gabCode repository root on Windows:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1 -Verify
```

Verification checks the upstream tag/commit metadata, asset layout, required notices and their integrity, and rejects extra files in the versioned asset directory. Runtime DLL hashes are not pinned.

## Regenerate from pinned source

Install the prerequisites listed in `Documentation/dependencies/windows-terminal-wpf.md`, including Visual Studio 2026 C++ components and Windows SDK 10.0.26100.0. Then run:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1
```

The script clones into a unique short path on the repository drive, checks the exact commit, initializes submodules, performs both SDK-style restores plus the central upstream `packages.config` restore, runs the x64 Release WPF MSBuild target, copies only the two runtime DLLs and notices, and removes only the checkout it created. Independent builds need not be byte-identical.

The script invokes MSBuild in the Visual Studio developer environment after the restores, targeting `OpenConsole.slnx`. No upstream source or compiler policy is changed.

To reuse an existing exact pinned checkout while developing the script:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1 -CheckoutPath X:\path\to\terminal
```

The checkout may contain ignored build output, but tracked upstream files must remain unchanged. Do not commit PDBs, packages, the checkout, or other generated output.
