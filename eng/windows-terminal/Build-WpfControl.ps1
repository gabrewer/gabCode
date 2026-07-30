[CmdletBinding()]
param(
    [switch]$Verify,
    [string]$CheckoutPath,
    [switch]$KeepCheckout
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tag = 'v1.24.11911.0'
$commit = '5a830b2bf7c053d5c7ac22208fe5a346cb5dd3dc'
$repository = 'https://github.com/microsoft/terminal.git'
$manifestUpstream = 'https://github.com/microsoft/terminal'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assetRoot = Join-Path $repoRoot "third_party\microsoft-terminal\$tag"
$expectedProvenanceFiles = @(
    [pscustomobject]@{
        File = 'LICENSE'
        RelativePath = 'LICENSE'
        Bytes = 1116L
        Sha256 = '5D177F23ECFEB0EA8E050B6A5A16355E1AE9A0B286436CA8F83ED08B3795BE6B'
    },
    [pscustomobject]@{
        File = 'NOTICE.md'
        RelativePath = 'NOTICE.md'
        Bytes = 23176L
        Sha256 = 'E7FBAADEE6AB20C28B87730A510EE5F5815D8FB4BD88D1D54D282DC2A74C0726'
    }
)
$expectedAssets = @(
    [pscustomobject]@{
        File = 'Microsoft.Terminal.Wpf.dll'
        RelativePath = 'win-x64\Microsoft.Terminal.Wpf.dll'
        ManifestPath = 'win-x64/Microsoft.Terminal.Wpf.dll'
        Bytes = 23552L
        Sha256 = '5B74201D3D8EEBB0D2FC3ABC35A1AB08EACFCA4203FBCFD4D1F5727F43EB386B'
    },
    [pscustomobject]@{
        File = 'Microsoft.Terminal.Control.dll'
        RelativePath = 'win-x64\Microsoft.Terminal.Control.dll'
        ManifestPath = 'win-x64/Microsoft.Terminal.Control.dll'
        Bytes = 1653760L
        Sha256 = '1F56A0A3B903BEAB561E7BFBC22CA66221668D801215A969B1A76094ACC30CB5'
    }
)

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter()] [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function Assert-AssetLayout {
    if (-not (Test-Path -LiteralPath $assetRoot -PathType Container)) {
        throw "Terminal asset directory is missing: $assetRoot"
    }

    $manifestPath = Join-Path $assetRoot 'manifest.json'
    $licensePath = Join-Path $assetRoot 'LICENSE'
    $noticePath = Join-Path $assetRoot 'NOTICE.md'
    foreach ($requiredPath in @($manifestPath, $licensePath, $noticePath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required terminal provenance file is missing: $requiredPath"
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.upstream -ne $manifestUpstream -or
        $manifest.tag -ne $tag -or
        $manifest.commit -ne $commit -or
        $manifest.license -ne 'MIT' -or
        $manifest.configuration -ne 'Release' -or
        $manifest.platform -ne 'x64' -or
        $manifest.windowsSdk -ne '10.0.22621.0' -or
        $manifest.platformToolset -ne 'v143') {
        throw 'Terminal manifest identity does not match the approved dependency.'
    }

    foreach ($expected in $expectedProvenanceFiles) {
        $path = Join-Path $assetRoot $expected.RelativePath
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $manifestFile = @($manifest.provenanceFiles | Where-Object file -eq $expected.File)
        if ($manifestFile.Count -ne 1) {
            throw "Manifest must contain exactly one provenance entry for $($expected.File)."
        }

        if ($item.Length -ne $expected.Bytes -or $hash -ne $expected.Sha256 -or
            $manifestFile[0].path -ne $expected.RelativePath -or
            [long]$manifestFile[0].bytes -ne $expected.Bytes -or
            $manifestFile[0].sha256 -ne $expected.Sha256) {
            throw "Provenance file or manifest entry does not match the approved bytes/hash: $($expected.File)"
        }
    }

    foreach ($expected in $expectedAssets) {
        $path = Join-Path $assetRoot $expected.RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required terminal runtime asset is missing: $path"
        }

        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $manifestAsset = @($manifest.assets | Where-Object file -eq $expected.File)
        if ($manifestAsset.Count -ne 1) {
            throw "Manifest must contain exactly one entry for $($expected.File)."
        }

        if ($item.Length -ne $expected.Bytes -or $hash -ne $expected.Sha256 -or
            $manifestAsset[0].path -ne $expected.ManifestPath -or
            [long]$manifestAsset[0].bytes -ne $expected.Bytes -or
            $manifestAsset[0].sha256 -ne $expected.Sha256) {
            throw "Runtime asset or manifest entry does not match the approved path/bytes/hash: $($expected.File)"
        }
    }

    $allowedRelativePaths = @(
        'LICENSE',
        'NOTICE.md',
        'manifest.json',
        'win-x64\Microsoft.Terminal.Wpf.dll',
        'win-x64\Microsoft.Terminal.Control.dll'
    )
    $actualRelativePaths = Get-ChildItem -LiteralPath $assetRoot -Recurse -File |
        ForEach-Object { [IO.Path]::GetRelativePath($assetRoot, $_.FullName) }
    $unexpected = @($actualRelativePaths | Where-Object { $_ -notin $allowedRelativePaths })
    if ($unexpected.Count -ne 0) {
        throw "Unexpected files exist in the approved terminal asset layout: $($unexpected -join ', ')"
    }

    Write-Host "Verified Windows Terminal WPF assets for $tag ($commit)." -ForegroundColor Green
}

if ($Verify) {
    Assert-AssetLayout
    exit 0
}

if (-not $IsWindows) {
    throw 'Windows Terminal WPF assets can only be regenerated on Windows.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
foreach ($component in @(
    'Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64',
    'Microsoft.VisualStudio.ComponentGroup.UWP.VC.v143'
)) {
    $installationPath = & $vswhere -products * -requires $component -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Required Visual Studio component is missing: $component"
    }
}

$createdCheckout = $false
if ([string]::IsNullOrWhiteSpace($CheckoutPath)) {
    $driveRoot = [IO.Path]::GetPathRoot($repoRoot)
    $CheckoutPath = Join-Path $driveRoot ("gabcode-wt-{0}" -f ([guid]::NewGuid().ToString('N').Substring(0, 8)))
    Invoke-CheckedNative -FilePath 'git' -ArgumentList @('clone', '--branch', $tag, '--depth', '1', $repository, $CheckoutPath)
    $createdCheckout = $true
}

$checkout = (Resolve-Path $CheckoutPath).Path
try {
    $actualCommit = (& git -C $checkout rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $commit) {
        throw "Checkout commit '$actualCommit' does not match approved commit '$commit'."
    }

    Invoke-CheckedNative -FilePath 'git' -ArgumentList @('-C', $checkout, 'submodule', 'update', '--init', '--recursive')
    if (-not [string]::IsNullOrWhiteSpace((& git -C $checkout status --short --untracked-files=no))) {
        throw 'Pinned checkout contains tracked modifications before the build.'
    }

    Push-Location $checkout
    try {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @('restore', '.\src\cascadia\WpfTerminalControl\WpfTerminalControl.csproj', '-p:Platform=AnyCPU')
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @('restore', '.\src\cascadia\WpfTerminalTestNetCore\WpfTerminalTestNetCore.csproj', '-p:Platform=x64')
        Invoke-CheckedNative -FilePath (Join-Path $checkout 'dep\nuget\nuget.exe') -ArgumentList @(
            'restore',
            (Join-Path $checkout 'dep\nuget\packages.config'),
            '-PackagesDirectory',
            (Join-Path $checkout 'packages'),
            '-NonInteractive'
        )

        Import-Module (Join-Path $checkout 'tools\OpenConsole.psm1') -Force
        Set-MsbuildDevEnvironment
        Invoke-CheckedNative -FilePath 'msbuild.exe' -ArgumentList @(
            (Join-Path $checkout 'OpenConsole.sln'),
            '/p:Configuration=Release',
            '/p:Platform=x64',
            '/t:Terminal\wpf\WpfTerminalTestNetCore',
            '/m'
        )
    }
    finally {
        Pop-Location
    }

    $outputRoot = Join-Path $checkout 'src\cascadia\WpfTerminalTestNetCore\bin\x64\Release\net8.0-windows'
    foreach ($expected in $expectedAssets) {
        $source = Join-Path $outputRoot $expected.File
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Approved build did not produce $source"
        }

        $item = Get-Item -LiteralPath $source
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ($item.Length -ne $expected.Bytes -or $hash -ne $expected.Sha256) {
            throw "Built asset does not match the approved bytes/hash: $($expected.File)"
        }
    }

    New-Item -ItemType Directory -Path (Join-Path $assetRoot 'win-x64') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $checkout 'LICENSE') -Destination (Join-Path $assetRoot 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $checkout 'NOTICE.md') -Destination (Join-Path $assetRoot 'NOTICE.md') -Force
    foreach ($expected in $expectedAssets) {
        Copy-Item -LiteralPath (Join-Path $outputRoot $expected.File) -Destination (Join-Path $assetRoot $expected.RelativePath) -Force
    }

    if (-not [string]::IsNullOrWhiteSpace((& git -C $checkout status --short --untracked-files=no))) {
        throw 'Pinned checkout contains tracked modifications after the build.'
    }

    Assert-AssetLayout
}
finally {
    if ($createdCheckout -and -not $KeepCheckout -and (Test-Path -LiteralPath $CheckoutPath)) {
        Remove-Item -LiteralPath $CheckoutPath -Recurse -Force
    }
}
