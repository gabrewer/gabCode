# Windows Terminal WPF x64 assets

GabCode consumes two runtime files built from Microsoft's Windows Terminal tag `v1.24.11911.0`, commit `5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc`:

- `Microsoft.Terminal.Wpf.dll`
- `Microsoft.Terminal.Control.dll`

The files, upstream `LICENSE`, `NOTICE.md`, and their manifest live under `third_party/microsoft-terminal/v1.24.11911.0/`. No unofficial NuGet package or patched upstream source is used.

## Verify the committed layout

From the gabCode repository root on Windows:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1 -Verify
```

Verification checks the approved tag/commit metadata, exact byte lengths and SHA-256 hashes, required notices, and rejects extra files in the versioned asset directory.

## Regenerate from pinned source

Install the prerequisites listed in `Documentation/dependencies/windows-terminal-wpf.md`, including the v143 desktop and UWP C++ components and Windows SDK 10.0.22621.0. Then run:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1
```

The script clones into a unique short path on the repository drive, checks the exact commit, initializes submodules, performs both SDK-style restores plus the central upstream `packages.config` restore, runs the approved x64 Release WPF MSBuild target, validates the built hashes, copies only the two runtime DLLs and notices, and removes only the checkout it created.

The script invokes MSBuild directly after those restores instead of using `Invoke-OpenConsoleBuild`. That helper first asks the bundled NuGet 4.1 client to parse the entire solution, which is unreliable with newer Visual Studio 18 installations; its final build operation is the same solution, configuration, platform, and target used here. No source, toolset, warning, or compiler policy is changed.

To reuse an existing exact pinned checkout while developing the script:

```powershell
pwsh -NoProfile -File eng/windows-terminal/Build-WpfControl.ps1 -CheckoutPath X:\path\to\terminal
```

The checkout may contain ignored build output, but tracked upstream files must remain unchanged. Do not commit PDBs, packages, the checkout, or other generated output.
