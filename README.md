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

## License

gabCode is available under the [MIT License](LICENSE).
