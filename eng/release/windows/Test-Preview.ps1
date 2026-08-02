[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

    [string] $EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Get-MsiRows([string] $Path, [string] $Query, [int] $ColumnCount) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase',
            [System.Reflection.BindingFlags]::InvokeMethod,
            $null,
            $installer,
            @($Path, 0))
        $view = $database.GetType().InvokeMember(
            'OpenView',
            [System.Reflection.BindingFlags]::InvokeMethod,
            $null,
            $database,
            @($Query))
        $view.GetType().InvokeMember('Execute', [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null) | Out-Null
        $rows = [System.Collections.Generic.List[object]]::new()
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
            if ($null -eq $record) {
                break
            }

            $values = [System.Collections.Generic.List[string]]::new()
            for ($column = 1; $column -le $ColumnCount; $column++) {
                $value = $record.GetType().InvokeMember(
                    'StringData',
                    [System.Reflection.BindingFlags]::GetProperty,
                    $null,
                    $record,
                    @([int] $column))
                $values.Add([string] $value)
            }
            $rows.Add($values.ToArray())
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }

        return $rows.ToArray()
    }
    finally {
        if ($null -ne $view) {
            try { $view.GetType().InvokeMember('Close', [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null) | Out-Null } catch { }
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
        if ($null -ne $database) {
            [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        }
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Get-RelatedProducts([string] $UpgradeCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        return @($installer.RelatedProducts($UpgradeCode))
    }
    finally {
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Get-InstalledProductVersion([string] $ProductCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        return [string] $installer.ProductInfo($ProductCode, 'VersionString')
    }
    finally {
        [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Invoke-MsiExec([string] $Name, [string[]] $Arguments, [string] $LogPath) {
    $msiexecPath = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($msiexecPath)
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.ArgumentList.Add('/qn')
    $startInfo.ArgumentList.Add('/norestart')
    $startInfo.ArgumentList.Add('/L*v')
    $startInfo.ArgumentList.Add($LogPath)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start msiexec.exe for $Name."
    }

    try {
        if (-not $process.WaitForExit([int] [TimeSpan]::FromMinutes(5).TotalMilliseconds)) {
            $process.Kill($true)
            throw "$Name exceeded the five-minute verification bound."
        }
        if ($process.ExitCode -ne 0) {
            $tail = if (Test-Path -LiteralPath $LogPath) { (Get-Content -LiteralPath $LogPath -Tail 40) -join [Environment]::NewLine } else { 'MSI log was not created.' }
            throw "$Name failed with exit code $($process.ExitCode).`n$tail"
        }
    }
    finally {
        $process.Dispose()
    }

    $log = Get-Item -LiteralPath $LogPath
    if ($log.Length -le 0 -or $log.Length -gt 8MB) {
        throw "$Name produced an empty or unbounded MSI log ($($log.Length) bytes)."
    }
    Write-Output "$Name exit code: 0; log: $LogPath ($($log.Length) bytes)"
}

function Get-Inventory([string] $Root) {
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Path = [System.IO.Path]::GetRelativePath($Root, $_.FullName)
            Bytes = $_.Length
        }
    })
}

function Assert-Sentinels([string] $RepositorySentinel, [string] $RepositoryHash, [string] $UserSentinel, [string] $UserHash) {
    if (-not (Test-Path -LiteralPath $RepositorySentinel) -or
        (Get-FileHash -LiteralPath $RepositorySentinel -Algorithm SHA256).Hash -ne $RepositoryHash) {
        throw 'Installer operation modified the controlled repository/worktree sentinel.'
    }
    if (-not (Test-Path -LiteralPath $UserSentinel) -or
        (Get-FileHash -LiteralPath $UserSentinel -Algorithm SHA256).Hash -ne $UserHash) {
        throw 'Installer operation modified user data outside the application install directory.'
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$packageNameMatch = [regex]::Match([System.IO.Path]::GetFileName($resolvedPackagePath), '^gabCode-(?<version>\d+\.\d+\.\d+-preview\.\d+)-windows-x64\.msi$')
if (-not $packageNameMatch.Success) {
    throw "Package filename does not match the approved gabCode preview convention: $resolvedPackagePath"
}
$releaseVersion = $packageNameMatch.Groups['version'].Value
$releaseVersionMatch = [regex]::Match($releaseVersion, '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)-preview\.(?<preview>\d+)$')
$numericVersion = '{0}.{1}.{2}' -f $releaseVersionMatch.Groups['major'].Value, $releaseVersionMatch.Groups['minor'].Value, $releaseVersionMatch.Groups['build'].Value
$previewOrdinal = [long] $releaseVersionMatch.Groups['preview'].Value
if ($previewOrdinal -eq [long]::MaxValue) {
    throw 'Cannot create the bounded same-numeric preview upgrade probe because the preview ordinal is already at its maximum.'
}
$sameNumericProbeVersion = '{0}.{1}.{2}-preview.{3}' -f $releaseVersionMatch.Groups['major'].Value, $releaseVersionMatch.Groups['minor'].Value, $releaseVersionMatch.Groups['build'].Value, ($previewOrdinal + 1)
if ([int] $releaseVersionMatch.Groups['build'].Value -ge 65535) {
    throw 'Cannot create the bounded later-numeric upgrade probe because the MSI build version is already 65535.'
}
$upgradeProbeVersion = '{0}.{1}.{2}-preview.1' -f $releaseVersionMatch.Groups['major'].Value, $releaseVersionMatch.Groups['minor'].Value, ([int] $releaseVersionMatch.Groups['build'].Value + 1)
$upgradeProbeNumericVersion = '{0}.{1}.{2}' -f $releaseVersionMatch.Groups['major'].Value, $releaseVersionMatch.Groups['minor'].Value, ([int] $releaseVersionMatch.Groups['build'].Value + 1)
$upgradeCode = '{14C3C588-78D0-B414-D61A-021C8DB736E5}'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\gabCode'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\gabCode.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'gabCode.lnk'
$desktopShortcutExisted = Test-Path -LiteralPath $desktopShortcut
$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [System.Security.Principal.WindowsPrincipal]::new($currentIdentity)
$isElevated = $currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    throw 'Run deterministic installer verification from a non-elevated PowerShell session.'
}

$defaultEvidencePath = Join-Path $repositoryRoot '.pi\tmp\windows-preview-verification'
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $evidencePath = $defaultEvidencePath
}
elseif ([System.IO.Path]::IsPathRooted($EvidenceDirectory)) {
    $evidencePath = [System.IO.Path]::GetFullPath($EvidenceDirectory)
}
else {
    $evidencePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $EvidenceDirectory))
}
if (Test-Path -LiteralPath $evidencePath) {
    if ($evidencePath -eq $defaultEvidencePath) {
        Remove-Item -LiteralPath $evidencePath -Recurse -Force
    }
    elseif (@(Get-ChildItem -LiteralPath $evidencePath -Force).Count -ne 0) {
        throw "Custom evidence directory is not empty; refusing to remove unrelated entries: $evidencePath"
    }
}
New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null

$repositorySentinelDirectory = Join-Path $repositoryRoot '.pi\tmp\windows-preview-sentinel'
$userSentinelDirectory = Join-Path $env:LOCALAPPDATA ('gabCode-PackagingVerification\' + [Guid]::NewGuid().ToString('N'))
$repositorySentinel = Join-Path $repositorySentinelDirectory 'repository-sentinel.txt'
$userSentinel = Join-Path $userSentinelDirectory 'user-data-sentinel.txt'
New-Item -ItemType Directory -Path $repositorySentinelDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $userSentinelDirectory -Force | Out-Null
[System.IO.File]::WriteAllText($repositorySentinel, 'gabCode controlled repository sentinel')
[System.IO.File]::WriteAllText($userSentinel, 'gabCode controlled user-data sentinel')
$repositoryHash = (Get-FileHash -LiteralPath $repositorySentinel -Algorithm SHA256).Hash
$userHash = (Get-FileHash -LiteralPath $userSentinel -Algorithm SHA256).Hash

$installedByVerification = $false
$report = [ordered]@{}
try {
    $preexistingProducts = @(Get-RelatedProducts $upgradeCode)
    if ($preexistingProducts.Count -ne 0) {
        throw "Refusing to replace an existing gabCode installation: $($preexistingProducts -join ', ')"
    }
    if (Test-Path -LiteralPath $installDirectory) {
        throw "Refusing to remove an unregistered pre-existing install directory: $installDirectory"
    }

    $propertyRows = @(Get-MsiRows $resolvedPackagePath 'SELECT `Property`, `Value` FROM `Property`' 2)
    $properties = @{}
    foreach ($row in $propertyRows) { $properties[$row[0]] = $row[1] }
    foreach ($requiredProperty in @('ProductCode', 'ProductName', 'ProductVersion', 'UpgradeCode', 'ARPNOMODIFY')) {
        if (-not $properties.ContainsKey($requiredProperty)) { throw "MSI Property table is missing '$requiredProperty'." }
    }
    if ($properties.ProductName -ne 'gabCode developer preview' -or
        $properties.ProductVersion -ne $numericVersion -or
        $properties.UpgradeCode -ne $upgradeCode -or
        $properties.ARPNOMODIFY -ne '1') {
        throw 'MSI product identity or numeric version does not match the approved preview contract.'
    }
    if ($properties.ContainsKey('ALLUSERS') -and -not [string]::IsNullOrEmpty($properties.ALLUSERS)) {
        throw 'MSI unexpectedly requests a machine-wide installation.'
    }

    $tables = @((Get-MsiRows $resolvedPackagePath 'SELECT `Name` FROM `_Tables`' 1) | ForEach-Object { $_[0] })
    if ($tables -contains 'CustomAction') { throw 'Preview MSI must not contain custom actions.' }
    if ($tables -contains 'MsiDigitalSignature' -or $tables -contains 'MsiDigitalCertificate') { throw 'Preview MSI unexpectedly contains Windows Installer signature tables.' }

    $directoryRows = @(Get-MsiRows $resolvedPackagePath 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' 3)
    $installDirectoryRow = @($directoryRows | Where-Object { $_[0] -eq 'INSTALLFOLDER' })
    if ($installDirectoryRow.Count -ne 1 -or $installDirectoryRow[0][1] -ne 'LocalProgramsFolder' -or $installDirectoryRow[0][2] -ne 'gabCode') {
        throw 'MSI install directory is not the approved per-user application area.'
    }

    $shortcutRows = @(Get-MsiRows $resolvedPackagePath 'SELECT `Shortcut`, `Directory_`, `Name`, `Target` FROM `Shortcut`' 4)
    if ($shortcutRows.Count -ne 1 -or $shortcutRows[0][1] -ne 'ProgramMenuFolder' -or $shortcutRows[0][2] -ne 'gabCode' -or $shortcutRows[0][3] -ne '[INSTALLFOLDER]GabCode.Windows.exe') {
        throw 'MSI must contain exactly one Start menu shortcut and no desktop shortcut.'
    }

    $msiFiles = @(Get-MsiRows $resolvedPackagePath 'SELECT `FileName`, `FileSize` FROM `File`' 2 | ForEach-Object {
        [pscustomobject]@{ Name = ($_[0] -split '\|')[-1]; Bytes = [long] $_[1] }
    })
    $forbiddenMsiFiles = @($msiFiles | Where-Object { $_.Name -match '(?i)\.pdb$|\.cs$|\.csproj$|\.slnx?$|\.nupkg$|\.snupkg$|\.wixpdb$|testhost|\.Tests\.' })
    if ($forbiddenMsiFiles.Count -ne 0) { throw "MSI contains forbidden build/test files: $($forbiddenMsiFiles.Name -join ', ')" }
    foreach ($requiredFile in @('GabCode.Windows.exe', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.Terminal.Wpf.dll', 'Microsoft.Terminal.Control.dll', 'LICENSE.txt', 'NOTICE.md')) {
        if ($msiFiles.Name -notcontains $requiredFile) { throw "MSI is missing required file '$requiredFile'." }
    }

    $packageSignature = Get-AuthenticodeSignature -LiteralPath $resolvedPackagePath
    if ($packageSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Expected unsigned MSI, observed signature status '$($packageSignature.Status)'."
    }
    $packageBytes = [System.IO.File]::ReadAllBytes($resolvedPackagePath)
    foreach ($pathPattern in @([System.Text.Encoding]::UTF8.GetBytes($repositoryRoot), [System.Text.Encoding]::Unicode.GetBytes($repositoryRoot))) {
        if (Test-ContainsBytes -Bytes $packageBytes -Pattern $pathPattern) {
            throw 'MSI contains the build-machine repository path.'
        }
    }

    Invoke-MsiExec 'install' @('/i', $resolvedPackagePath) (Join-Path $evidencePath 'install.log')
    $installedByVerification = $true
    Assert-Sentinels $repositorySentinel $repositoryHash $userSentinel $userHash

    $installedProducts = @(Get-RelatedProducts $upgradeCode)
    if ($installedProducts.Count -ne 1 -or (Get-InstalledProductVersion $installedProducts[0]) -ne $numericVersion) {
        throw 'Installed product registration does not match the baseline MSI.'
    }
    if (-not (Test-Path -LiteralPath $installDirectory) -or -not (Test-Path -LiteralPath $startMenuShortcut)) {
        throw 'Install did not create the application directory and single Start menu entry.'
    }
    if ((Test-Path -LiteralPath $desktopShortcut) -ne $desktopShortcutExisted) {
        throw 'Install changed the gabCode desktop-shortcut state.'
    }

    $uninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $arpEntries = @(Get-ItemProperty -Path $uninstallRoots -ErrorAction SilentlyContinue | Where-Object {
        $_.PSChildName -eq $installedProducts[0] -and $_.DisplayName -eq 'gabCode developer preview'
    })
    if ($arpEntries.Count -ne 1 -or
        $arpEntries[0].DisplayVersion -ne $numericVersion -or
        $arpEntries[0].WindowsInstaller -ne 1 -or
        $arpEntries[0].UninstallString -notmatch '(?i)msiexec\.exe\s+/X') {
        throw 'Install did not create one normal Windows Installer Apps uninstall entry.'
    }
    $arpEvidence = [pscustomobject]@{
        DisplayName = $arpEntries[0].DisplayName
        DisplayVersion = $arpEntries[0].DisplayVersion
        Publisher = $arpEntries[0].Publisher
        UninstallString = $arpEntries[0].UninstallString
        WindowsInstaller = $arpEntries[0].WindowsInstaller
    }

    $terminalManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'third_party\microsoft-terminal\v1.24.11911.0\manifest.json') -Raw | ConvertFrom-Json
    $terminalEvidence = @()
    foreach ($asset in $terminalManifest.assets) {
        $installedAsset = Join-Path $installDirectory $asset.file
        $actualHash = (Get-FileHash -LiteralPath $installedAsset -Algorithm SHA256).Hash
        $actualBytes = (Get-Item -LiteralPath $installedAsset).Length
        if ($actualHash -ne $asset.sha256 -or $actualBytes -ne $asset.bytes) {
            throw "Installed terminal asset '$($asset.file)' does not match the pinned manifest."
        }
        $terminalEvidence += [pscustomobject]@{ File = $asset.file; Bytes = $actualBytes; SHA256 = $actualHash }
    }
    foreach ($relativeLicense in @('licenses\gabCode\LICENSE.txt', 'licenses\microsoft-terminal\LICENSE.txt', 'licenses\microsoft-terminal\NOTICE.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $relativeLicense))) { throw "Installed payload is missing '$relativeLicense'." }
    }
    foreach ($unsignedFile in @('GabCode.Windows.exe', 'Microsoft.Terminal.Wpf.dll', 'Microsoft.Terminal.Control.dll')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $installDirectory $unsignedFile)
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
            throw "Expected installed '$unsignedFile' to be unsigned; observed '$($signature.Status)'."
        }
    }

    $installedInventory = @(Get-Inventory $installDirectory)
    $forbiddenInstalledFiles = @($installedInventory | Where-Object { $_.Path -match '(?i)\.pdb$|\.cs$|\.csproj$|\.slnx?$|\.nupkg$|\.snupkg$|\.wixpdb$|testhost|\.Tests\.' })
    if ($forbiddenInstalledFiles.Count -ne 0) { throw "Installed payload contains forbidden files: $($forbiddenInstalledFiles.Path -join ', ')" }

    Invoke-MsiExec 'repair' @('/fa', $resolvedPackagePath) (Join-Path $evidencePath 'repair.log')
    Assert-Sentinels $repositorySentinel $repositoryHash $userSentinel $userHash

    $sameNumericProbeOutput = Join-Path $evidencePath 'same-numeric-upgrade-package'
    & (Join-Path $PSScriptRoot 'Build-Preview.ps1') -Version $sameNumericProbeVersion -OutputDirectory $sameNumericProbeOutput
    if (-not $?) { throw 'Local same-numeric preview package build failed.' }
    $sameNumericProbePackage = Join-Path $sameNumericProbeOutput "gabCode-$sameNumericProbeVersion-windows-x64.msi"
    Invoke-MsiExec 'same-numeric-upgrade' @('/i', $sameNumericProbePackage) (Join-Path $evidencePath 'same-numeric-upgrade.log')
    Assert-Sentinels $repositorySentinel $repositoryHash $userSentinel $userHash

    $sameNumericProducts = @(Get-RelatedProducts $upgradeCode)
    $sameNumericFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installDirectory 'GabCode.Windows.dll')).ProductVersion
    if ($sameNumericProducts.Count -ne 1 -or
        $sameNumericProducts[0] -eq $properties.ProductCode -or
        (Get-InstalledProductVersion $sameNumericProducts[0]) -ne $numericVersion -or
        -not $sameNumericFileVersion.StartsWith($sameNumericProbeVersion, [StringComparison]::Ordinal)) {
        throw 'Same-numeric preview label did not replace the first product and payload.'
    }

    $probeOutput = Join-Path $evidencePath 'later-numeric-upgrade-package'
    & (Join-Path $PSScriptRoot 'Build-Preview.ps1') -Version $upgradeProbeVersion -OutputDirectory $probeOutput
    if (-not $?) { throw 'Local later-numeric preview package build failed.' }
    $probePackage = Join-Path $probeOutput "gabCode-$upgradeProbeVersion-windows-x64.msi"
    Invoke-MsiExec 'later-numeric-upgrade' @('/i', $probePackage) (Join-Path $evidencePath 'later-numeric-upgrade.log')
    Assert-Sentinels $repositorySentinel $repositoryHash $userSentinel $userHash

    $upgradedProducts = @(Get-RelatedProducts $upgradeCode)
    if ($upgradedProducts.Count -ne 1 -or
        $upgradedProducts[0] -eq $sameNumericProducts[0] -or
        (Get-InstalledProductVersion $upgradedProducts[0]) -ne $upgradeProbeNumericVersion) {
        throw 'Later numeric preview did not replace the same-numeric preview registration.'
    }
    if (@(Get-ChildItem -LiteralPath (Split-Path $installDirectory -Parent) -Directory -Filter 'gabCode').Count -ne 1) {
        throw 'Upgrade created an unintended side-by-side application directory.'
    }

    Invoke-MsiExec 'uninstall' @('/x', $probePackage) (Join-Path $evidencePath 'uninstall.log')
    $installedByVerification = $false
    Assert-Sentinels $repositorySentinel $repositoryHash $userSentinel $userHash
    if (@(Get-RelatedProducts $upgradeCode).Count -ne 0) { throw 'Uninstall left a registered gabCode product.' }
    if (Test-Path -LiteralPath $installDirectory) { throw 'Uninstall left the gabCode application directory.' }
    if (Test-Path -LiteralPath $startMenuShortcut) { throw 'Uninstall left the gabCode Start menu entry.' }
    if ((Test-Path -LiteralPath $desktopShortcut) -ne $desktopShortcutExisted) { throw 'Uninstall changed desktop-shortcut state.' }

    $remainingProcesses = @(Get-Process -Name 'GabCode.Windows' -ErrorAction SilentlyContinue)
    if ($remainingProcesses.Count -ne 0) { throw "Verification left $($remainingProcesses.Count) gabCode application process(es)." }

    $os = Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsName, OsVersion, OsArchitecture, CsSystemType
    $report = [ordered]@{
        Package = $resolvedPackagePath
        PackageSHA256 = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256).Hash
        PackageSignature = $packageSignature.Status.ToString()
        NumericProductVersion = $numericVersion
        SameNumericUpgradeProbeVersion = $sameNumericProbeVersion
        LaterNumericUpgradeProbeVersion = $upgradeProbeNumericVersion
        UpgradeCode = $upgradeCode
        ProductCode = $properties.ProductCode
        MsiFileCount = $msiFiles.Count
        MsiFiles = $msiFiles
        InstalledFileCount = $installedInventory.Count
        InstalledFiles = $installedInventory
        TerminalAssets = $terminalEvidence
        InstallDirectory = $installDirectory
        StartMenuEntry = $startMenuShortcut
        AppsUninstallEntry = $arpEvidence
        VerificationUser = $currentIdentity.Name
        VerificationElevated = $isElevated
        SentinelSHA256 = [ordered]@{ Repository = $repositoryHash; UserData = $userHash }
        RemainingGabCodeProcesses = $remainingProcesses.Count
        SelfContainedEvidence = @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')
        OperatingSystem = $os
        Accessibility = [ordered]@{
            KeyboardOnlyInstaller = 'NOT CHECKED by noninteractive verifier'
            Narrator = 'NOT CHECKED'
            HighContrast = 'NOT CHECKED'
            Scaling = 'NOT CHECKED'
            TerminalIntegration = 'NOT CHECKED by packaging verifier'
        }
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidencePath 'artifact-report.json') -Encoding utf8NoBOM

    Write-Output "Verification evidence: $evidencePath"
    Write-Output "MSI signature: $($packageSignature.Status) (expected)"
    Write-Output "Installed file count: $($installedInventory.Count)"
    foreach ($asset in $terminalEvidence) { Write-Output "$($asset.File): $($asset.Bytes) bytes; SHA256 $($asset.SHA256)" }
    Write-Output 'Install, repair, same-numeric upgrade, later-numeric upgrade, and uninstall exit codes: 0'
    Write-Output 'Repository and user-data sentinels: unchanged'
    Write-Output 'Remaining gabCode application processes: 0'
}
finally {
    if ($installedByVerification) {
        foreach ($productCode in @(Get-RelatedProducts $upgradeCode)) {
            try {
                Invoke-MsiExec 'failure-cleanup-uninstall' @('/x', $productCode) (Join-Path $evidencePath 'failure-cleanup-uninstall.log')
            }
            catch {
                Write-Warning "Could not clean up product $productCode after verification failure: $_"
            }
        }
    }
    if (Test-Path -LiteralPath $repositorySentinelDirectory) { Remove-Item -LiteralPath $repositorySentinelDirectory -Recurse -Force }
    if (Test-Path -LiteralPath $userSentinelDirectory) { Remove-Item -LiteralPath $userSentinelDirectory -Recurse -Force }
}
