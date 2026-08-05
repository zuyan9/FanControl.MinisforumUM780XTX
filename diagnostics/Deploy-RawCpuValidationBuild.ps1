[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FanControlDirectory,

    [Parameter(Mandatory = $true)]
    [string] $BuiltPluginPath,

    [Parameter(Mandatory = $true)]
    [string] $DisabledConfigPath,

    [Parameter(Mandatory = $true)]
    [string] $SnapshotDirectory,

    [string] $ExpectedVersion = '0.2.0.0'
)

<#
.SYNOPSIS
Orderly stops Fan Control, snapshots its deployment, installs the raw-CPU
validation build, starts Fan Control on a both-controls-disabled config, and
verifies the new plugin over Fan Control IPC.

.DESCRIPTION
The script must run elevated. It never force-terminates Fan Control. If
deployment or startup verification fails after the orderly stop, it restores
the snapshotted plugin, user configuration, and CACHE while Fan Control remains
stopped. The snapshot directory must not already exist.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$expectedFanControlVersion = '272.0.0.0'
$pluginName = 'Minisforum UM780 XTX (F7BSD)'
$cpuControlId =
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-raw-v1"
$systemControlId =
    "$pluginName/minisforum.um780xtx.f7bsd.system-raw-v2"
$cpuRpmId = "$pluginName/minisforum.um780xtx.f7bsd.fan1"
$systemRpmId = "$pluginName/minisforum.um780xtx.f7bsd.fan2"
$cpuTemperatureId =
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-temperature"
$systemTemperatureId =
    "$pluginName/minisforum.um780xtx.f7bsd.system-temperature"

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Get-FileRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "Expected a file, found a directory: $Path"
    }
    [ordered]@{
        Path = $item.FullName
        Length = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $item.FullName `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        FileVersion = [string]$item.VersionInfo.FileVersion
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}

function Assert-FileMatchesRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Record,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $current = Get-FileRecord $Path
    if ($current.Length -ne [long]$Record.Length -or
        $current.Sha256 -cne [string]$Record.Sha256) {
        throw "$Description changed after it was validated: $Path"
    }
    $current
}

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Description is missing required property '$Name'."
    }
    $property.Value
}

function Assert-DisabledConfig {
    param([Parameter(Mandatory = $true)][string] $Path)

    $config = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    $version = Get-RequiredPropertyValue $config '__VERSION__' $Path
    if ([string]$version -cne '272') {
        throw "$Path must be a Fan Control V272 configuration."
    }

    $fanControl = Get-RequiredPropertyValue $config 'FanControl' $Path
    $controls = @(Get-RequiredPropertyValue `
        $fanControl 'Controls' "$Path FanControl")
    if ($controls.Count -ne 2) {
        throw "$Path must contain exactly the two UM780 controls."
    }

    $cpu = @($controls | Where-Object {
        [string]$_.Identifier -ceq $cpuControlId
    })
    $system = @($controls | Where-Object {
        [string]$_.Identifier -ceq $systemControlId
    })
    if ($cpu.Count -ne 1 -or $system.Count -ne 1) {
        throw (
            "$Path must contain exactly one raw-v1 CPU control and one " +
            'raw-v2 system control.')
    }

    foreach ($entry in @(
            [ordered]@{ Name = 'CPU'; Control = $cpu[0] },
            [ordered]@{ Name = 'system'; Control = $system[0] })) {
        $description = "$Path $($entry.Name) control"
        $enable = Get-RequiredPropertyValue `
            $entry.Control 'Enable' $description
        $forceApply = Get-RequiredPropertyValue `
            $entry.Control 'ForceApply' $description
        $selectedCurve = Get-RequiredPropertyValue `
            $entry.Control 'SelectedFanCurve' $description
        if ($enable -isnot [bool] -or $enable) {
            throw "$description must set Enable to false."
        }
        if ($forceApply -isnot [bool] -or $forceApply) {
            throw "$description must set ForceApply to false."
        }
        if ($null -ne $selectedCurve) {
            throw "$description must not select a fan curve."
        }
    }
}

function Assert-CacheSelectsUserConfig {
    param(
        [Parameter(Mandatory = $true)][string] $CachePath,
        [Parameter(Mandatory = $true)][string] $UserConfigPath
    )

    $cache = [IO.File]::ReadAllText($CachePath) | ConvertFrom-Json
    $fileName = Get-RequiredPropertyValue `
        $cache 'CurrentConfigFileName' $CachePath
    $folder = Get-RequiredPropertyValue `
        $cache 'CustomConfigFolder' $CachePath
    if ([string]$fileName -cne [IO.Path]::GetFileName($UserConfigPath)) {
        throw (
            "$CachePath selects config '$fileName'; expected " +
            "'$([IO.Path]::GetFileName($UserConfigPath))'.")
    }
    if ([string]::IsNullOrWhiteSpace([string]$folder)) {
        throw "$CachePath has no custom configuration folder."
    }

    $selected = Get-FullPath (Join-Path ([string]$folder) ([string]$fileName))
    if (-not $selected.Equals(
            $UserConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            "$CachePath selects '$selected'; expected the deployed disabled " +
            "config '$UserConfigPath'.")
    }
}

function Assert-IpcFanControlVersion {
    param([Parameter(Mandatory = $true)][string] $OutputPath)

    $reply = Invoke-Ipc -Command 'info' -OutputPath $OutputPath |
        ConvertFrom-Json
    $version = Get-RequiredPropertyValue $reply 'version' 'Fan Control IPC info'
    if ([string]$version -cne $expectedFanControlVersion) {
        throw (
            "Fan Control IPC reported version '$version'; expected " +
            "'$expectedFanControlVersion'.")
    }
}

function Get-ProcessIdentity {
    param([Parameter(Mandatory = $true)][Diagnostics.Process] $Process)

    try {
        $Process.Refresh()
        $processPath = [string]$Process.Path
        if ([string]::IsNullOrWhiteSpace($processPath)) {
            throw 'The executable path is unavailable.'
        }
        [ordered]@{
            Id = $Process.Id
            StartTimeUtcTicks =
                $Process.StartTime.ToUniversalTime().Ticks
            StartTimeUtc =
                $Process.StartTime.ToUniversalTime().ToString('o')
            Path = Get-FullPath $processPath
        }
    }
    catch {
        throw (
            "Could not establish exact identity for process $($Process.Id): " +
            $_.Exception.Message)
    }
}

function Assert-IdentityUsesPath {
    param(
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)][string] $ExpectedPath,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not ([string]$Identity.Path).Equals(
            $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            "$Description path is '$($Identity.Path)'; expected " +
            "'$ExpectedPath'.")
    }
}

function Assert-OnlyExactFanControlProcess {
    param(
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)][string] $ExpectedPath
    )

    $running = @(Get-Process -Name FanControl,FanControl.Service `
        -ErrorAction SilentlyContinue)
    if ($running.Count -ne 1 -or $running[0].Id -ne [int]$Identity.Id) {
        throw 'The exact expected Fan Control process is not the only running instance.'
    }
    $current = Get-ProcessIdentity $running[0]
    if ([long]$current.StartTimeUtcTicks -ne
            [long]$Identity.StartTimeUtcTicks) {
        throw 'The Fan Control process identity changed unexpectedly.'
    }
    Assert-IdentityUsesPath $current $ExpectedPath 'Fan Control process'
    $running[0]
}

