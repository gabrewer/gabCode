# gabCode Application Icons PRD

| Field | Value |
| --- | --- |
| Status | Approved |
| Date | 2026-08-02 |
| Approval | Human approval recorded in GitHub issue #38 on 2026-08-02 |

## Product Name & One-Liner

**gabCode native application icons** — Give gabCode a consistent, recognizable icon wherever Windows and macOS present the installed application.

## Problem & Audience

A developer installing or launching gabCode should be able to recognize it immediately in the Windows Start menu, Windows Apps list, Finder, Dock, Launchpad, and the macOS DMG. Today the projects have icon-ready assets but do not consistently bind them to their platform-native application and package surfaces.

The audience is the individual developer using gabCode locally on Windows or macOS.

## Core Features

1. **Windows executable icon** — Embed the supplied multi-resolution `.ico` in the WPF executable so Windows uses gabCode’s icon for the application. **Must-have**
2. **Windows installer identity** — Use the same icon for the existing MSI’s Start-menu shortcut and installed-apps entry where WiX supports it. **Must-have**
3. **macOS application icon** — Populate the existing `AppIcon.appiconset` with the supplied PNG representations, correctly mapped to Apple’s required point-size and scale slots. **Must-have**
4. **macOS bundle and DMG visibility** — Preserve the existing Xcode AppIcon configuration so the built `.app`, and therefore the staged app in the preview DMG, displays gabCode’s icon in Finder, Dock, and Launchpad. **Must-have**
5. **Source-owned assets** — Copy only the platform-required assets into their respective application source trees; neither platform build depends on the sibling `gabcode-icon-assets` directory. **Must-have**
6. **Packaging regression coverage** — Extend deterministic project/package checks to prove each platform configuration references its source-owned icon assets. **Should-have**

## Non-Goals

- Redesigning the gabCode visual identity or generating new icon artwork.
- Adding application-logo imagery to terminal content, commands, or other ordinary UI controls.
- Creating a Windows desktop shortcut; the preview installer intentionally does not create one.
- Adding App Store, Windows Store, code-signing, notarization, or release-distribution work.
- Making the external `gabcode-icon-assets` directory a runtime or build dependency.

## Technical Considerations

- Copy `gabcode.ico` to `src/GabCode.Windows` and configure the WPF project’s application-icon property.
- Copy the needed supplied PNGs to `src/GabCode.MacOS/gabCode/Assets.xcassets/AppIcon.appiconset` and add the matching filenames to `Contents.json`. The existing Xcode project already sets `ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon`.
- Map Apple asset-catalog slots as follows: 16pt 1x/2x uses 16/32px; 32pt uses 32/64px; 128pt uses 128/256px; 256pt uses 256/512px; and 512pt uses 512/1024px.
- Update the WiX preview installer to reference the copied Windows icon for the Start-menu shortcut and Add/Remove Programs icon, without changing its one-Start-menu-shortcut/no-desktop-shortcut behavior.
- Validate Windows behavior on Windows and macOS bundle behavior on the declared Apple Silicon macOS target. If either target is unavailable, report it as **NOT CHECKED** rather than asserting native visual evidence.

## Milestones

1. **Asset ownership** — Copy the required Windows and macOS icon assets into their application project locations.
2. **Native project wiring** — Configure WPF, the macOS asset catalog, and WiX to use those assets in their native identity surfaces.
3. **Automated verification** — Add or update deterministic configuration/package tests and run the supported build/test surfaces.
4. **Target-platform evidence** — Verify the Windows executable/MSI and macOS app/DMG icon behavior on their respective native systems, recording unavailable checks as NOT CHECKED.

## Open Questions

- None for the initial implementation. Native visual verification is contingent on access to both target operating systems.
