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

## License

gabCode is available under the [MIT License](LICENSE).