function Assert-NoFanControlProcess {
    $running = @(Get-Process -Name FanControl,FanControl.Service `
        -ErrorAction SilentlyContinue)
    if ($running.Count -ne 0) {
        throw "Expected Fan Control to be stopped; found $($running.Count) process(es)."
    }
}

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
        $utf8NoBom)
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this deployment from an elevated PowerShell session.'
    }
}

function Assert-ExactMachine {
    $properties = Get-ItemProperty `
        -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' `
        -ErrorAction Stop
    $identity = [ordered]@{
        Product = [string]$properties.SystemProductName
        Board = [string]$properties.BaseBoardProduct
        BoardVersion = [string]$properties.BaseBoardVersion
        BiosVersion = [string]$properties.BIOSVersion
        EcMajor = [int]$properties.ECFirmwareMajorRelease
        EcMinor = [int]$properties.ECFirmwareMinorRelease
    }
    if ($identity.Product -cne 'Venus series' -or
        $identity.Board -cne 'F7BSD' -or
        $identity.BoardVersion -cne '1.1' -or
        $identity.BiosVersion -cne '1.06' -or
        $identity.EcMajor -ne 0 -or $identity.EcMinor -ne 8) {
        throw 'This is not the exact validated UM780 XTX/F7BSD firmware profile.'
    }
    $identity
}

function Wait-ExactProcessExit {
    param(
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)][string] $ExpectedPath,
        [int] $Seconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process = Get-Process -Id ([int]$Identity.Id) `
            -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return
        }
        $current = Get-ProcessIdentity $process
        if ([long]$current.StartTimeUtcTicks -ne
                [long]$Identity.StartTimeUtcTicks) {
            return
        }
        Assert-IdentityUsesPath $current $ExpectedPath 'Fan Control process'
        Start-Sleep -Milliseconds 250
    }
    throw (
        "Process $($Identity.Id) did not exit normally within $Seconds " +
        'seconds.')
}

function Invoke-Ipc {
    param(
        [Parameter(Mandatory = $true)][string] $Command,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $OutputPath
    )

    & $script:ipcExecutable $script:ipcAssembly $Command @Arguments `
        --output $OutputPath
    $exitCode = $LASTEXITCODE
    $text = if ([IO.File]::Exists($OutputPath)) {
        [IO.File]::ReadAllText($OutputPath)
    }
    else {
        ''
    }
    if ($exitCode -ne 0) {
        throw "Fan Control IPC '$Command' failed with exit $exitCode`: $text"
    }
    $text
}

