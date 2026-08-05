<#
.SYNOPSIS
Validates or restores an exact v4 campaign-start Fan Control snapshot.

.DESCRIPTION
Fan Control must already be stopped. Without -Restore, every snapshot file is
checked against its recorded length, SHA-256, and (for the plugin) versions,
but no Fan Control file is changed. Passing -Restore explicitly restores the
four allowlisted files and verifies their hashes again; CACHE is restored last.

.EXAMPLE
.\Restore-V4FanControlDeploymentSnapshot.ps1 `
  -FanControlDirectory 'C:\path\to\FanControl' `
  -SnapshotDirectory 'C:\path\to\repo\diagnostics\hardware-results\v4\deployment-start'

.EXAMPLE
.\Restore-V4FanControlDeploymentSnapshot.ps1 `
  -FanControlDirectory 'C:\path\to\FanControl' `
  -SnapshotDirectory 'C:\path\to\repo\diagnostics\hardware-results\v4\deployment-start' `
  -Restore
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FanControlDirectory,

    [Parameter(Mandatory = $true)]
    [string] $SnapshotDirectory,

    [switch] $Restore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifestFileName = 'deployment-snapshot.json'
$expectedKind = 'FanControl.MinisforumUM780XTX.V4DeploymentSnapshot'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Join-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "A snapshot relative path was unexpectedly rooted: $RelativePath"
    }

    $normalizedRoot = Get-NormalizedFullPath $Root
    $candidate = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($normalizedRoot, $RelativePath))
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A snapshot path escaped its root: $RelativePath"
    }
    $candidate
}

function Assert-FanControlStopped {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like 'FanControl*'
    })
    if ($running.Count -ne 0) {
        $description = $running | ForEach-Object {
            "$($_.ProcessName) (PID $($_.Id))"
        }
        throw (
            'Fan Control must be exited normally before snapshot, deployment, ' +
            'or restoration. Running process(es): ' + ($description -join ', '))
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Json {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $json = $Value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $utf8NoBom)
}

Assert-FanControlStopped

$fanControlRoot = Get-NormalizedFullPath $FanControlDirectory
if (-not [System.IO.Directory]::Exists($fanControlRoot)) {
    throw "Fan Control directory does not exist: $fanControlRoot"
}
$snapshotRoot = Get-NormalizedFullPath $SnapshotDirectory
if (-not [System.IO.Directory]::Exists($snapshotRoot)) {
    throw "Snapshot directory does not exist: $snapshotRoot"
}

$manifestPath = Join-PathUnderRoot $snapshotRoot $manifestFileName
if (-not [System.IO.File]::Exists($manifestPath)) {
    throw "Snapshot manifest does not exist: $manifestPath"
}
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ([int]$manifest.SchemaVersion -ne 1 -or
    [string]$manifest.Kind -ne $expectedKind) {
    throw 'The deployment snapshot manifest kind or schema is unsupported.'
}

$recordedFanControlRoot = Get-NormalizedFullPath `
    ([string]$manifest.FanControlDirectory)
