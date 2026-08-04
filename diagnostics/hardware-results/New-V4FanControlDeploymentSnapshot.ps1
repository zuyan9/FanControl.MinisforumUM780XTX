<#
.SYNOPSIS
Creates an immutable campaign-start snapshot and optionally deploys an exact
v4.0.0.0 UM780 XTX plugin build.

.DESCRIPTION
Fan Control must already be stopped. Without -DeployV4, this script only
validates the supplied v4 DLL and snapshots the deployed plugin, CACHE,
userConfig.json, and log.txt. Passing -DeployV4 explicitly enables the single
deployed-DLL replacement after the snapshot has verified.

.EXAMPLE
.\New-V4FanControlDeploymentSnapshot.ps1 `
  -FanControlDirectory 'C:\path\to\FanControl' `
  -BuiltPluginPath 'C:\path\to\repo\bin\Release\net10.0-windows\FanControl.MinisforumUM780XTX.dll' `
  -SnapshotDirectory 'C:\path\to\repo\diagnostics\hardware-results\v4\deployment-start'

.EXAMPLE
.\New-V4FanControlDeploymentSnapshot.ps1 `
  -FanControlDirectory 'C:\path\to\FanControl' `
  -BuiltPluginPath 'C:\path\to\repo\bin\Release\net10.0-windows\FanControl.MinisforumUM780XTX.dll' `
  -SnapshotDirectory 'C:\path\to\repo\diagnostics\hardware-results\v4\deployment-start' `
  -DeployV4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FanControlDirectory,

    [Parameter(Mandatory = $true)]
    [string] $BuiltPluginPath,

    [Parameter(Mandatory = $true)]
    [string] $SnapshotDirectory,

    [switch] $DeployV4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedPluginFileName = 'FanControl.MinisforumUM780XTX.dll'
$expectedVersion = '4.0.0.0'
$manifestFileName = 'deployment-snapshot.json'
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
        throw "A campaign relative path was unexpectedly rooted: $RelativePath"
    }

    $normalizedRoot = Get-NormalizedFullPath $Root
    $candidate = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($normalizedRoot, $RelativePath))
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A campaign path escaped its root: $RelativePath"
    }
    $candidate
}

function Test-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Candidate
    )

    $normalizedRoot = Get-NormalizedFullPath $Root
    $normalizedCandidate = Get-NormalizedFullPath $Candidate
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    $normalizedCandidate.StartsWith(
        $prefix,
        [System.StringComparison]::OrdinalIgnoreCase)
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

function Get-PluginVersionRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = Get-Item -LiteralPath $Path
    $assemblyVersion = try {
        [System.Reflection.AssemblyName]::GetAssemblyName($item.FullName).
            Version.ToString()
    }
    catch {
        throw "The supplied plugin is not a readable .NET assembly: $($_.Exception.Message)"
    }

    [ordered]@{
        Path = $item.FullName
        Length = $item.Length
        Sha256 = Get-Sha256 $item.FullName
        FileVersion = $item.VersionInfo.FileVersion
        AssemblyVersion = $assemblyVersion
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}

function Assert-ExactV4Plugin {
    param(
        [Parameter(Mandatory = $true)] $Record,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ([System.IO.Path]::GetFileName([string]$Record.Path) -ne
            $expectedPluginFileName) {
        throw "$Description must be named $expectedPluginFileName."
    }
    if ([string]$Record.FileVersion -ne $expectedVersion -or
        [string]$Record.AssemblyVersion -ne $expectedVersion) {
        throw (
            "$Description is not exact v4.0.0.0: file version " +
            "'$($Record.FileVersion)', assembly version " +
            "'$($Record.AssemblyVersion)'.")
    }
}

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $json = $Manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $utf8NoBom)
}

Assert-FanControlStopped

$fanControlRoot = Get-NormalizedFullPath $FanControlDirectory
if (-not [System.IO.Directory]::Exists($fanControlRoot)) {
    throw "Fan Control directory does not exist: $fanControlRoot"
}

$builtPlugin = Get-NormalizedFullPath $BuiltPluginPath
if (-not [System.IO.File]::Exists($builtPlugin)) {
    throw "Built plugin does not exist: $builtPlugin"
}
$builtRecord = Get-PluginVersionRecord $builtPlugin
Assert-ExactV4Plugin $builtRecord 'The supplied built plugin'

$snapshotRoot = Get-NormalizedFullPath $SnapshotDirectory
if ([System.IO.File]::Exists($snapshotRoot) -or
    [System.IO.Directory]::Exists($snapshotRoot)) {
    throw "Snapshot path already exists; refusing to overwrite it: $snapshotRoot"
}
if (Test-PathUnderRoot $fanControlRoot $snapshotRoot) {
    throw 'The snapshot directory must be outside the Fan Control directory.'
}

$definitions = @(
    [ordered]@{
        Key = 'Plugin'
        RelativePath = 'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll'
        SnapshotRelativePath = 'files\FanControl.MinisforumUM780XTX.dll'
    },
    [ordered]@{
        Key = 'Cache'
        RelativePath = 'Configurations\CACHE'
        SnapshotRelativePath = 'files\CACHE'
    },
    [ordered]@{
        Key = 'UserConfig'
        RelativePath = 'Configurations\userConfig.json'
        SnapshotRelativePath = 'files\userConfig.json'
    },
    [ordered]@{
        Key = 'FanControlLog'
        RelativePath = 'log.txt'
        SnapshotRelativePath = 'files\log.txt'
    }
)

foreach ($definition in $definitions) {
    $source = Join-PathUnderRoot $fanControlRoot $definition.RelativePath
    if (-not [System.IO.File]::Exists($source)) {
        throw "Required campaign source is missing: $source"
    }
}

$deployedPluginPath = Join-PathUnderRoot `
    $fanControlRoot $definitions[0].RelativePath
