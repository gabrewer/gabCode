[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.\d+$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NativeSuccess([string] $Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function New-StableGuid([string] $Value) {
    $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Value))
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($digest, $guidBytes, $guidBytes.Length)
    return ([Guid]::new($guidBytes)).ToString('B').ToUpperInvariant()
}

function Test-ContainsBytes([byte[]] $Bytes, [byte[]] $Pattern) {
    if ($Pattern.Length -eq 0 -or $Bytes.Length -lt $Pattern.Length) {
        return $false
    }

    for ($offset = 0; $offset -le $Bytes.Length - $Pattern.Length; $offset++) {
        if ($Bytes[$offset] -ne $Pattern[0]) {
            continue
        }

        $matches = $true
        for ($index = 1; $index -lt $Pattern.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $Pattern[$index]) {
                $matches = $false
                break
            }
        }

        if ($matches) {
            return $true
        }
    }

    return $false
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sourcePath = Join-Path $PSScriptRoot 'GabCode.Preview.wxs'
$applicationProject = Join-Path $repositoryRoot 'src\GabCode.Windows\GabCode.Windows.csproj'
$terminalRoot = Join-Path $repositoryRoot 'third_party\microsoft-terminal\v1.24.11911.0'
$terminalManifestPath = Join-Path $terminalRoot 'manifest.json'
$versionMatch = [regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)-preview\.(?<preview>\d+)$')
$numericVersion = '{0}.{1}.{2}' -f $versionMatch.Groups['major'].Value, $versionMatch.Groups['minor'].Value, $versionMatch.Groups['build'].Value
$versionParts = $numericVersion.Split('.') | ForEach-Object { [int] $_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Windows MSI version '$numericVersion' exceeds the supported 255.255.65535 range."
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$artifactName = "gabCode-$Version-windows-x64.msi"
$packagePath = Join-Path $outputPath $artifactName

if (Test-Path -LiteralPath $outputPath) {
    $existingEntries = @(Get-ChildItem -LiteralPath $outputPath -Force)
    $unexpectedEntries = @($existingEntries | Where-Object { $_.Name -ne $artifactName })
    if ($unexpectedEntries.Count -ne 0) {
        $names = $unexpectedEntries.Name -join ', '
        throw "Output directory is not clean. Refusing to remove unrelated entries from '$outputPath': $names"
    }

    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
}
else {
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
}

$temporaryRoot = Join-Path $repositoryRoot ('.pi\tmp\windows-preview-build\{0}-{1}' -f $Version, [Guid]::NewGuid().ToString('N'))
$publishPath = Join-Path $temporaryRoot 'publish'
$intermediatePath = Join-Path $temporaryRoot 'wix-obj'
$generatedSourcePath = Join-Path $temporaryRoot 'GabCode.Preview.generated.wxs'

try {
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
    New-Item -ItemType Directory -Path $intermediatePath -Force | Out-Null

    Push-Location $repositoryRoot
    try {
        & dotnet tool restore
        Assert-NativeSuccess 'dotnet tool restore'

        & dotnet publish $applicationProject `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --output $publishPath `
            -p:ContinuousIntegrationBuild=true `
            -p:Deterministic=true `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:PublishSingleFile=false `
            -p:UseAppHost=true `
            -p:Version=$Version
        Assert-NativeSuccess 'dotnet publish'
    }
    finally {
        Pop-Location
    }

    $licenseDirectory = Join-Path $publishPath 'licenses'
    $gabCodeLicenseDirectory = Join-Path $licenseDirectory 'gabCode'
    $terminalLicenseDirectory = Join-Path $licenseDirectory 'microsoft-terminal'
    New-Item -ItemType Directory -Path $gabCodeLicenseDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $terminalLicenseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $gabCodeLicenseDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $terminalRoot 'LICENSE') -Destination (Join-Path $terminalLicenseDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $terminalRoot 'NOTICE.md') -Destination (Join-Path $terminalLicenseDirectory 'NOTICE.md')

    $requiredRuntimeFiles = @(
        'GabCode.Windows.exe',
        'GabCode.Windows.dll',
        'GabCode.Windows.runtimeconfig.json',
        'coreclr.dll',
        'hostfxr.dll',
        'PresentationFramework.dll',
        'Microsoft.Terminal.Wpf.dll',
        'Microsoft.Terminal.Control.dll',
        'licenses\gabCode\LICENSE.txt',
        'licenses\microsoft-terminal\LICENSE.txt',
        'licenses\microsoft-terminal\NOTICE.md'
    )
    foreach ($relativePath in $requiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
            throw "Self-contained publish is missing required payload file '$relativePath'."
        }
    }

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File | Where-Object {
        $_.Extension -in @('.pdb', '.cs', '.csproj', '.sln', '.slnx', '.nupkg', '.snupkg', '.wixpdb', '.tmp', '.cache') -or
        $_.Name -match '(?i)testhost|\.Tests\.'
    })
    if ($forbiddenFiles.Count -ne 0) {
        throw "Publish payload contains forbidden build/test material: $($forbiddenFiles.FullName -join ', ')"
    }

    $terminalManifest = Get-Content -LiteralPath $terminalManifestPath -Raw | ConvertFrom-Json
    foreach ($asset in $terminalManifest.assets) {
        $publishedAsset = Join-Path $publishPath $asset.file
        $publishedHash = (Get-FileHash -LiteralPath $publishedAsset -Algorithm SHA256).Hash
        if ($publishedHash -ne $asset.sha256 -or (Get-Item -LiteralPath $publishedAsset).Length -ne $asset.bytes) {
            throw "Published terminal asset '$($asset.file)' does not match the pinned manifest."
        }
    }

    $pathPatterns = @(
        [System.Text.Encoding]::UTF8.GetBytes($repositoryRoot),
        [System.Text.Encoding]::Unicode.GetBytes($repositoryRoot)
    )
    foreach ($file in Get-ChildItem -LiteralPath $publishPath -Recurse -File) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        foreach ($pattern in $pathPatterns) {
            if (Test-ContainsBytes -Bytes $bytes -Pattern $pattern) {
                throw "Published file '$($file.FullName)' contains the build-machine repository path."
            }
        }
    }

    [xml] $wixDocument = Get-Content -LiteralPath $sourcePath -Raw
    $namespace = [System.Xml.XmlNamespaceManager]::new($wixDocument.NameTable)
    $namespace.AddNamespace('wix', 'http://wixtoolset.org/schemas/v4/wxs')
    $package = $wixDocument.SelectSingleNode('/wix:Wix/wix:Package', $namespace)
    $package.SetAttribute('Version', $numericVersion)
    $package.SetAttribute('ProductCode', (New-StableGuid "gabCode/windows-x64/product/$Version"))
    $xmlSettings = [System.Xml.XmlWriterSettings]::new()
    $xmlSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $xmlSettings.Indent = $true
    $xmlWriter = [System.Xml.XmlWriter]::Create($generatedSourcePath, $xmlSettings)
    try {
        $wixDocument.Save($xmlWriter)
    }
    finally {
        $xmlWriter.Dispose()
    }

    Push-Location $repositoryRoot
    try {
        & dotnet wix build -acceptEula wix7 $generatedSourcePath `
            -arch x64 `
            -bindpath "Publish=$publishPath" `
            -intermediatefolder $intermediatePath `
            -pdbtype none `
            -out $packagePath
        Assert-NativeSuccess 'wix build'
    }
    finally {
        Pop-Location
    }

    $outputEntries = @(Get-ChildItem -LiteralPath $outputPath -Force)
    if ($outputEntries.Count -ne 1 -or $outputEntries[0].FullName -ne (Get-Item -LiteralPath $packagePath).FullName) {
        throw "Packaging output must contain exactly '$artifactName'."
    }

    $hash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    Write-Output "Package: $packagePath"
    Write-Output "SHA256: $($hash.Hash)"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