if (-not $fanControlRoot.Equals(
        $recordedFanControlRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw (
        "The requested Fan Control directory '$fanControlRoot' does not match " +
        "the snapshot origin '$recordedFanControlRoot'.")
}

$expectedDefinitions = [ordered]@{
    Plugin = [ordered]@{
        RelativePath = 'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll'
        SnapshotRelativePath = 'files\FanControl.MinisforumUM780XTX.dll'
    }
    Cache = [ordered]@{
        RelativePath = 'Configurations\CACHE'
        SnapshotRelativePath = 'files\CACHE'
    }
    UserConfig = [ordered]@{
        RelativePath = 'Configurations\userConfig.json'
        SnapshotRelativePath = 'files\userConfig.json'
    }
    FanControlLog = [ordered]@{
        RelativePath = 'log.txt'
        SnapshotRelativePath = 'files\log.txt'
    }
}

$records = @($manifest.SnapshotFiles)
if ($records.Count -ne $expectedDefinitions.Count) {
    throw (
        "The manifest has $($records.Count) snapshot records; expected " +
        "$($expectedDefinitions.Count).")
}

$validated = @()
foreach ($key in $expectedDefinitions.Keys) {
    $matches = @($records | Where-Object { [string]$_.Key -eq $key })
    if ($matches.Count -ne 1) {
        throw "The snapshot manifest must contain exactly one '$key' record."
    }
    $record = $matches[0]
    $expected = $expectedDefinitions[$key]
    if ([string]$record.RelativePath -ne $expected.RelativePath -or
        [string]$record.SnapshotRelativePath -ne
            $expected.SnapshotRelativePath) {
        throw "The '$key' snapshot paths do not match the fixed restore allowlist."
    }

    $snapshotPath = Join-PathUnderRoot `
        $snapshotRoot ([string]$record.SnapshotRelativePath)
    if (-not [System.IO.File]::Exists($snapshotPath)) {
        throw "Snapshot file is missing: $snapshotPath"
    }
    $snapshotItem = Get-Item -LiteralPath $snapshotPath
    $snapshotHash = Get-Sha256 $snapshotPath
    if ($snapshotItem.Length -ne [long]$record.Length -or
        $snapshotHash -ne [string]$record.Sha256) {
        throw "Snapshot hash or length verification failed for '$key'."
    }

    if ($key -eq 'Plugin') {
        $fileVersion = $snapshotItem.VersionInfo.FileVersion
        $assemblyVersion =
            [System.Reflection.AssemblyName]::GetAssemblyName($snapshotPath).
                Version.ToString()
        if ($fileVersion -ne [string]$record.FileVersion -or
            $assemblyVersion -ne [string]$record.AssemblyVersion) {
            throw 'Snapshot plugin version verification failed.'
        }
    }

    $targetPath = Join-PathUnderRoot `
        $fanControlRoot ([string]$record.RelativePath)
    $targetParent = [System.IO.Path]::GetDirectoryName($targetPath)
    if (-not $targetParent -or
        -not [System.IO.Directory]::Exists($targetParent)) {
        throw "Restore target directory does not exist: $targetParent"
    }

    $validated += [ordered]@{
        Key = $key
        SnapshotPath = $snapshotPath
        TargetPath = $targetPath
        Length = [long]$record.Length
        Sha256 = [string]$record.Sha256
        FileVersion = [string]$record.FileVersion
        AssemblyVersion = [string]$record.AssemblyVersion
        LastWriteTimeUtc = [string]$record.LastWriteTimeUtc
    }
}

if (-not $Restore) {
    [pscustomobject]@{
        Status = 'SnapshotVerifiedNoRestoreRequested'
        SnapshotDirectory = $snapshotRoot
        FanControlDirectory = $fanControlRoot
        VerifiedFileCount = $validated.Count
        ManifestPath = $manifestPath
    }
    return
}

Assert-FanControlStopped

# Restore CACHE last so it never points at a configuration that has not yet
# been restored. Extra campaign configuration files are deliberately untouched.
$restoreOrder = @('Plugin', 'UserConfig', 'FanControlLog', 'Cache')
$restored = @()
foreach ($key in $restoreOrder) {
    $entry = $validated | Where-Object { $_.Key -eq $key } |
        Select-Object -First 1
    Copy-Item -LiteralPath $entry.SnapshotPath `
        -Destination $entry.TargetPath -Force

    $restoredItem = Get-Item -LiteralPath $entry.TargetPath
    $restoredItem.LastWriteTimeUtc = [datetime]::Parse(
        $entry.LastWriteTimeUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    $restoredHash = Get-Sha256 $entry.TargetPath
    if ($restoredItem.Length -ne $entry.Length -or
        $restoredHash -ne $entry.Sha256) {
        throw "Restored hash or length verification failed for '$key'."
    }

    if ($key -eq 'Plugin') {
        $fileVersion = $restoredItem.VersionInfo.FileVersion
        $assemblyVersion =
            [System.Reflection.AssemblyName]::GetAssemblyName($entry.TargetPath).
                Version.ToString()
        if ($fileVersion -ne $entry.FileVersion -or
            $assemblyVersion -ne $entry.AssemblyVersion) {
            throw 'Restored plugin version verification failed.'
        }
    }

    $restored += [ordered]@{
        Key = $key
        TargetPath = $entry.TargetPath
        Length = $restoredItem.Length
        Sha256 = $restoredHash
        FileVersion = if ($key -eq 'Plugin') {
            $restoredItem.VersionInfo.FileVersion
        } else {
            $null
        }
    }
}

$report = [ordered]@{
    SchemaVersion = 1
    Kind = 'FanControl.MinisforumUM780XTX.V4DeploymentRestoreReport'
    RestoredUtc = [DateTimeOffset]::UtcNow.ToString('o')
    SnapshotDirectory = $snapshotRoot
    ManifestPath = $manifestPath
    FanControlDirectory = $fanControlRoot
    Files = $restored
    Status = 'RestoredAndVerified'
}
$reportName = 'restore-report-' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.json'
$reportPath = Join-PathUnderRoot $snapshotRoot $reportName
Write-Json $report $reportPath

[pscustomobject]@{
    Status = $report.Status
    SnapshotDirectory = $snapshotRoot
    FanControlDirectory = $fanControlRoot
    RestoredFileCount = $restored.Count
    ReportPath = $reportPath
}
