# gabCode

A native worktree navigator for developers using Pi, Git, GitHub, and VS Code.

gabCode is being designed and built in the open. The project is currently in its initial product-definition stage.

## Product direction

Read the [initial product requirements document](Documentation/design/gabcode-initial-prd.md) for the product boundary, architecture direction, and planned milestones.

## Windows development

The initial Windows client uses C#, .NET 10, and WPF. The repository's `global.json` pins the required SDK feature band.

Prerequisites:

- Windows
- .NET SDK 10.0.302 or a later patch in the 10.0.3xx feature band

From the repository root:

```powershell
dotnet restore GabCode.slnx
dotnet build GabCode.slnx --configuration Release --no-restore
dotnet test GabCode.slnx --configuration Release --no-build
dotnet run --project src/GabCode.Windows/GabCode.Windows.csproj
```

Visual Studio is optional for this command-line workflow.

### Windows Terminal WPF dependency

The pinned Windows Terminal WPF control is evaluated separately from the current app bootstrap. Its dependency gate is **GO for Windows x64 integration with accepted limitations**. The upstream x64 Release build is reproducible with the v143 desktop and UWP C++ tools plus Windows SDK 10.0.22621.0. See the [dependency record](Documentation/dependencies/windows-terminal-wpf.md) for the exact source pin, build prerequisites, accepted limitations, and integration boundary; do not substitute an unofficial package.

## macOS development

The initial macOS client uses SwiftUI and Swift. Its baseline is an Apple Silicon Mac running macOS 26.0 or later with Xcode 26.6, the macOS 26.5 SDK, and Swift 6.3.3.

Prerequisites:

- Apple Silicon Mac running macOS 26.0 or later
- Full Xcode 26.6 selected through `xcode-select` (not Command Line Tools)
- macOS 26.5 SDK and Swift 6.3.3 supplied by the selected Xcode installation

From the repository root, verify the selected toolchain and discover the shared scheme:

```bash
xcode-select -p
xcodebuild -version
xcrun --sdk macosx --show-sdk-version
xcrun swift --version
xcodebuild -list -project src/GabCode.MacOS/gabCode.xcodeproj
```

Build and test the native app:

```bash
xcodebuild -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode -configuration Debug -destination 'platform=macOS' build
xcodebuild -project src/GabCode.MacOS/gabCode.xcodeproj -scheme gabCode -configuration Debug -destination 'platform=macOS' test
```

Launch through Xcode without embedding a machine-specific Xcode path in the repository:

```bash
open src/GabCode.MacOS/gabCode.xcodeproj
```

Select the shared `gabCode` scheme and **My Mac** destination, then press `Command-R`.

### Unsigned Apple Silicon preview packaging

The reproducible ad-hoc/non-notarized preview packaging surface is documented in [macOS unsigned developer preview](Documentation/release/macos-unsigned-preview.md). On the declared target Mac:

```bash
./eng/release/macos/build-preview.sh \
  0.0.1-preview.1 \
  artifacts/v0.0.1-preview.1
./eng/release/macos/test-preview.sh \
  artifacts/v0.0.1-preview.1/gabCode-0.0.1-preview.1-macos-arm64.dmg
```

## License

gabCode is available under the [MIT License](LICENSE).