function Get-OnlySensor {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Identifier
    )

    $matches = @($Snapshot.Sensors | Where-Object {
        [string]$_.Identifier -ceq $Identifier
    })
    if ($matches.Count -ne 1) {
        return $null
    }
    $matches[0]
}

function Test-FiniteRange {
    param(
        $Value,
        [Parameter(Mandatory = $true)][double] $Minimum,
        [Parameter(Mandatory = $true)][double] $Maximum
    )

    if ($null -eq $Value) {
        return $false
    }
    try {
        $number = [double]$Value
        -not [double]::IsNaN($number) -and
            -not [double]::IsInfinity($number) -and
            $number -ge $Minimum -and $number -le $Maximum
    }
    catch {
        $false
    }
}

function Test-HealthyDisabledSnapshot {
    param([Parameter(Mandatory = $true)] $Snapshot)

    $cpuControl = Get-OnlySensor $Snapshot $cpuControlId
    $systemControl = Get-OnlySensor $Snapshot $systemControlId
    $cpuRpm = Get-OnlySensor $Snapshot $cpuRpmId
    $systemRpm = Get-OnlySensor $Snapshot $systemRpmId
    $cpuTemperature = Get-OnlySensor $Snapshot $cpuTemperatureId
    $systemTemperature = Get-OnlySensor $Snapshot $systemTemperatureId
    if ($null -eq $cpuControl -or $null -eq $systemControl -or
        $null -eq $cpuRpm -or $null -eq $systemRpm -or
        $null -eq $cpuTemperature -or $null -eq $systemTemperature) {
        return $false
    }

    $null -eq $cpuControl.Value -and
        $null -eq $systemControl.Value -and
        (Test-FiniteRange $cpuRpm.Value 0 6500) -and
        (Test-FiniteRange $systemRpm.Value 0 6500) -and
        (Test-FiniteRange $cpuTemperature.Value 1 120) -and
        (Test-FiniteRange $systemTemperature.Value 1 120)
}

