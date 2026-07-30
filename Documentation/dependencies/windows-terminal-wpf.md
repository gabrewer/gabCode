# Windows Terminal WPF dependency gate

## Verdict: **GO — approved for Windows x64 integration with accepted limitations**

The exact Microsoft Windows Terminal WPF control source can be acquired and built on the target Windows machine, and its host renders and retains a live terminal HWND. The dependency is approved for gabCode's Windows terminal foundation.

The product explicitly accepts that the pinned WPF surface has no demonstrated keyboard-only route out of terminal focus, exposes neither search nor hyperlink actions through its public WPF/C ABI, and does not close an assigned terminal connection when the control is replaced or destroyed. These observations remain documented integration facts, but they do not block adoption and do not require an upstream patch or future product work unless a later human decision reintroduces those requirements.

No gabCode product code, tests, upstream source, generated package, or binary was changed for this gate. macOS is **NOT CHECKED**; this is a Windows-only evaluation.

## Pinned source and license

| Item | Evidence |
| --- | --- |
| Upstream | <https://github.com/microsoft/terminal> |
| Tag | `v1.24.11911.0` |
| Resolved commit | `5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc` |
| Release | [Windows Terminal v1.24.11911.0](https://github.com/microsoft/terminal/releases/tag/v1.24.11911.0), published 2026-07-16; non-draft, non-prerelease |
| Primary license | MIT (`LICENSE`) |
| Notice obligation | Ship the upstream `NOTICE.md` with any redistribution and review its third-party notices; it contains MIT, BSD-style, Apache-style, public-domain, CC-BY-SA test-spec, and other notices. |
| Upgrade boundary | Any update is a new pinned tag/commit and must repeat acquisition, clean build, output/dependency inventory, licensing, core runtime/input, and live-view movement checks. Accepted focus, search, hyperlink, and dedicated accessibility limitations remain non-blocking unless a later human decision changes the product requirements. No floating branch, unofficial NuGet package, source retarget, or pinned-source patch is approved. |

The WPF wrapper build has `Microsoft.Terminal.Wpf` product version `0.1+5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc`; it is an unsigned local development output, not a signed Microsoft redistribution artifact.

## Reproducible x64 Release build

### Validated environment

- Windows 10 `10.0.26200.0`, x64
- Visual Studio Enterprise 2026 `18.8.2`
- Windows SDK `10.0.22621.0`
- MSVC `v143` `14.44.35207`, including both:
  - `Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64`
  - `Microsoft.VisualStudio.ComponentGroup.UWP.VC.v143`
- .NET SDK `10.0.400-preview.0.26322.102` selected for the clean upstream SDK-style restores; the generated host requires an installed `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` 8.x runtime (8.0.28 and 8.0.29 were present during validation).

Verify the Visual Studio prerequisites before building:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64 -property installationPath
& $vswhere -products * -requires Microsoft.VisualStudio.ComponentGroup.UWP.VC.v143 -property installationPath
```

Both commands must return the Visual Studio installation path. The desktop compiler alone is insufficient: the upstream WPF target traverses UWP projects and fails with `MSB8020` without the v143 UWP component.

### Clean acquisition and build

Use a short external or ignored checkout path; the legacy bundled NuGet restore failed under a longer checkout path. Do not place upstream source or outputs in the tracked gabCode tree.

```powershell
$checkout = 'X:\wtf-<short-random-suffix>'
git clone --branch v1.24.11911.0 --depth 1 https://github.com/microsoft/terminal.git $checkout
git -C $checkout rev-parse HEAD # must be 5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc
Set-Location $checkout
git submodule update --init --recursive

# The upstream helper restores packages.config projects but omits these SDK-style restores.
dotnet restore .\src\cascadia\WpfTerminalControl\WpfTerminalControl.csproj -p:Platform=AnyCPU
dotnet restore .\src\cascadia\WpfTerminalTestNetCore\WpfTerminalTestNetCore.csproj -p:Platform=x64

Import-Module .\tools\OpenConsole.psm1
Set-MsbuildDevEnvironment
Invoke-OpenConsoleBuild /p:Configuration=Release /p:Platform=x64 "/t:Terminal\wpf\WpfTerminalTestNetCore" /m
```

The checkout's `NuGet.Config` uses the `TerminalDependencies@Local` Azure DevOps feed. On a fresh short-path checkout, the two explicit SDK restores followed by the exact approved upstream target completed with **18 warnings and 0 errors**. All 18 were `CS1668` diagnostics from RoslynCodeTaskFactory about the Visual Studio v143 ATL/MFC library path being absent from `LIB`; the qualified target does not consume ATL/MFC and linked successfully. The direct upstream helper command is intentionally unchanged; the explicit restores are a required reproducibility pre-step rather than a source or policy modification.

## Built x64 artifact inventory

The following files came from the validated host output at `src\cascadia\WpfTerminalTestNetCore\bin\x64\Release\net8.0-windows`. Hashes identify the observed local build; they are not a promise that every future source build is bit-for-bit identical.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `Microsoft.Terminal.Wpf.dll` | 23,552 | `5B74201D3D8EEBB0D2FC3ABC35A1AB08EACFCA4203FBCFD4D1F5727F43EB386B` |
| `Microsoft.Terminal.Control.dll` | 1,653,760 | `1F56A0A3B903BEAB561E7BFBC22CA66221668D801215A969B1A76094ACC30CB5` |
| `WpfTerminalTestNetCore.exe` | 152,576 | `13034DF21F9B30D2678C35E2536A8A0D0AAC363A664387E4E8FDF7C66BE52515` |
| `WpfTerminalTestNetCore.dll` | 11,776 | `DF1F8F4DECFFACAB4B330FFD2CD86CBA1ACCAD9DED9A659E4DC918322BCA5639` |
| `WpfTerminalTestNetCore.deps.json` | 893 | `AD52B787F0C2D9D91152E54F91D1A89C95832B3B5B8E81740C6D2EEF8AE10F0F` |
| `WpfTerminalTestNetCore.runtimeconfig.json` | 429 | `73B37384BB001E78DB2014D328750EB2990393712FD63CD00C0F41965B3DBE96` |

`Microsoft.Terminal.Wpf.dll` imports only `mscoree.dll`. `Microsoft.Terminal.Control.dll` imports Windows CRT API-set DLLs plus `KERNEL32`, `USER32`, `OLEAUT32`, `ole32`, `DWrite`, `d2d1`, `dxgi`, `d3d11`, `D3DCOMPILER_47`, `SHELL32`, UI Automation API sets, and delay-loads `icu.dll` and `UIAutomationCore.dll`. Those are Windows/platform dependencies on the validated target, not extra app-local DLLs discovered by this output. The runtime configuration requires the .NET 8 `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` shared frameworks. The host had no additional app-local native dependency besides `Microsoft.Terminal.Control.dll`.

## Packaging and redistribution boundary

The upstream `WpfTerminalControl.csproj` pack target expects native `Microsoft.Terminal.Control.dll` outputs for `Win32`, `x64`, and `ARM64`. An x64-only build cannot produce that package: `dotnet pack` correctly fails because the Win32 and ARM64 output paths are absent.

For the initial Windows x64 integration, gabCode will use an explicit x64 runtime-asset layout built from the pinned source rather than the upstream multi-architecture pack target. The integration sprint must establish how those versioned assets are produced and consumed before adding the application reference.

The chosen implementation must preserve pinned source/build provenance, include `LICENSE` and `NOTICE.md`, inventory shipped assets, meet the .NET desktop-runtime policy, and remain reproducible before packaging/signing work. Do not rely on an unofficial repackaged control. Building Win32 and ARM64 outputs or using the upstream multi-architecture package remains optional future work, not an integration prerequisite.

## WPF and future ConPTY boundary

The pinned managed public surface is deliberately small:

- `TerminalControl.Connection` accepts `ITerminalConnection`.
- `ITerminalConnection` supplies `Start()`, `WriteInput(string)`, `Resize(uint rows, uint columns)`, `Close()`, and a `TerminalOutput` event.
- `TerminalControl` supplies theme, auto-resize, row/column, selected-text, and explicit resize APIs.
- The wrapper owns an `HwndHost`; the child terminal HWND is created/destroyed by it.

Assigning `TerminalControl.Connection` unsubscribes the prior connection's output event and calls `Start()` on the new connection, but it never calls `Close()` on the prior connection. `TerminalContainer.DestroyWindowCore` destroys only the native terminal object and likewise never calls `Close()`. The pinned wrapper therefore does not own connection or child-process shutdown.

A future **gabCode-owned Windows-only** ConPTY adapter may implement `ITerminalConnection`; the gabCode host must retain that adapter and explicitly close it before replacing or destroying the control. The adapter must own process-tree lifecycle, output callbacks, cancellation, resize, and cleanup. A later approved integration sprint must exercise a real ConPTY shell and prove bounded descendant cleanup; the echo-only upstream sample cannot provide that evidence. Do not expose this upstream type outside a gabCode boundary and do not implement that adapter under this dependency-gate issue.

## Target-Windows capability evidence

| Capability | Result | Evidence / limitation |
| --- | --- | --- |
| Launch and native-host cleanup | PASS | The upstream .NET 8 WPF host launched, closed with exit code 0, and had zero remaining `WpfTerminalTestNetCore` processes. Its echo connection starts no child process, so this result is intentionally limited to the sample process and native HWND/resources. |
| `ITerminalConnection` / child-process cleanup | **ACCEPTED OWNERSHIP BOUNDARY** | The sample uses an echo-only connection whose `Close()` is a no-op. The wrapper invokes neither the old connection's `Close()` on replacement nor the active connection's `Close()` from `DestroyWindowCore`. This is accepted: gabCode owns and closes the ConPTY adapter and processes that gabCode creates. |
| ANSI and Unicode rendering | PASS | Injected `ANSI_RED` and `Unicode probe: Ω 漢字`; both appeared through `TextPattern`. Captured `wtf-001-ansi-unicode-probe.png`. |
| Resize and text retention | PASS | Terminal rect grew `767×411` → `967×611` then shrank to `587×341`; the Unicode marker remained after both resizes. |
| Scrollback | PASS (bounded) | First and last of 60 injected lines remained in `TextPattern`. Pinned `HwndTerminal.cpp` creates the terminal with 9,001 scrollback lines; `Terminal::Create` allocates viewport height plus that finite history. The WPF surface exposes no setting to change this bound. |
| Selection, copy, paste | PASS | Mouse selection exposed nonempty `TextPattern` selection; right-click copied exactly `ECHO CONNECTION`; right-click after placing `PASTE_PROBE` in the clipboard echoed it. |
| UI Automation provider | PASS | Native `WPFTermControl` exposed as focusable `Text Area` (`ControlType.Text`) with `TextPattern` and terminal text. This is UIA-client evidence, not Narrator validation. |
| Live view movement | PASS | A temporary untracked WPF harness moved one `TerminalControl` between two `ContentControl`s. The same managed object and native HWND (`3347802`) remained; `Start()` was called once and the retained marker remained visible. |
| Keyboard entry | PASS | The terminal native HWND had keyboard focus at host launch and accepted injected characters. |
| Keyboard focus escape | **ACCEPTED LIMITATION** | From the focused terminal in the movement harness, `Tab` did not focus the sibling `Escape target` button (`FocusAfterTab: null`, `EscapeTargetFocusedAfterTab: false`). The wrapper forwards `WM_KEYDOWN` and `WM_SYSKEYDOWN` to the terminal; no WPF public escape mechanism was found. Keyboard-only focus escape is not required for the initial product. |
| Search | **UNSUPPORTED / ACCEPTED LIMITATION** | The upstream WinUI terminal source contains search, but `Microsoft.Terminal.Wpf` exposes no search member and the native C exports expose no search operation. gabCode will not provide terminal search unless a future product decision adds it. |
| Hyperlinks | **UNSUPPORTED / ACCEPTED LIMITATION** | The upstream WinUI control contains hyperlink handling, but the pinned WPF wrapper/C exports expose no hyperlink event, retrieval, or activation API. gabCode will not provide terminal hyperlink activation unless a future product decision adds it. |
| Narrator | **NOT CHECKED / ACCEPTED LIMITATION** | UIA tree is present, but Narrator was not running; a UIA client is not equivalent to screen-reader completion. Dedicated Narrator qualification of terminal content is not required. |
| IME | **NOT CHECKED / ACCEPTED LIMITATION** | The source contains TSF-specific handling, but no IME target-machine scenario was run. Dedicated IME qualification is not required. |
| High contrast, text scaling, reduced motion | **NOT CHECKED / ACCEPTED LIMITATION** | Validation machine reported high contrast off and client/menu animation on; no setting-transition or scaled-render scenario was run. Dedicated qualification of the upstream terminal surface is not required. |

The screenshot and probe JSON are local untracked evidence under `.pi/tmp/`; they are not shipped artifacts.

## Approval and integration boundary

The human product owner approved this dependency after reviewing the gate evidence. Integration may proceed with these accepted limitations:

- no required keyboard-only focus escape from terminal content;
- no terminal search;
- no terminal hyperlink activation;
- no dedicated Narrator, IME, high-contrast, text-scaling, or reduced-motion qualification of the upstream terminal content surface;
- no expectation that the upstream wrapper automatically closes gabCode's connection or child processes.

GabCode still owns ordinary lifecycle for resources that gabCode creates: the ConPTY adapter, shell processes, output readers, cancellation, and descendant cleanup. This responsibility does not require modifying the upstream control. Integration must use the exact pin and approved x64 asset strategy, preserve license/notice material, retain the same live terminal view during layout movement, and avoid unofficial packages or silent upstream patches.

**Final dependency verdict: GO — approved for Windows x64 integration with accepted limitations.**