if ($builtPlugin.Equals(
        $deployedPluginPath,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The built plugin path and deployed plugin path must be different.'
}

[System.IO.Directory]::CreateDirectory($snapshotRoot) | Out-Null
[System.IO.Directory]::CreateDirectory(
    (Join-PathUnderRoot $snapshotRoot 'files')) | Out-Null

$snapshotRecords = @()
foreach ($definition in $definitions) {
    $source = Join-PathUnderRoot $fanControlRoot $definition.RelativePath
    $destination = Join-PathUnderRoot `
        $snapshotRoot $definition.SnapshotRelativePath
    Copy-Item -LiteralPath $source -Destination $destination

    $sourceItem = Get-Item -LiteralPath $source
    $destinationItem = Get-Item -LiteralPath $destination
    $sourceHash = Get-Sha256 $source
    $destinationHash = Get-Sha256 $destination
    if ($sourceItem.Length -ne $destinationItem.Length -or
        $sourceHash -ne $destinationHash) {
        throw "Snapshot verification failed for $($definition.Key)."
    }

    $snapshotRecords += [ordered]@{
        Key = $definition.Key
        RelativePath = $definition.RelativePath
        SnapshotRelativePath = $definition.SnapshotRelativePath
        Length = $sourceItem.Length
        Sha256 = $sourceHash
        FileVersion = if ($definition.Key -eq 'Plugin') {
            $sourceItem.VersionInfo.FileVersion
        } else {
            $null
        }
        AssemblyVersion = if ($definition.Key -eq 'Plugin') {
            [System.Reflection.AssemblyName]::GetAssemblyName($source).
                Version.ToString()
        } else {
            $null
        }
        LastWriteTimeUtc = $sourceItem.LastWriteTimeUtc.ToString('o')
    }
}

$manifest = [ordered]@{
    SchemaVersion = 1
    Kind = 'FanControl.MinisforumUM780XTX.V4DeploymentSnapshot'
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    FanControlDirectory = $fanControlRoot
    ExpectedBuiltPluginVersion = $expectedVersion
    BuiltPlugin = $builtRecord
    SnapshotFiles = $snapshotRecords
    Deployment = [ordered]@{
        Requested = [bool]$DeployV4
        Status = if ($DeployV4) { 'Pending' } else { 'NotRequested' }
        CompletedUtc = $null
        TargetPath = $deployedPluginPath
        BeforeSha256 = $snapshotRecords[0].Sha256
        AfterSha256 = $null
        Failure = $null
    }
}
$manifestPath = Join-PathUnderRoot $snapshotRoot $manifestFileName
Write-Manifest $manifest $manifestPath

if ($DeployV4) {
    Assert-FanControlStopped
    $currentHash = Get-Sha256 $deployedPluginPath
    if ($currentHash -ne $snapshotRecords[0].Sha256) {
        throw (
            'The deployed plugin changed after snapshot and before deployment; ' +
            'no deployment was attempted.')
    }

    try {
        Copy-Item -LiteralPath $builtPlugin -Destination $deployedPluginPath -Force
        $deployedRecord = Get-PluginVersionRecord $deployedPluginPath
        Assert-ExactV4Plugin $deployedRecord 'The deployed plugin'
        if ($deployedRecord.Sha256 -ne $builtRecord.Sha256 -or
            $deployedRecord.Length -ne $builtRecord.Length) {
            throw 'The deployed plugin does not exactly match the supplied v4 build.'
        }

        $manifest.Deployment.Status = 'DeployedAndVerified'
        $manifest.Deployment.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $manifest.Deployment.AfterSha256 = $deployedRecord.Sha256
        Write-Manifest $manifest $manifestPath
    }
    catch {
        $deploymentFailure = $_.Exception.ToString()
        $rollbackFailure = $null
        try {
            $snapshotPlugin = Join-PathUnderRoot `
                $snapshotRoot $snapshotRecords[0].SnapshotRelativePath
            Copy-Item -LiteralPath $snapshotPlugin `
                -Destination $deployedPluginPath -Force
            if ((Get-Sha256 $deployedPluginPath) -ne
                    $snapshotRecords[0].Sha256) {
                throw 'Automatic rollback hash verification failed.'
            }
        }
        catch {
            $rollbackFailure = $_.Exception.ToString()
        }

        $manifest.Deployment.Status = if ($rollbackFailure) {
            'DeploymentFailedRollbackFailed'
        } else {
            'DeploymentFailedOriginalRestored'
        }
        $manifest.Deployment.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $manifest.Deployment.Failure = [ordered]@{
            Deployment = $deploymentFailure
            Rollback = $rollbackFailure
        }
        Write-Manifest $manifest $manifestPath

        if ($rollbackFailure) {
            throw (
                "v4 deployment failed and automatic rollback also failed. " +
                "Deployment: $deploymentFailure Rollback: $rollbackFailure")
        }
        throw "v4 deployment failed; the original plugin was restored: $deploymentFailure"
    }
}

[pscustomobject]@{
    SnapshotDirectory = $snapshotRoot
    ManifestPath = $manifestPath
    SnapshotFileCount = $snapshotRecords.Count
    BuiltPluginVersion = $builtRecord.FileVersion
    BuiltPluginSha256 = $builtRecord.Sha256
    DeploymentStatus = $manifest.Deployment.Status
    DeployedPluginPath = $deployedPluginPath
}