function Get-FanControlStateForManifest {
    param($ExpectedIdentity)

    try {
        $running = @(Get-Process -Name FanControl,FanControl.Service `
            -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) {
            return 'Stopped'
        }
        if ($running.Count -eq 1 -and $null -ne $ExpectedIdentity -and
            $running[0].Id -eq [int]$ExpectedIdentity.Id) {
            $current = Get-ProcessIdentity $running[0]
            if ([long]$current.StartTimeUtcTicks -eq
                    [long]$ExpectedIdentity.StartTimeUtcTicks -and
                ([string]$current.Path).Equals(
                    [string]$ExpectedIdentity.Path,
                    [StringComparison]::OrdinalIgnoreCase)) {
                return 'ExpectedOriginalProcessStillRunning'
            }
        }
        "UnexpectedFanControlProcessCount:$($running.Count)"
    }
    catch {
        "Unknown:$($_.Exception.Message)"
    }
}

Assert-Elevated
$machine = Assert-ExactMachine
$fanControlRoot = Get-FullPath $FanControlDirectory
$builtPlugin = Get-FullPath $BuiltPluginPath
$disabledConfig = Get-FullPath $DisabledConfigPath
$snapshotRoot = Get-FullPath $SnapshotDirectory
$pluginTarget = Get-FullPath (Join-Path $fanControlRoot `
    'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll')
$userConfigTarget = Get-FullPath (Join-Path $fanControlRoot `
    'Configurations\userConfig.json')
$cacheTarget = Get-FullPath (Join-Path $fanControlRoot 'Configurations\CACHE')
$logTarget = Get-FullPath (Join-Path $fanControlRoot 'log.txt')
$fanControlExe = Get-FullPath (Join-Path $fanControlRoot 'FanControl.exe')
$script:ipcAssembly = Get-FullPath (Join-Path $fanControlRoot 'FanControl.IPC.dll')
$repoRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
$script:ipcExecutable = Get-FullPath (Join-Path $repoRoot `
    'diagnostics\FanControlIpc\bin\Release\net10.0-windows\FanControlIpc.exe')

if (-not [IO.Directory]::Exists($fanControlRoot)) {
    throw "Fan Control directory does not exist: $fanControlRoot"
}
foreach ($required in @(
        $builtPlugin, $disabledConfig, $pluginTarget, $userConfigTarget,
        $cacheTarget, $logTarget, $fanControlExe, $script:ipcAssembly,
        $script:ipcExecutable)) {
    if (-not [IO.File]::Exists($required)) {
        throw "Required file is missing: $required"
    }
}
if ($builtPlugin.Equals(
        $pluginTarget, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The built plugin and deployed plugin paths must differ.'
}
if ($disabledConfig.Equals(
        $userConfigTarget, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The disabled source config and live user config paths must differ.'
}
if ([IO.File]::Exists($snapshotRoot) -or
    [IO.Directory]::Exists($snapshotRoot)) {
    throw "Snapshot path already exists; refusing to overwrite it: $snapshotRoot"
}
if ($snapshotRoot.StartsWith(
        $fanControlRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The deployment snapshot must be outside the Fan Control directory.'
}
if (@(Get-Process -Name Cinebench -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close every Cinebench process before deployment.'
}

$fanControlRecord = Get-FileRecord $fanControlExe
if ($fanControlRecord.FileVersion -cne $expectedFanControlVersion) {
    throw (
        "Fan Control file version is '$($fanControlRecord.FileVersion)'; " +
        "expected '$expectedFanControlVersion'.")
}
$builtRecord = Get-FileRecord $builtPlugin
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($builtPlugin).
    Version.ToString()
if ($builtRecord.FileVersion -cne $ExpectedVersion -or
    $assemblyVersion -cne $ExpectedVersion) {
    throw (
        "Built plugin version is file '$($builtRecord.FileVersion)' / assembly " +
        "'$assemblyVersion', expected '$ExpectedVersion'.")
}
$disabledRecord = Get-FileRecord $disabledConfig
Assert-DisabledConfig $disabledConfig
$null = Assert-FileMatchesRecord $disabledConfig $disabledRecord `
    'Disabled configuration'
Assert-CacheSelectsUserConfig $cacheTarget $userConfigTarget

$running = @(Get-Process -Name FanControl,FanControl.Service `
    -ErrorAction SilentlyContinue)
if ($running.Count -gt 1) {
    throw "Expected zero or one Fan Control process; found $($running.Count)."
}
$originalProcessIdentity = $null
if ($running.Count -eq 1) {
    if (-not $running[0].Responding) {
        throw 'Fan Control is not responding; refusing deployment.'
    }
    $originalProcessIdentity = Get-ProcessIdentity $running[0]
    Assert-IdentityUsesPath $originalProcessIdentity $fanControlExe `
        'Running Fan Control process'
}

$definitions = @(
    [ordered]@{ Key = 'Plugin'; Source = $pluginTarget; Name = 'plugin.dll' },
    [ordered]@{ Key = 'UserConfig'; Source = $userConfigTarget; Name = 'userConfig.json' },
    [ordered]@{ Key = 'Cache'; Source = $cacheTarget; Name = 'CACHE' },
    [ordered]@{ Key = 'Log'; Source = $logTarget; Name = 'log.txt' }
)
[IO.Directory]::CreateDirectory($snapshotRoot) | Out-Null
$snapshotFiles = Join-Path $snapshotRoot 'files'
[IO.Directory]::CreateDirectory($snapshotFiles) | Out-Null
$manifestPath = Join-Path $snapshotRoot 'deployment.json'
$manifest = [ordered]@{
    SchemaVersion = 1
    Kind = 'FanControl.MinisforumUM780XTX.RawCpuValidationDeployment'
    StartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Machine = $machine
    FanControlDirectory = $fanControlRoot
    FanControl = $fanControlRecord
    ExpectedFanControlVersion = $expectedFanControlVersion
    BuiltPlugin = $builtRecord
    BuiltPluginAssemblyVersion = $assemblyVersion
    DisabledConfig = $disabledRecord
    OriginalFanControlProcess = $originalProcessIdentity
    OriginalFiles = @()
    Status = 'StoppingFanControlAndSnapshotting'
    Failure = $null
}
Write-Manifest $manifest $manifestPath

try {
    if ($null -ne $originalProcessIdentity) {
        $null = Assert-OnlyExactFanControlProcess `
            $originalProcessIdentity $fanControlExe
        Assert-IpcFanControlVersion `
            (Join-Path $snapshotRoot 'ipc-info-old.json')
        $null = Assert-OnlyExactFanControlProcess `
            $originalProcessIdentity $fanControlExe
        $null = Invoke-Ipc -Command 'exit' `
            -OutputPath (Join-Path $snapshotRoot 'ipc-exit-old.json')
        Wait-ExactProcessExit $originalProcessIdentity $fanControlExe
    }
    Assert-NoFanControlProcess

    foreach ($definition in $definitions) {
        $destination = Join-Path $snapshotFiles $definition.Name
        Copy-Item -LiteralPath $definition.Source -Destination $destination
        $sourceRecord = Get-FileRecord $definition.Source
        $snapshotRecord = Get-FileRecord $destination
        if ($sourceRecord.Length -ne $snapshotRecord.Length -or
            $sourceRecord.Sha256 -cne $snapshotRecord.Sha256) {
            throw "Snapshot verification failed for $($definition.Key)."
        }
        $manifest.OriginalFiles += [ordered]@{
            Key = $definition.Key
            TargetPath = $definition.Source
            SnapshotPath = $destination
            Length = $sourceRecord.Length
            Sha256 = $sourceRecord.Sha256
        }
    }
    Assert-CacheSelectsUserConfig $cacheTarget $userConfigTarget
    $manifest.Status = 'SnapshottedOriginalFanControlStopped'
    Write-Manifest $manifest $manifestPath
}
catch {
    $snapshotFailure = $_.Exception.ToString()
    $fanControlState = Get-FanControlStateForManifest `
        $originalProcessIdentity
    $manifest.Status = 'SnapshotStageFailedNoDeploymentAttempted'
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $manifest.Failure = [ordered]@{
        Snapshot = $snapshotFailure
        FanControlState = $fanControlState
    }
    $manifestWriteFailure = $null
    try {
        Write-Manifest $manifest $manifestPath
    }
    catch {
        $manifestWriteFailure = $_.Exception.ToString()
    }
    if ($manifestWriteFailure) {
        throw (
            "Snapshot stage failed and its manifest update also failed. " +
            "Snapshot: $snapshotFailure Manifest: $manifestWriteFailure")
    }
    throw (
        "Snapshot stage failed; no deployment was attempted. Fan Control " +
        "state is '$fanControlState': $snapshotFailure")
}

$startedIdentity = $null
try {
    foreach ($key in @('Plugin', 'UserConfig', 'Cache')) {
        $entry = @($manifest.OriginalFiles | Where-Object Key -ceq $key)
        if ($entry.Count -ne 1) {
            throw "Snapshot entry '$key' is unavailable before deployment."
        }
        $null = Assert-FileMatchesRecord `
            $entry[0].TargetPath $entry[0] "Snapshotted $key target"
    }
    $null = Assert-FileMatchesRecord $builtPlugin $builtRecord `
        'Built plugin'
    $null = Assert-FileMatchesRecord $disabledConfig $disabledRecord `
        'Disabled configuration'
    $null = Assert-FileMatchesRecord $fanControlExe $fanControlRecord `
        'Fan Control executable'
    Assert-DisabledConfig $disabledConfig
    Assert-NoFanControlProcess

    Copy-Item -LiteralPath $builtPlugin -Destination $pluginTarget -Force
    Copy-Item -LiteralPath $disabledConfig -Destination $userConfigTarget -Force
    $deployedRecord = Get-FileRecord $pluginTarget
    if ($deployedRecord.Length -ne $builtRecord.Length -or
        $deployedRecord.Sha256 -cne $builtRecord.Sha256) {
        throw 'The deployed plugin does not match the validated build.'
    }
    $deployedConfigRecord = Get-FileRecord $userConfigTarget
    if ($deployedConfigRecord.Length -ne $disabledRecord.Length -or
        $deployedConfigRecord.Sha256 -cne $disabledRecord.Sha256) {
        throw 'The deployed user config does not match the validated disabled config.'
    }
    Assert-DisabledConfig $userConfigTarget
    $cacheEntry = @($manifest.OriginalFiles |
        Where-Object Key -ceq 'Cache')
    if ($cacheEntry.Count -ne 1) {
        throw 'The snapshotted CACHE entry is unavailable before startup.'
    }
    $null = Assert-FileMatchesRecord `
        $cacheTarget $cacheEntry[0] 'Fan Control CACHE'
    Assert-CacheSelectsUserConfig $cacheTarget $userConfigTarget
    Assert-NoFanControlProcess

    $started = Start-Process -FilePath $fanControlExe `
        -WorkingDirectory $fanControlRoot -WindowStyle Minimized -PassThru
    $startedIdentity = Get-ProcessIdentity $started
    Assert-IdentityUsesPath $startedIdentity $fanControlExe `
        'Started Fan Control process'
    $null = Assert-OnlyExactFanControlProcess `
        $startedIdentity $fanControlExe

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $verified = $null
    $healthySamples = [Collections.Generic.List[object]]::new()
    $consecutiveHealthy = 0
    $attempt = 0
    $ipcVersionVerified = $false
    $lastStartupError = $null
    do {
        Start-Sleep -Seconds 1
        $attempt++
        $null = Assert-OnlyExactFanControlProcess `
            $startedIdentity $fanControlExe
        try {
            if (-not $ipcVersionVerified) {
                Assert-IpcFanControlVersion (Join-Path $snapshotRoot `
                    'ipc-info-started.json')
                $ipcVersionVerified = $true
            }
            $snapshotPath = Join-Path $snapshotRoot `
                ('plugin-sensors-{0:D2}.json' -f $attempt)
            $snapshotText = Invoke-Ipc -Command 'plugin-sensors' `
                -OutputPath $snapshotPath
            $snapshot = $snapshotText | ConvertFrom-Json
            if (Test-HealthyDisabledSnapshot $snapshot) {
                $consecutiveHealthy++
                $healthySamples.Add($snapshot)
                if ($consecutiveHealthy -ge 2) {
                    $verified = $snapshot
                }
            }
            else {
                $consecutiveHealthy = 0
                $healthySamples.Clear()
            }
            $lastStartupError = $null
        }
        catch {
            $consecutiveHealthy = 0
            $healthySamples.Clear()
            $lastStartupError = $_.Exception.ToString()
        }
        $null = Assert-OnlyExactFanControlProcess `
            $startedIdentity $fanControlExe
    } while ($null -eq $verified -and
        [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $verified) {
        throw (
            'The V272 raw-v1 plugin did not produce two consecutive healthy ' +
            'disabled-control telemetry samples within 45 seconds. Last IPC ' +
            "error: $lastStartupError")
    }

    $manifest.Status = 'DeployedStartedAndVerifiedHealthyDisabled'
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $manifest.DeployedPlugin = $deployedRecord
    $manifest.DeployedConfig = $deployedConfigRecord
    $manifest.FanControlProcess = $startedIdentity
    $manifest.HealthyVerificationSampleCount = $healthySamples.Count
    $manifest.FinalHealthySnapshot = $verified
    Write-Manifest $manifest $manifestPath

    [pscustomobject]@{
        Status = $manifest.Status
        SnapshotDirectory = $snapshotRoot
        ManifestPath = $manifestPath
        PluginSha256 = $deployedRecord.Sha256
        ConfigSha256 = $deployedConfigRecord.Sha256
        FanControlProcessId = $startedIdentity.Id
        HealthyVerificationSampleCount = $healthySamples.Count
    }
}
catch {
    $deploymentFailure = $_.Exception.ToString()
    $rollbackFailure = $null
    try {
        $current = @(Get-Process -Name FanControl,FanControl.Service `
            -ErrorAction SilentlyContinue)
        if ($null -ne $startedIdentity) {
            if ($current.Count -ne 0) {
                $null = Assert-OnlyExactFanControlProcess `
                    $startedIdentity $fanControlExe
                $null = Invoke-Ipc -Command 'exit' `
                    -OutputPath (Join-Path $snapshotRoot 'ipc-exit-rollback.json')
                Wait-ExactProcessExit $startedIdentity $fanControlExe
            }
        }
        elseif ($current.Count -ne 0) {
            throw (
                'An unverified Fan Control process is running; refusing to ' +
                'overwrite loaded deployment files during rollback.')
        }
        Assert-NoFanControlProcess

        $rollbackEntries = [Collections.Generic.List[object]]::new()
        foreach ($key in @('Plugin', 'UserConfig', 'Cache')) {
            $entry = @($manifest.OriginalFiles | Where-Object Key -ceq $key)
            if ($entry.Count -ne 1) {
                throw "Snapshot entry '$key' is unavailable for rollback."
            }
            $null = Assert-FileMatchesRecord `
                $entry[0].SnapshotPath $entry[0] "Snapshot copy for $key"
            $rollbackEntries.Add($entry[0])
        }
        foreach ($entry in $rollbackEntries) {
            Copy-Item -LiteralPath $entry.SnapshotPath `
                -Destination $entry.TargetPath -Force
            $null = Assert-FileMatchesRecord `
                $entry.TargetPath $entry "Restored $($entry.Key)"
        }
    }
    catch {
        $rollbackFailure = $_.Exception.ToString()
    }

    $manifest.Status = if ($rollbackFailure) {
        'DeploymentFailedRollbackFailed'
    }
    else {
        'DeploymentFailedOriginalRestoredFanControlStopped'
    }
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $manifest.Failure = [ordered]@{
        Deployment = $deploymentFailure
        Rollback = $rollbackFailure
    }
    $manifestWriteFailure = $null
    try {
        Write-Manifest $manifest $manifestPath
    }
    catch {
        $manifestWriteFailure = $_.Exception.ToString()
    }
    if ($rollbackFailure) {
        throw (
            "Deployment and rollback failed: $deploymentFailure`n" +
            "$rollbackFailure`nManifest: $manifestWriteFailure")
    }
    if ($manifestWriteFailure) {
        throw (
            'Deployment failed and original files were restored, but the ' +
            "manifest update failed: $deploymentFailure`n$manifestWriteFailure")
    }
    throw (
        'Deployment failed; original files were restored and Fan Control ' +
        "remains stopped: $deploymentFailure")
}
