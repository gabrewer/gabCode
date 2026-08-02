[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.\d+$')]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NativeSuccess([string] $Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Assert-RegularFile([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing or is not a regular file: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must not be a link or reparse point: $Path"
    }
}

function Assert-PreparedPair(
    [string] $Platform,
    [string] $ArtifactPath,
    [string] $EvidencePath,
    [string] $ExpectedArtifactName,
    [string] $ExpectedEvidenceName,
    [string] $ExpectedVersion,
    [string] $ExpectedCommit
) {
    $artifactExists = Test-Path -LiteralPath $ArtifactPath
    $evidenceExists = Test-Path -LiteralPath $EvidencePath
    if (-not $artifactExists -and -not $evidenceExists) {
        return $false
    }
    if ($artifactExists -ne $evidenceExists) {
        throw "Prepared $Platform output is partial; both '$ExpectedArtifactName' and '$ExpectedEvidenceName' are required."
    }

    Assert-RegularFile $ArtifactPath "$Platform artifact"
    Assert-RegularFile $EvidencePath "$Platform evidence"
    $artifact = Get-Item -LiteralPath $ArtifactPath
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($evidence.schemaVersion -ne 1 -or
        $evidence.platform -cne $Platform -or
        $evidence.version -cne $ExpectedVersion -or
        $evidence.sourceCommit -cne $ExpectedCommit -or
        $evidence.evidenceFileName -cne $ExpectedEvidenceName -or
        $evidence.artifact.fileName -cne $ExpectedArtifactName -or
        [long] $evidence.artifact.bytes -ne $artifact.Length -or
        $evidence.artifact.sha256 -cne $hash -or
        $evidence.verification.status -cne 'PASS') {
        throw "Prepared $Platform artifact/evidence does not match the requested version, source commit, or recomputed file facts."
    }

    return $true
}

function Get-ReviewedSourceCommit([string] $Root, [bool] $Fetch) {
    Push-Location $Root
    try {
        if ($Fetch) {
            & git fetch origin main
            Assert-NativeSuccess 'git fetch origin main'
        }

        $status = @(& git status --porcelain --untracked-files=all)
        Assert-NativeSuccess 'git status'
        if ($status.Count -ne 0) {
            throw "Working tree must contain no tracked or untracked non-ignored changes before preview preparation: $($status -join '; ')"
        }

        $headCommit = (& git rev-parse HEAD).Trim().ToLowerInvariant()
        Assert-NativeSuccess 'git rev-parse HEAD'
        $mainCommit = (& git rev-parse origin/main).Trim().ToLowerInvariant()
        Assert-NativeSuccess 'git rev-parse origin/main'
        if ($headCommit -cne $mainCommit) {
            throw "Preview preparation requires HEAD to equal reviewed origin/main. HEAD=$headCommit origin/main=$mainCommit"
        }

        return $headCommit
    }
    finally {
        Pop-Location
    }
}

if (-not $IsWindows) {
    throw 'Windows is required to prepare the gabCode MSI.'
}
if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Windows x64 is required to prepare the gabCode MSI.'
}

