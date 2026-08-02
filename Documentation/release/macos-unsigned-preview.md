# macOS unsigned developer preview

This document covers `gabCode-0.0.1-preview.1-macos-arm64.dmg`, an unsupported preview for trusted developers.

## Supported target

- Apple Silicon (`arm64`) only
- macOS 26.0 or later
- Ordinary per-user application launch; no elevation or background installer

This artifact is ad-hoc signed only. It is **not** Developer ID signed, notarized, stapled, App Store distributed, or qualified as a trusted production release. The bundle identifier remains the existing unresolved preview identity, `com.gabrewer.gabcode`.

## Verify the known artifact

Obtain the SHA-256 value through the release's `SHA256SUMS.txt` or another trusted channel, then run:

```bash
shasum -a 256 gabCode-0.0.1-preview.1-macos-arm64.dmg
hdiutil verify gabCode-0.0.1-preview.1-macos-arm64.dmg
```

Continue only when the checksum exactly matches the trusted value. Do not use the approval steps below for an unknown or mismatched download.

Repository maintainers can run the complete read-only artifact verifier from the repository root:

```bash
./eng/release/macos/test-preview.sh \
  artifacts/v0.0.1-preview.1/gabCode-0.0.1-preview.1-macos-arm64.dmg
```

## Install or replace

1. Quit an existing gabCode preview. If terminals are active, choose **Close and Stop Terminals** only after saving terminal work.
2. Open `gabCode-0.0.1-preview.1-macos-arm64.dmg` in Finder.
3. Drag `gabCode.app` onto the **Applications** folder target.
4. If Finder asks whether to replace the existing application, confirm only after checking that the mounted DMG is the verified artifact.
5. Eject the `gabCode 0.0.1 Preview 1` volume when copying finishes.

The drag-copy does not install a service, updater, privileged helper, shell configuration, or repository file. Replacing the application replaces only `/Applications/gabCode.app`.

## First launch and Gatekeeper

Because this preview is not Developer ID signed or notarized, normal launch is rejected by Gatekeeper. On the target macOS 26.5.2 machine, a quarantined copy with the published SHA-256 produced this observed path:

1. Finder launch displayed **“gabCode.app” Not Opened** and stated that Apple could not verify the app is free of malware.
2. Choosing **Done**, then opening **System Settings → Privacy & Security**, showed **“gabCode.app” was blocked to protect your Mac**.
3. Choosing **Open Anyway** displayed a second **Open “gabCode.app”?** warning with **Move to Trash**, **Open Anyway**, and **Done** actions.
4. Confirming **Open Anyway** required an administrator username and password. An authorized human must review the app and enter those credentials; automated release tooling must not supply or store them.
5. After authorization, launch gabCode again from Applications with the controlled directory argument below if it did not remain open.

The final credential entry and post-approval launch were `NOT CHECKED` during automated task execution because no administrator credentials were provided. macOS policy and wording can vary by security settings. Do not globally disable Gatekeeper, run `spctl --master-disable`, or remove quarantine metadata as an installation instruction.

On launch, the current prototype requires an explicit controlled worktree directory argument. From Terminal:

```bash
open -a /Applications/gabCode.app --args \
  --terminal-directory '/absolute/path/to/controlled/worktree'
```

The prototype opens two ordinary login-shell terminals rooted in that directory. gabCode does not start or interpret Pi.

## Remove

1. Quit gabCode. Confirm terminal shutdown if prompted and verify any shell work has finished.
2. Eject any mounted gabCode preview DMG in Finder.
3. Move `/Applications/gabCode.app` to Trash.
4. Empty Trash when appropriate.

Removal deletes only the application bundle. This preview has no uninstaller and does not delete repositories, worktrees, shell files, or user data outside the bundle.

## Troubleshooting

- **Checksum mismatch:** delete the DMG and obtain it again from the approved release. Do not launch it.
- **“Apple could not verify…” / unidentified developer:** use the one-time path above only after checksum verification. This result is expected for this preview.
- **Damaged or unreadable DMG:** run `hdiutil verify`; download again if verification fails.
- **Wrong architecture:** this artifact is `arm64` only and will not support Intel Macs.
- **No terminal workspace appears:** launch with an existing, readable controlled directory through `--terminal-directory` as shown above.
- **A terminal remains after exit:** stop and report the process details; clean process-group shutdown is required and must not be represented as successful.
- **DMG will not eject:** close Finder windows and any processes using the volume, then eject it in Finder or Disk Utility. Do not leave verification mounts attached.

## Build the preview

On the declared target Mac, from the repository root:

```bash
./eng/release/macos/build-preview.sh \
  0.0.1-preview.1 \
  artifacts/v0.0.1-preview.1
```

The build uses a temporary clean DerivedData directory, forces an arm64 Release bundle with marketing version `0.0.1` and build number `1`, signs nested code before the containing app with an ad-hoc identity, assembles the four-item DMG root, verifies the image, and prints its SHA-256 value. It does not select a signing team or permanently change project publisher settings.

The DMG includes gabCode's MIT license and the SwiftTerm 1.15.0 notice pinned at revision `dd2fb8ac5b861e7bf617c872895e338f38165648`.

## Native Accessibility Assessment

- **Keyboard-only:** NOT CHECKED — Finder `Command-O` reached the expected Gatekeeper rejection with Full Keyboard Access enabled (`AppleKeyboardUIMode=2`), but keyboard-only drag/copy installation was not completed because the terminal host was not granted Automation control of Finder.
- **Focus behavior:** PASS for the installed app's terminal focus commands and close confirmation; Gatekeeper administrator-authentication focus is `NOT CHECKED`.
- **Accessibility tree/screen reader:** NOT CHECKED — VoiceOver installation and launch exercise requires a human.
- **Dynamic status and errors:** PASS for the native Gatekeeper rejection and two-stage warning text; post-credential result is `NOT CHECKED`.
- **Contrast/scaling/reduced motion:** NOT CHECKED — human target-machine exercise is required.
- **Terminal integration:** PASS — an installed non-quarantined copy launched two ordinary shells in a controlled spaces-and-Unicode repository, accepted independent commands, presented active-terminal close confirmation, and left no installed-app process or shell after confirmed exit.
- **Human target-machine validation still needed:** yes — complete administrator-approved Gatekeeper launch, keyboard-only Finder copy, VoiceOver, contrast, scaling, and reduced-motion checks.