$versionMatch = [regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)-preview\.(?<preview>\d+)$')
$componentValues = [System.Collections.Generic.List[uint64]]::new()
foreach ($componentName in @('major', 'minor', 'build', 'preview')) {
    [uint64] $componentValue = 0
    if (-not [uint64]::TryParse($versionMatch.Groups[$componentName].Value, [ref] $componentValue)) {
        throw "Preview version component '$componentName' is outside the supported unsigned integer range."
    }
    $componentValues.Add($componentValue)
}
if ($componentValues[3] -eq 0) {
    throw 'Preview ordinal must be a positive integer.'
}
if ($componentValues[0] -gt 255 -or $componentValues[1] -gt 255 -or $componentValues[2] -gt 65535) {
    throw "Windows MSI version exceeds the supported 255.255.65535 range: $Version"
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$schemaPath = Join-Path $repositoryRoot 'eng\release\preview-evidence.schema.json'
Assert-RegularFile $schemaPath 'Preview evidence schema'
$schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
if ($schema.'$schema' -cne 'https://json-schema.org/draft/2020-12/schema') {
    throw 'Preview evidence schema is not the reviewed draft 2020-12 contract.'
}

$sourceCommit = Get-ReviewedSourceCommit $repositoryRoot $true
$artifactName = "gabCode-$Version-windows-x64.msi"
$evidenceName = "gabCode-$Version-windows-x64.evidence.json"
$macArtifactName = "gabCode-$Version-macos-arm64.dmg"
$macEvidenceName = "gabCode-$Version-macos-arm64.evidence.json"
$outputPath = Join-Path $repositoryRoot "artifacts\v$Version"
$artifactPath = Join-Path $outputPath $artifactName
$evidencePath = Join-Path $outputPath $evidenceName
$macArtifactPath = Join-Path $outputPath $macArtifactName
$macEvidencePath = Join-Path $outputPath $macEvidenceName

if (Test-Path -LiteralPath $outputPath) {
    $outputItem = Get-Item -LiteralPath $outputPath -Force
    if (-not $outputItem.PSIsContainer -or ($outputItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Artifact output must be a real directory: $outputPath"
    }

    $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @($artifactName, $evidenceName, $macArtifactName, $macEvidenceName)) {
        [void] $allowedNames.Add($name)
    }
    $unexpected = @(Get-ChildItem -LiteralPath $outputPath -Force | Where-Object { -not $allowedNames.Contains($_.Name) })
    if ($unexpected.Count -ne 0) {
        throw "Artifact directory contains unknown entries; refusing to remove them: $($unexpected.Name -join ', ')"
    }
}

[void] (Assert-PreparedPair 'macos' $macArtifactPath $macEvidencePath $macArtifactName $macEvidenceName $Version $sourceCommit)
$windowsPairExists = Assert-PreparedPair 'windows' $artifactPath $evidencePath $artifactName $evidenceName $Version $sourceCommit
if ($windowsPairExists) {
    Write-Output 'Matching Windows preview artifact/evidence already exists and passed recomputation.'
    Write-Output "Artifact: $artifactPath"
    Write-Output "Evidence: $evidencePath"
    return
}

$temporaryRoot = Join-Path $repositoryRoot ('.pi\tmp\windows-preview-prepare\{0}-{1}' -f $Version, [Guid]::NewGuid().ToString('N'))
$packageOutput = Join-Path $temporaryRoot 'package'
$verificationOutput = Join-Path $temporaryRoot 'verification'
$temporaryEvidencePath = Join-Path $temporaryRoot $evidenceName
$temporaryArtifactPath = Join-Path $packageOutput $artifactName

try {
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
    New-Item -ItemType Directory -Path $verificationOutput -Force | Out-Null

    Push-Location $repositoryRoot
    try {
        & dotnet tool restore
        Assert-NativeSuccess 'dotnet tool restore'
        & dotnet restore GabCode.slnx
        Assert-NativeSuccess 'dotnet restore'
        & dotnet build GabCode.slnx --configuration Release --no-restore
        Assert-NativeSuccess 'dotnet build'
        & dotnet test GabCode.slnx --configuration Release --no-build
        Assert-NativeSuccess 'dotnet test'
    }
    finally {
        Pop-Location
    }

    & (Join-Path $PSScriptRoot 'Build-Preview.ps1') -Version $Version -OutputDirectory $packageOutput
    if (-not $?) { throw 'Build-Preview.ps1 failed.' }
    & (Join-Path $PSScriptRoot 'Test-Preview.ps1') -PackagePath $temporaryArtifactPath -EvidenceDirectory $verificationOutput
    if (-not $?) { throw 'Test-Preview.ps1 failed.' }

    Assert-RegularFile (Join-Path $verificationOutput 'artifact-report.json') 'Windows artifact verification report'
    $sourceCommitAfterBuild = Get-ReviewedSourceCommit $repositoryRoot $false
    if ($sourceCommitAfterBuild -cne $sourceCommit) {
        throw 'Source commit changed during Windows preview preparation.'
    }

    $artifact = Get-Item -LiteralPath $temporaryArtifactPath
    $hash = (Get-FileHash -LiteralPath $temporaryArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $dotnetVersion = (& dotnet --version).Trim()
    Assert-NativeSuccess 'dotnet --version'
    $evidence = [ordered]@{
        schemaVersion = 1
        platform = 'windows'
        version = $Version
        sourceCommit = $sourceCommit
        evidenceFileName = $evidenceName
        artifact = [ordered]@{
            fileName = $artifactName
            bytes = $artifact.Length
            sha256 = $hash
        }
        toolchain = [ordered]@{
            operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = 'x64'
            buildTool = "dotnet $dotnetVersion; PowerShell $($PSVersionTable.PSVersion); WiX 7.0.0"
        }
        verification = [ordered]@{
            status = 'PASS'
            checks = @(
                'solution-build-and-tests',
                'unsigned-msi-identity-and-payload',
                'install-repair-upgrade-uninstall',
                'repository-and-user-data-sentinels',
                'process-and-install-cleanup'
            )
            completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporaryEvidencePath -Encoding utf8NoBOM
    [void] (Assert-PreparedPair 'windows' $temporaryArtifactPath $temporaryEvidencePath $artifactName $evidenceName $Version $sourceCommit)

    if (-not (Test-Path -LiteralPath $outputPath)) {
        New-Item -ItemType Directory -Path $outputPath | Out-Null
    }
    try {
        Move-Item -LiteralPath $temporaryArtifactPath -Destination $artifactPath
        Move-Item -LiteralPath $temporaryEvidencePath -Destination $evidencePath
    }
    catch {
        if ((Test-Path -LiteralPath $artifactPath) -and -not (Test-Path -LiteralPath $evidencePath)) {
            Remove-Item -LiteralPath $artifactPath -Force
        }
        throw
    }

    Write-Output 'Windows preview preparation passed.'
    Write-Output "Artifact: $artifactPath"
    Write-Output "Evidence: $evidencePath"
    Write-Output "SHA256: $hash"
    Write-Output 'Transfer both files together; no GitHub issue, tag, or release was created.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
