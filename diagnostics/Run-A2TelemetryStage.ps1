[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string] $Name,

    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'cpu', 'igpu', 'combined')]
    [string] $Workload,

    [Parameter(Mandatory = $true)]
    [ValidateRange(10, 3600)]
    [int] $Duration,

    [Parameter(Mandatory = $true)]
    [string] $OutputRoot,

    [Parameter(Mandatory = $true)]
    [string] $FanControlRoot,

    [ValidateRange(1, 120)]
    [double] $CpuMaximumC = 95,

    [ValidateRange(1, 120)]
    [double] $SystemMaximumC = 69,

    [ValidateRange(0, 100)]
    [Nullable[double]] $ExpectedCpuControlPercent,

    [switch] $RequireActiveCpuControl,

    [ValidateRange(0, 100)]
    [Nullable[double]] $ExpectedSystemControlPercent,

    [ValidateRange(0, 6500)]
    [int] $MinimumSystemRpm = 0,

    [switch] $ExtendedThermalGuard,

    [ValidateRange(1, 120)]
    [double] $GpuMaximumC = 85,

    [ValidateRange(1, 120)]
    [double] $DimmMaximumC = 75,

    [ValidateRange(1, 120)]
    [double] $ReadyCpuMaximumC = 65,

    [ValidateRange(1, 120)]
    [double] $ReadySystemMaximumC = 60,

    [ValidateRange(1, 120)]
    [double] $ReadyGpuMaximumC = 60,

    [ValidateRange(1, 120)]
    [double] $ReadyDimmMaximumC = 55
)

<#
.SYNOPSIS
Runs an A2 telemetry-only reproduction stage under two independent supervisors.

.DESCRIPTION
By default this is the A2 telemetry-only wrapper: the UM780 plugin must be
loaded and both controls must remain disabled. Optional expected-control
parameters support later staged tests while retaining the same cached sensor
guard, workload supervision, and fail-closed abort path. Active-control mode
validates that every sample is present, finite, and in range; a separate
sequencer is required to prove requested target transitions.

Run this script from an elevated PowerShell session.  OutputRoot must not exist.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utf8NoBom = [Text.UTF8Encoding]::new($false)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$resolvedFanControlRoot = [IO.Path]::GetFullPath($FanControlRoot)
$guardDuration = $Duration + 90
$pollMilliseconds = 250
$guardFreshnessSeconds = 5
$guardReadySeconds = 15
$watchdogHandshakeSeconds = 3
$abortExitGraceSeconds = 5
$guardHardGraceSeconds = 20

if ($RequireActiveCpuControl -and
        $PSBoundParameters.ContainsKey('ExpectedCpuControlPercent')) {
    throw 'RequireActiveCpuControl cannot be combined with ExpectedCpuControlPercent.'
}
$expectedCpuControlMode = if ($RequireActiveCpuControl) {
    'active'
}
elseif ($null -eq $ExpectedCpuControlPercent) {
    'disabled'
}
else {
    'exact'
}
$expectedSystemControlMode = if ($null -eq $ExpectedSystemControlPercent) {
    'disabled'
}
else {
    'exact'
}

if ($ReadyCpuMaximumC -gt $CpuMaximumC -or
        $ReadySystemMaximumC -gt $SystemMaximumC -or
        ($ExtendedThermalGuard -and
            ($ReadyGpuMaximumC -gt $GpuMaximumC -or
             $ReadyDimmMaximumC -gt $DimmMaximumC))) {
    throw 'Each admission temperature must be less than or equal to its guard maximum.'
}

$fanControlExe = Join-Path $resolvedFanControlRoot 'FanControl.exe'
$fanControlIpcAssembly = Join-Path $resolvedFanControlRoot 'FanControl.IPC.dll'
$guardExe = Join-Path $repoRoot `
    'diagnostics\FanControlIpc\bin\Release\net10.0-windows\FanControlIpc.exe'
$harnessScript = Join-Path $repoRoot 'diagnostics\Run-ReproductionStage.ps1'
$expectedCpuBurnPath = Join-Path $repoRoot `
    'diagnostics\CpuBurn\bin\Release\net10.0-windows\CpuBurn.exe'
$expectedWinSatPath = (Get-Command 'WinSAT.exe' -ErrorAction Stop).Source

$cpuRpmId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1'
$systemRpmId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2'
$cpuTemperatureId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature'
$systemTemperatureId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature'
$cpuControlId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-raw-v1'
$systemControlId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2'
$cpuPackageTemperatureId = '/amdcpu/0/temperature/2'
$gpuTemperatureId = '/gpu-amd/0/temperature/4'
$dimm0TemperatureId = '/memory/dimm/0/temperature/0'
$dimm1TemperatureId = '/memory/dimm/1/temperature/0'

$auxDirectory = Join-Path $resolvedOutputRoot 'aux'
$harnessDirectory = Join-Path $resolvedOutputRoot 'harness'
$wrapperLedgerPath = Join-Path $resolvedOutputRoot 'wrapper.jsonl'
$wrapperSummaryPath = Join-Path $resolvedOutputRoot 'summary.json'
$guardLedgerPath = Join-Path $auxDirectory 'guard.jsonl'
$guardAbortPath = Join-Path $auxDirectory 'guard.abort.json'
$guardSummaryPath = Join-Path $auxDirectory 'guard-summary.json'
$guardStdoutPath = Join-Path $auxDirectory 'guard.stdout.log'
$guardStderrPath = Join-Path $auxDirectory 'guard.stderr.log'
$harnessStdoutPath = Join-Path $auxDirectory 'harness.stdout.log'
$harnessStderrPath = Join-Path $auxDirectory 'harness.stderr.log'

$guardProcess = $null
$harnessProcess = $null
$guardExitCode = $null
$harnessExitCode = $null
$wrapperFailure = $null
$fallbackTargets = [System.Collections.Generic.List[object]]::new()
$watchdogTargets = [System.Collections.Generic.List[object]]::new()
$workloadStartSeenAt = $null
$watchdogStartSeen = $false
$abortObservedAt = $null
$guardStartedUtc = $null
$harnessStartedUtc = $null
$expectedFanControlIdentity = $null
$expectedGuardIdentity = $null
$expectedHarnessIdentity = $null
$pendingSummary = $null
$checkpointHandshakeComplete = $Workload -eq 'idle'
$expectedWorkloadCount = switch ($Workload) {
    'idle' { 0 }
    'combined' { 2 }
    default { 1 }
}

function Write-DurableJsonLine {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $line = ($Value | ConvertTo-Json -Depth 20 -Compress) +
        [Environment]::NewLine
    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::Append, [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream, $utf8NoBom, 4096, $true)
        try {
            $writer.Write($line)
            $writer.Flush()
            $stream.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-WrapperRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    Write-DurableJsonLine -Path $wrapperLedgerPath -Value ([ordered]@{
        Kind = $Kind
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        Data = $Data
    })
}

function Add-WrapperFailure {
    param([Parameter(Mandatory = $true)][string] $Message)

    $script:wrapperFailure = if ([string]::IsNullOrWhiteSpace($wrapperFailure)) {
        $Message
    }
    else {
        "$wrapperFailure; $Message"
    }
}

function Try-WriteWrapperRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    try {
        Write-WrapperRecord -Kind $Kind -Data $Data
    }
    catch {
        Add-WrapperFailure -Message (
            "failed to write wrapper record '$Kind': $($_.Exception.Message)")
    }
}

function Write-DurableNewJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $text = ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine
    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream, $utf8NoBom, 4096, $true)
        try {
            $writer.Write($text)
            $writer.Flush()
            $stream.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-DurableReplaceJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    $temporaryPath = Join-Path $directory (
        ".$(Split-Path -Leaf $Path).$([Guid]::NewGuid().ToString('N')).tmp")
    $backupPath = "$temporaryPath.backup"
    try {
        Write-DurableNewJson -Path $temporaryPath -Value $Value
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
            [IO.File]::Delete($backupPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [IO.File]::Delete($temporaryPath)
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            [IO.File]::Delete($backupPath)
        }
    }
}

function New-DurableAbort {
    param(
        [Parameter(Mandatory = $true)][string] $Reason,
        [hashtable] $Details = @{}
    )

    if (Test-Path -LiteralPath $guardAbortPath) {
        return
    }
    $abort = [ordered]@{
        Status = 'ABORT'
        Source = 'Run-A2TelemetryStage'
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        Reason = $Reason
        Details = $Details
    }
    try {
        Write-DurableNewJson -Path $guardAbortPath -Value $abort
        Write-WrapperRecord -Kind 'abort-created' -Data @{
            Reason = $Reason
            Details = $Details
        }
    }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $guardAbortPath)) {
            throw
        }
    }
}

function Get-JsonRecords {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }
    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $reader = [IO.StreamReader]::new(
            $stream, $utf8NoBom, $true, 4096, $true)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    $endsWithNewline = $text.EndsWith("`n", [StringComparison]::Ordinal)
    $lines = @($text -split "`r?`n")
    $records = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        try {
            $records.Add(($line | ConvertFrom-Json -ErrorAction Stop))
        }
        catch {
            $isIncompleteTail = $index -eq ($lines.Count - 1) -and
                -not $endsWithNewline
            if ($isIncompleteTail) {
                break
            }
            throw "Invalid JSON record in ${Path}: $($_.Exception.Message)"
        }
    }
    return $records.ToArray()
}

function Get-LastJsonRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    $records = @(Get-JsonRecords -Path $Path)
    if ($records.Count -eq 0) {
        return $null
    }
    return $records[-1]
}

function Get-ProcessPath {
    param([Parameter(Mandatory = $true)][Diagnostics.Process] $Process)

    $Process.Refresh()
    $path = $null
    try { $path = [string]$Process.Path } catch { }
    if ([string]::IsNullOrWhiteSpace($path)) {
        try { $path = [string]$Process.MainModule.FileName } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Unable to resolve process path for PID $($Process.Id)."
    }
    return [IO.Path]::GetFullPath($path)
}

function Get-ProcessIdentity {
    param([Parameter(Mandatory = $true)][Diagnostics.Process] $Process)

    return [ordered]@{
        Id = $Process.Id
        StartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        StartTimeUtc = $Process.StartTime.ToUniversalTime().ToString('o')
        Path = Get-ProcessPath -Process $Process
    }
}

function Test-IdentityAlive {
    param([Parameter(Mandatory = $true)] $Target)

    try {
        $process = Get-Process -Id ([int]$Target.Id) -ErrorAction Stop
        return $process.StartTime.ToUniversalTime().Ticks -eq
            [long]$Target.StartTimeUtcTicks
    }
    catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
        return $false
    }
    catch {
        throw "Unable to inspect exact process identity $($Target.Id): $($_.Exception.Message)"
    }
}

function Stop-TrackedProcessExact {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }
    $currentIdentity = Get-ProcessIdentity -Process $Process
    if ([long]$currentIdentity.StartTimeUtcTicks -ne
            [long]$Identity.StartTimeUtcTicks -or
            -not [StringComparer]::OrdinalIgnoreCase.Equals(
                [string]$currentIdentity.Path, [string]$Identity.Path)) {
        throw "Refusing to stop $Label because its process identity changed."
    }
    $Process.Kill()
    [void]$Process.WaitForExit(5000)
    if (-not $Process.HasExited) {
        throw "$Label did not exit after Kill()."
    }
}

function Stop-ExactTargets {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Targets,
        [Parameter(Mandatory = $true)][string] $Reason
    )

    $unique = @{}
    foreach ($target in @($Targets)) {
        if ($null -ne $target -and $null -ne $target.Id -and
                $null -ne $target.StartTimeUtcTicks) {
            $unique["$($target.Id)|$($target.StartTimeUtcTicks)"] = $target
        }
    }
    foreach ($target in @($unique.Values)) {
        try {
            $process = Get-Process -Id ([int]$target.Id) -ErrorAction Stop
            $process.EnableRaisingEvents = $true
            $null = $process.Handle
            if ($process.StartTime.ToUniversalTime().Ticks -ne
                    [long]$target.StartTimeUtcTicks) {
                continue
            }
            $identity = Get-ProcessIdentity -Process $process
            if ($null -ne $target.Path -and
                    -not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [string]$identity.Path, [string]$target.Path)) {
                continue
            }
            $process.Kill()
            [void]$process.WaitForExit(5000)
            if (-not $process.HasExited) {
                throw "PID $($process.Id) did not exit after Kill()."
            }
            Write-WrapperRecord -Kind 'exact-target-stopped' -Data @{
                Reason = $Reason
                Target = $identity
            }
        }
        catch {
            if (Test-IdentityAlive -Target $target) {
                throw
            }
            # The exact process has already exited.
        }
    }
}

function Get-LiveTargets {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Targets
    )

    return @($Targets | Where-Object { Test-IdentityAlive -Target $_ })
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Contains('"')) {
        throw 'Process arguments containing a quote are not supported.'
    }
    return '"' + $Value + '"'
}

function Stop-NewlyStartedProcess {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][long] $StartTimeUtcTicks,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }
    if ($Process.StartTime.ToUniversalTime().Ticks -ne $StartTimeUtcTicks) {
        throw "Refusing to stop $Label because its process identity changed during initialization."
    }
    $Process.Kill()
    [void]$Process.WaitForExit(5000)
    if (-not $Process.HasExited) {
        throw "$Label did not exit after launch cleanup."
    }
}

function Start-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList,
        [Parameter(Mandatory = $true)][string] $StandardOutput,
        [Parameter(Mandatory = $true)][string] $StandardError
    )

    $quoted = @($ArgumentList | ForEach-Object {
        Quote-ProcessArgument -Value ([string]$_)
    })
    $process = $null
    $startTimeUtcTicks = $null
    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $quoted `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $StandardOutput `
            -RedirectStandardError $StandardError
        $startTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        $identity = Get-ProcessIdentity -Process $process
        $process.EnableRaisingEvents = $true
        $null = $process.Handle
        return [pscustomobject]@{
            Process = $process
            Identity = $identity
        }
    }
    catch {
        $launchError = $_.Exception.Message
        $cleanupError = $null
        if ($null -ne $process) {
            try {
                if ($null -ne $startTimeUtcTicks) {
                    Stop-NewlyStartedProcess -Process $process `
                        -StartTimeUtcTicks $startTimeUtcTicks `
                        -Label 'newly started tracked process'
                }
                else {
                    # Start-Process returned this object, but identity capture
                    # failed before a start time was available. Kill through
                    # that original object as the best possible cleanup.
                    $process.Refresh()
                    if (-not $process.HasExited) {
                        $process.Kill()
                        [void]$process.WaitForExit(5000)
                        if (-not $process.HasExited) {
                            throw 'The newly started tracked process did not exit after launch cleanup.'
                        }
                    }
                }
            }
            catch {
                $cleanupError = $_.Exception.Message
            }
        }
        if ($cleanupError) {
            throw "Tracked-process initialization failed: $launchError Launch cleanup also failed: $cleanupError"
        }
        throw "Tracked-process initialization failed: $launchError"
    }
}

function Get-MonitoredValue {
    param(
        [Parameter(Mandatory = $true)] $Record,
        [Parameter(Mandatory = $true)][string] $Identifier
    )

    $property = $Record.Values.PSObject.Properties[$Identifier]
    if ($null -eq $property) {
        return [pscustomobject]@{ Found = $false; Value = $null }
    }
    return [pscustomobject]@{ Found = $true; Value = $property.Value }
}

function Test-ControlNumber {
    param([AllowNull()] $Value)

    if ($null -eq $Value) {
        return $false
    }
    $numericTypeCodes = @(
        [TypeCode]::SByte, [TypeCode]::Byte,
        [TypeCode]::Int16, [TypeCode]::UInt16,
        [TypeCode]::Int32, [TypeCode]::UInt32,
        [TypeCode]::Int64, [TypeCode]::UInt64,
        [TypeCode]::Single, [TypeCode]::Double,
        [TypeCode]::Decimal)
    if ($numericTypeCodes -notcontains
            [Type]::GetTypeCode($Value.GetType())) {
        return $false
    }
    $number = [double]$Value
    return -not [double]::IsNaN($number) -and
        -not [double]::IsInfinity($number) -and
        $number -ge 0 -and $number -le 100
}

function Test-ExpectedControlValue {
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)]
        [ValidateSet('disabled', 'exact', 'active')]
        [string] $Mode,
        [AllowNull()][Nullable[double]] $Expected
    )

    if (-not $Entry.Found) {
        return $false
    }
    if ($Mode -eq 'disabled') {
        return $null -eq $Entry.Value
    }
    if (-not (Test-ControlNumber -Value $Entry.Value)) {
        return $false
    }
    if ($Mode -eq 'active') {
        return $true
    }
    if ($null -eq $Expected) {
        return $false
    }
    return [Math]::Abs([double]$Entry.Value - [double]$Expected) -le 0.1
}

function Format-ExpectedControlArgument {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('disabled', 'exact', 'active')]
        [string] $Mode,
        [AllowNull()][Nullable[double]] $Value)

    if ($Mode -eq 'disabled') {
        return 'null'
    }
    if ($Mode -eq 'active') {
        return 'active'
    }
    if ($null -eq $Value) {
        throw 'An exact control expectation requires a percentage.'
    }
    return [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture,
        '{0:0.######}',
        [double]$Value)
}

function Test-ExpectedControlSummaryValue {
    param(
        [AllowNull()] $ActualMode,
        [AllowNull()] $ActualPercent,
        [Parameter(Mandatory = $true)]
        [ValidateSet('disabled', 'exact', 'active')]
        [string] $ExpectedMode,
        [AllowNull()][Nullable[double]] $Expected
    )

    if ($ActualMode -isnot [string] -or
            -not [StringComparer]::Ordinal.Equals(
                [string]$ActualMode, $ExpectedMode)) {
        return $false
    }
    if ($ExpectedMode -ne 'exact') {
        return $null -eq $ActualPercent
    }
    if (-not (Test-ControlNumber -Value $ActualPercent) -or
            $null -eq $Expected) {
        return $false
    }
    return [Math]::Abs(
        [double]$ActualPercent - [double]$Expected) -le 0.0001
}

function Test-GuardReadyRecord {
    param([Parameter(Mandatory = $true)] $Record)

    if ($Record.StartupGraceActive -ne $false -or $Record.Error -or
            @($Record.Violations).Count -ne 0) {
        return $false
    }
    $requiredTelemetryIds = @(
        $cpuRpmId, $systemRpmId, $cpuTemperatureId,
        $systemTemperatureId)
    if ($ExtendedThermalGuard) {
        $requiredTelemetryIds += @(
            $cpuPackageTemperatureId, $gpuTemperatureId,
            $dimm0TemperatureId, $dimm1TemperatureId)
    }
    foreach ($identifier in $requiredTelemetryIds) {
        $entry = Get-MonitoredValue -Record $Record -Identifier $identifier
        if (-not $entry.Found -or $null -eq $entry.Value) {
            return $false
        }
    }
    $readyCpu = Get-MonitoredValue -Record $Record `
        -Identifier $cpuTemperatureId
    $readySystem = Get-MonitoredValue -Record $Record `
        -Identifier $systemTemperatureId
    if ([double]$readyCpu.Value -gt $readyCpuMaximumC -or
            [double]$readySystem.Value -gt $readySystemMaximumC) {
        return $false
    }
    if ($ExtendedThermalGuard) {
        $readyCpuPackage = Get-MonitoredValue -Record $Record `
            -Identifier $cpuPackageTemperatureId
        $readyGpu = Get-MonitoredValue -Record $Record `
            -Identifier $gpuTemperatureId
        $readyDimm0 = Get-MonitoredValue -Record $Record `
            -Identifier $dimm0TemperatureId
        $readyDimm1 = Get-MonitoredValue -Record $Record `
            -Identifier $dimm1TemperatureId
        if ([double]$readyCpuPackage.Value -gt $readyCpuMaximumC -or
                [double]$readyGpu.Value -gt $readyGpuMaximumC -or
                [double]$readyDimm0.Value -gt $readyDimmMaximumC -or
                [double]$readyDimm1.Value -gt $readyDimmMaximumC) {
            return $false
        }
    }
    $cpuControl = Get-MonitoredValue -Record $Record `
        -Identifier $cpuControlId
    $systemControl = Get-MonitoredValue -Record $Record `
        -Identifier $systemControlId
    if (-not (Test-ExpectedControlValue -Entry $cpuControl `
                -Mode $expectedCpuControlMode `
                -Expected $ExpectedCpuControlPercent) -or
            -not (Test-ExpectedControlValue -Entry $systemControl `
                -Mode $expectedSystemControlMode `
                -Expected $ExpectedSystemControlPercent)) {
        return $false
    }
    if ($MinimumSystemRpm -gt 0) {
        $systemRpm = Get-MonitoredValue -Record $Record `
            -Identifier $systemRpmId
        if (-not $systemRpm.Found -or $null -eq $systemRpm.Value -or
                [double]$systemRpm.Value -lt $MinimumSystemRpm) {
            return $false
        }
    }
    return $true
}

function Test-GuardRecordFresh {
    param(
        [Parameter(Mandatory = $true)] $Record,
        [Parameter(Mandatory = $true)][double] $MaximumAgeSeconds
    )

    if ($null -eq $Record.Utc -or $null -eq $Record.Sequence -or
            $null -eq $Record.MonotonicMilliseconds) {
        return $false
    }
    $recordUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$Record.Utc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$recordUtc)) {
        return $false
    }
    $ageSeconds = ([DateTimeOffset]::UtcNow -
        $recordUtc.ToUniversalTime()).TotalSeconds
    return $ageSeconds -ge -1 -and $ageSeconds -le $MaximumAgeSeconds
}

function Test-LedgerFresh {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][double] $MaximumAgeSeconds
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $age = [DateTime]::UtcNow - (Get-Item -LiteralPath $Path).LastWriteTimeUtc
    return $age.TotalSeconds -ge -1 -and
        $age.TotalSeconds -le $MaximumAgeSeconds
}

function Capture-FallbackTargets {
    param([Parameter(Mandatory = $true)][object[]] $CheckpointRecords)

    foreach ($record in @($CheckpointRecords | Where-Object {
            $_.Kind -eq 'workload-start'
        })) {
        $key = [string]$record.Data.ProcessId
        if (@($fallbackTargets | Where-Object { [string]$_.Id -eq $key }).Count) {
            continue
        }
        try {
            $process = Get-Process -Id ([int]$record.Data.ProcessId) `
                -ErrorAction Stop
            $identity = Get-ProcessIdentity -Process $process
            $expectedPath = [IO.Path]::GetFullPath([string]$record.Data.FilePath)
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$identity.Path, $expectedPath)) {
                throw "Workload PID $($identity.Id) path did not match its checkpoint."
            }
            $fallbackTargets.Add($identity)
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # A very short-lived workload will be represented by its exit record.
        }
    }
}

function Refresh-WatchdogEvidence {
    if (-not (Test-Path -LiteralPath $harnessDirectory)) {
        return @()
    }
    $path = Join-Path $harnessDirectory 'workload-watchdog.jsonl'
    $records = @(Get-JsonRecords -Path $path)
    $starts = @($records | Where-Object { $_.Kind -eq 'watchdog-start' })
    if ($starts.Count -gt 0) {
        $script:watchdogStartSeen = $true
        foreach ($target in @($starts[-1].Targets)) {
            $key = "$($target.Id)|$($target.StartTimeUtcTicks)"
            if (@($watchdogTargets | Where-Object {
                    "$($_.Id)|$($_.StartTimeUtcTicks)" -eq $key
                }).Count -eq 0) {
                $watchdogTargets.Add($target)
            }
        }
    }
    return $records
}

function Capture-DirectHarnessWorkloads {
    if ($null -eq $harnessProcess) {
        return
    }
    if ($null -eq $expectedHarnessIdentity) {
        throw 'The harness process identity was not captured.'
    }
    if (-not (Test-IdentityAlive -Target $expectedHarnessIdentity)) {
        return
    }
    $rows = @(Get-CimInstance Win32_Process -Filter (
        "ParentProcessId = $($harnessProcess.Id)") -ErrorAction Stop |
        Where-Object { $_.Name -in @('CpuBurn.exe', 'WinSAT.exe') })
    foreach ($row in $rows) {
        try {
            $process = Get-Process -Id ([int]$row.ProcessId) -ErrorAction Stop
            $identity = Get-ProcessIdentity -Process $process
            $allowed = [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$identity.Path,
                    [IO.Path]::GetFullPath($expectedCpuBurnPath)) -or
                [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$identity.Path,
                    [IO.Path]::GetFullPath($expectedWinSatPath))
            if (-not $allowed) {
                throw "Refusing to capture unexpected direct harness child: $($identity.Path)"
            }
            $key = "$($identity.Id)|$($identity.StartTimeUtcTicks)"
            if (@($fallbackTargets | Where-Object {
                    "$($_.Id)|$($_.StartTimeUtcTicks)" -eq $key
                }).Count -eq 0) {
                $fallbackTargets.Add($identity)
                Write-WrapperRecord -Kind 'direct-workload-captured' -Data @{
                    Target = $identity
                }
            }
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # The direct child exited between the CIM and process snapshots.
        }
    }
}

function Get-GlobalWorkloadRows {
    return @(Get-CimInstance Win32_Process -Filter (
        "Name = 'CpuBurn.exe' OR Name = 'WinSAT.exe'") -ErrorAction Stop)
}

function Refresh-CleanupEvidence {
    if (Test-Path -LiteralPath $harnessDirectory -PathType Container) {
        $checkpointPath = Join-Path $harnessDirectory 'checkpoint.jsonl'
        $checkpoints = @(Get-JsonRecords -Path $checkpointPath)
        if ($checkpoints.Count -ne 0) {
            Capture-FallbackTargets -CheckpointRecords $checkpoints
        }
        [void](Refresh-WatchdogEvidence)
    }
    if ($null -ne $harnessProcess -and
            $null -ne $expectedHarnessIdentity -and
            (Test-IdentityAlive -Target $expectedHarnessIdentity)) {
        Capture-DirectHarnessWorkloads
    }
}

function Test-SafeToStopGuard {
    Refresh-CleanupEvidence
    $targets = @($watchdogTargets.ToArray() + $fallbackTargets.ToArray())
    if (@(Get-LiveTargets -Targets $targets).Count -ne 0) {
        return $false
    }
    if (@(Get-GlobalWorkloadRows).Count -ne 0) {
        return $false
    }
    if ($null -ne $harnessProcess) {
        if ($null -eq $expectedHarnessIdentity -or
                (Test-IdentityAlive -Target $expectedHarnessIdentity)) {
            return $false
        }
    }
    return $true
}

function Stop-LingeringHarness {
    param([Parameter(Mandatory = $true)][string] $Reason)

    if ($null -eq $harnessProcess) {
        return
    }
    if ($null -eq $expectedHarnessIdentity) {
        throw 'Refusing cleanup because the harness identity was not captured.'
    }
    $checkpointPath = Join-Path $harnessDirectory 'checkpoint.jsonl'
    $checkpoints = @(Get-JsonRecords -Path $checkpointPath)
    if ($checkpoints.Count -ne 0) {
        Capture-FallbackTargets -CheckpointRecords $checkpoints
    }
    [void](Refresh-WatchdogEvidence)
    if (Test-IdentityAlive -Target $expectedHarnessIdentity) {
        Capture-DirectHarnessWorkloads
    }
    Stop-ExactTargets -Targets @($watchdogTargets.ToArray() +
        $fallbackTargets.ToArray()) -Reason $Reason
    if (Test-IdentityAlive -Target $expectedHarnessIdentity) {
        Capture-DirectHarnessWorkloads
    }
    $remaining = @(Get-LiveTargets -Targets @(
        $watchdogTargets.ToArray() + $fallbackTargets.ToArray()))
    if ($remaining.Count -ne 0) {
        throw 'Refusing to stop the harness while an exact workload target remains.'
    }
    $unverifiedWorkloads = @(Get-GlobalWorkloadRows)
    if ($unverifiedWorkloads.Count -ne 0) {
        throw 'Refusing to stop the harness while an unverified workload process remains.'
    }
    if (Test-IdentityAlive -Target $expectedHarnessIdentity) {
        Stop-TrackedProcessExact -Process $harnessProcess `
            -Identity $expectedHarnessIdentity -Label 'A2 harness'
        Write-WrapperRecord -Kind 'harness-stopped' -Data @{
            Reason = $Reason
            ProcessId = $harnessProcess.Id
        }
    }
}

function Wait-ForGuardAfterFailure {
    if ($null -eq $guardProcess) {
        return
    }
    $hardDeadline = if ($null -ne $guardStartedUtc) {
        $guardStartedUtc.AddSeconds($guardDuration + $guardHardGraceSeconds)
    }
    else {
        [DateTimeOffset]::UtcNow.AddSeconds($guardHardGraceSeconds)
    }
    $lastCleanupError = $null
    $guardProcess.Refresh()
    while (-not $guardProcess.HasExited) {
        try {
            if ($null -ne $harnessProcess) {
                Stop-LingeringHarness -Reason 'failure-supervised-cleanup'
            }
            if (Test-SafeToStopGuard) {
                Stop-TrackedProcessExact -Process $guardProcess `
                    -Identity $expectedGuardIdentity -Label 'A2 guard'
                Write-WrapperRecord -Kind 'guard-stopped-after-safe-cleanup' `
                    -Data @{ ProcessId = $guardProcess.Id }
                break
            }
        }
        catch {
            $lastCleanupError = $_.Exception.Message
        }
        if ([DateTimeOffset]::UtcNow -ge $hardDeadline) {
            break
        }
        Start-Sleep -Milliseconds $pollMilliseconds
        $guardProcess.Refresh()
    }
    if (-not $guardProcess.HasExited) {
        $detail = if ($lastCleanupError) {
            " Last cleanup error: $lastCleanupError"
        }
        else { '' }
        throw "The guard was deliberately left running because workload absence was not proven before the hard deadline.$detail"
    }
    if ($guardProcess.HasExited) {
        [void]$guardProcess.WaitForExit()
        $script:guardExitCode = $guardProcess.ExitCode
        Write-WrapperRecord -Kind 'guard-exit-after-failure' -Data @{
            ExitCode = $guardExitCode
        }
    }
}

function Assert-AdministrativeSession {
    $principal = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run-A2TelemetryStage.ps1 requires an elevated PowerShell session.'
    }
}

function Assert-StaticPreflight {
    Assert-AdministrativeSession
    if (Test-Path -LiteralPath $resolvedOutputRoot) {
        throw "OutputRoot must not exist: $resolvedOutputRoot"
    }
    foreach ($path in @(
            $fanControlExe, $fanControlIpcAssembly, $guardExe,
            $harnessScript)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required file is missing: $path"
        }
    }
    $fanControl = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    if ($fanControl.Count -ne 1) {
        throw "A2 requires exactly one FanControl process; found $($fanControl.Count)."
    }
    if (-not $fanControl[0].Responding) {
        throw 'FanControl is not responding.'
    }
    $script:expectedFanControlIdentity =
        Get-ProcessIdentity -Process $fanControl[0]
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            [string]$expectedFanControlIdentity.Path,
            [IO.Path]::GetFullPath($fanControlExe))) {
        throw 'The running FanControl executable is not the configured V272 path.'
    }
    $conflicts = @(Get-Process CpuBurn, WinSAT -ErrorAction SilentlyContinue)
    if ($conflicts.Count -ne 0) {
        throw "A workload is already running: $($conflicts.ProcessName -join ', ')"
    }
}

Assert-StaticPreflight
[IO.Directory]::CreateDirectory($resolvedOutputRoot) | Out-Null
[IO.Directory]::CreateDirectory($auxDirectory) | Out-Null
Write-WrapperRecord -Kind 'wrapper-start' -Data @{
    Name = $Name
    Workload = $Workload
    DurationSeconds = $Duration
    GuardDurationSeconds = $guardDuration
    CpuMaximumC = $CpuMaximumC
    SystemMaximumC = $SystemMaximumC
    ReadyCpuMaximumC = $readyCpuMaximumC
    ReadySystemMaximumC = $readySystemMaximumC
    ExtendedThermalGuard = [bool]$ExtendedThermalGuard
    GpuMaximumC = $GpuMaximumC
    DimmMaximumC = $DimmMaximumC
    ReadyGpuMaximumC = $readyGpuMaximumC
    ReadyDimmMaximumC = $readyDimmMaximumC
    ExpectedCpuControlPercent = $ExpectedCpuControlPercent
    ExpectedCpuControlMode = $expectedCpuControlMode
    ExpectedSystemControlPercent = $ExpectedSystemControlPercent
    ExpectedSystemControlMode = $expectedSystemControlMode
    MinimumSystemRpm = $MinimumSystemRpm
    FanControl = $expectedFanControlIdentity
}

try {
    $cpuMaximumText = [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture, '{0:0.###}',
        $CpuMaximumC)
    $systemMaximumText = [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture, '{0:0.###}',
        $SystemMaximumC)
    $expectedCpuControlText = Format-ExpectedControlArgument `
        -Mode $expectedCpuControlMode `
        -Value $ExpectedCpuControlPercent
    $expectedSystemControlText = Format-ExpectedControlArgument `
        -Mode $expectedSystemControlMode `
        -Value $ExpectedSystemControlPercent
    $gpuMaximumText = [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture, '{0:0.###}',
        $GpuMaximumC)
    $dimmMaximumText = [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture, '{0:0.###}',
        $DimmMaximumC)
    $guardArguments = @(
        $fanControlIpcAssembly,
        'guard',
        [string]$guardDuration,
        $guardLedgerPath,
        $guardAbortPath,
        $cpuMaximumText,
        $systemMaximumText,
        $expectedCpuControlText,
        $expectedSystemControlText,
        [string]$MinimumSystemRpm)
    if ($ExtendedThermalGuard) {
        $guardArguments += @($gpuMaximumText, $dimmMaximumText)
    }
    $guardArguments += @('--output', $guardSummaryPath)
    $guardLaunch = Start-TrackedProcess -FilePath $guardExe `
        -ArgumentList $guardArguments -StandardOutput $guardStdoutPath `
        -StandardError $guardStderrPath
    $guardProcess = $guardLaunch.Process
    $expectedGuardIdentity = $guardLaunch.Identity
    $guardStartedUtc = [DateTimeOffset]::UtcNow
    $guardProcess.PriorityClass =
        [Diagnostics.ProcessPriorityClass]::High
    $guardProcess.PriorityBoostEnabled = $true
    Write-WrapperRecord -Kind 'guard-start' -Data @{
        ProcessId = $guardProcess.Id
        DurationSeconds = $guardDuration
        LedgerPath = $guardLedgerPath
        AbortPath = $guardAbortPath
        PriorityClass = [string]$guardProcess.PriorityClass
        PriorityBoostEnabled = [bool]$guardProcess.PriorityBoostEnabled
    }

    $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds($guardReadySeconds)
    $readyRecord = $null
    $previousReadyRecord = $null
    while ([DateTimeOffset]::UtcNow -lt $readyDeadline) {
        $guardProcess.Refresh()
        if ($guardProcess.HasExited) {
            [void]$guardProcess.WaitForExit()
            $guardExitCode = $guardProcess.ExitCode
            throw "The A2 guard exited before readiness with code $guardExitCode."
        }
        if (Test-Path -LiteralPath $guardAbortPath) {
            throw 'The A2 guard created an abort before readiness.'
        }
        $candidate = Get-LastJsonRecord -Path $guardLedgerPath
        if ($null -ne $candidate -and
                (Test-GuardReadyRecord -Record $candidate) -and
                (Test-GuardRecordFresh -Record $candidate `
                    -MaximumAgeSeconds $guardFreshnessSeconds) -and
                (Test-LedgerFresh -Path $guardLedgerPath `
                    -MaximumAgeSeconds $guardFreshnessSeconds)) {
            if ($null -ne $previousReadyRecord -and
                    [long]$candidate.Sequence -eq
                        ([long]$previousReadyRecord.Sequence + 1) -and
                    [double]$candidate.MonotonicMilliseconds -gt
                        [double]$previousReadyRecord.MonotonicMilliseconds) {
                $readyRecord = $candidate
                break
            }
            $previousReadyRecord = $candidate
        }
        else {
            $previousReadyRecord = $null
        }
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    if ($null -eq $readyRecord) {
        throw "The A2 guard did not produce complete post-grace telemetry within $guardReadySeconds seconds."
    }
    Write-WrapperRecord -Kind 'guard-ready' -Data @{
        Sequence = $readyRecord.Sequence
        Values = $readyRecord.Values
    }

    $fanControlBeforeHarness = @(
        Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    if ($fanControlBeforeHarness.Count -ne 1 -or
            -not (Test-IdentityAlive -Target $expectedFanControlIdentity) -or
            -not $fanControlBeforeHarness[0].Responding) {
        throw 'Fan Control changed or stopped responding after guard readiness.'
    }
    $guardProcess.Refresh()
    if ($guardProcess.HasExited) {
        [void]$guardProcess.WaitForExit()
        $guardExitCode = $guardProcess.ExitCode
        throw "The A2 guard exited before harness launch with code $guardExitCode."
    }
    if (Test-Path -LiteralPath $guardAbortPath) {
        throw 'The A2 guard created an abort before harness launch.'
    }
    $launchRecord = Get-LastJsonRecord -Path $guardLedgerPath
    if ($null -eq $launchRecord -or
            -not (Test-GuardReadyRecord -Record $launchRecord) -or
            -not (Test-GuardRecordFresh -Record $launchRecord `
                -MaximumAgeSeconds $guardFreshnessSeconds) -or
            [long]$launchRecord.Sequence -lt [long]$readyRecord.Sequence) {
        throw 'Guard telemetry was no longer ready immediately before harness launch.'
    }

    $hostProcess = Get-Process -Id $PID -ErrorAction Stop
    $hostPath = Get-ProcessPath -Process $hostProcess
    $harnessArguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $harnessScript,
        '-Name',
        $Name,
        '-Workload',
        $Workload,
        '-Duration',
        [string]$Duration,
        '-OutputDirectory',
        $harnessDirectory,
        '-FanControlRequired',
        '-ExternalAbortPath',
        $guardAbortPath)
    $harnessLaunch = Start-TrackedProcess -FilePath $hostPath `
        -ArgumentList $harnessArguments -StandardOutput $harnessStdoutPath `
        -StandardError $harnessStderrPath
    $harnessProcess = $harnessLaunch.Process
    $expectedHarnessIdentity = $harnessLaunch.Identity
    $harnessStartedUtc = [DateTimeOffset]::UtcNow
    Write-WrapperRecord -Kind 'harness-start' -Data @{
        ProcessId = $harnessProcess.Id
        OutputDirectory = $harnessDirectory
    }

    while ($true) {
        $harnessProcess.Refresh()
        $guardProcess.Refresh()
        if ($harnessProcess.HasExited) {
            break
        }

        if ($guardProcess.HasExited) {
            [void]$guardProcess.WaitForExit()
            $guardExitCode = $guardProcess.ExitCode
            New-DurableAbort -Reason 'guard-exited-before-harness' -Details @{
                ExitCode = $guardExitCode
            }
        }
        elseif (-not (Test-LedgerFresh -Path $guardLedgerPath `
                -MaximumAgeSeconds $guardFreshnessSeconds)) {
            New-DurableAbort -Reason 'guard-ledger-stale' -Details @{
                MaximumAgeSeconds = $guardFreshnessSeconds
            }
        }

        if (-not $checkpointHandshakeComplete) {
            $checkpointPath = Join-Path $harnessDirectory 'checkpoint.jsonl'
            $checkpoints = @(Get-JsonRecords -Path $checkpointPath)
            $workloadStarts = @($checkpoints | Where-Object {
                $_.Kind -eq 'workload-start'
            })
            if ($checkpoints.Count -ne 0) {
                Capture-FallbackTargets -CheckpointRecords $checkpoints
                if ($null -eq $workloadStartSeenAt -and
                        $workloadStarts.Count -ne 0) {
                    $workloadStartSeenAt = [DateTimeOffset]::UtcNow
                    Write-WrapperRecord -Kind 'workload-start-observed'
                }
            }
            [void](Refresh-WatchdogEvidence)
            if ($workloadStarts.Count -ge $expectedWorkloadCount -and
                    $watchdogStartSeen) {
                $checkpointHandshakeComplete = $true
                Write-WrapperRecord -Kind 'workload-handshake-complete' -Data @{
                    WorkloadStartCount = $workloadStarts.Count
                }
            }
        }
        if ($null -ne $workloadStartSeenAt -and -not $watchdogStartSeen -and
                ([DateTimeOffset]::UtcNow - $workloadStartSeenAt).TotalSeconds -gt
                    $watchdogHandshakeSeconds) {
            New-DurableAbort -Reason 'workload-watchdog-start-missing' `
                -Details @{ TimeoutSeconds = $watchdogHandshakeSeconds }
        }

        if (Test-Path -LiteralPath $guardAbortPath) {
            if ($null -eq $abortObservedAt) {
                $abortObservedAt = [DateTimeOffset]::UtcNow
                Write-WrapperRecord -Kind 'abort-observed' -Data @{
                    Path = $guardAbortPath
                }
            }
            elseif (([DateTimeOffset]::UtcNow - $abortObservedAt).TotalSeconds -gt
                    $abortExitGraceSeconds) {
                Stop-LingeringHarness -Reason 'abort-exit-grace-expired'
            }
        }
        Start-Sleep -Milliseconds $pollMilliseconds
    }

    [void]$harnessProcess.WaitForExit()
    $harnessExitCode = $harnessProcess.ExitCode
    Write-WrapperRecord -Kind 'harness-exit' -Data @{
        ExitCode = $harnessExitCode
    }

    $finalCheckpointPath = Join-Path $harnessDirectory 'checkpoint.jsonl'
    $finalCheckpoints = @(Get-JsonRecords -Path $finalCheckpointPath)
    if ($finalCheckpoints.Count -ne 0) {
        Capture-FallbackTargets -CheckpointRecords $finalCheckpoints
    }
    $finalWatchdogRecords = @(Refresh-WatchdogEvidence)
    $allTargets = @($watchdogTargets.ToArray() + $fallbackTargets.ToArray())
    $liveTargets = @(Get-LiveTargets -Targets $allTargets)
    if ($liveTargets.Count -ne 0) {
        New-DurableAbort -Reason 'workload-remained-after-harness' -Details @{
            Targets = $liveTargets
        }
        Stop-ExactTargets -Targets $liveTargets `
            -Reason 'workload-remained-after-harness'
    }

    $guardHardDeadline = $guardStartedUtc.AddSeconds(
        $guardDuration + $guardHardGraceSeconds)
    while (-not $guardProcess.HasExited) {
        if (-not (Test-LedgerFresh -Path $guardLedgerPath `
                -MaximumAgeSeconds $guardFreshnessSeconds)) {
            New-DurableAbort -Reason 'guard-ledger-stale-after-harness' `
                -Details @{ MaximumAgeSeconds = $guardFreshnessSeconds }
        }
        if ([DateTimeOffset]::UtcNow -ge $guardHardDeadline) {
            New-DurableAbort -Reason 'guard-runtime-deadline' -Details @{
                DeadlineUtc = $guardHardDeadline.ToString('o')
            }
            if (-not (Test-SafeToStopGuard)) {
                throw 'The guard exceeded its runtime deadline while workload absence was not proven; it was left running.'
            }
            Stop-TrackedProcessExact -Process $guardProcess `
                -Identity $expectedGuardIdentity -Label 'A2 guard'
            break
        }
        Start-Sleep -Milliseconds $pollMilliseconds
        $guardProcess.Refresh()
    }
    [void]$guardProcess.WaitForExit()
    $guardExitCode = $guardProcess.ExitCode
    Write-WrapperRecord -Kind 'guard-exit' -Data @{
        ExitCode = $guardExitCode
    }

    $problems = [System.Collections.Generic.List[string]]::new()
    if ($harnessExitCode -ne 0) {
        $problems.Add("harness-exit-$harnessExitCode")
    }
    if ($guardExitCode -ne 0) {
        $problems.Add("guard-exit-$guardExitCode")
    }
    if (Test-Path -LiteralPath $guardAbortPath) {
        $problems.Add('abort-present')
    }

    $harnessRecords = $finalCheckpoints
    $stageCompletes = @($harnessRecords | Where-Object {
        $_.Kind -eq 'stage-complete' -and $_.Data.Status -eq 'completed'
    })
    $stageEnds = @($harnessRecords | Where-Object { $_.Kind -eq 'stage-end' })
    $stageFailures = @($harnessRecords | Where-Object { $_.Kind -eq 'stage-failure' })
    if ($stageCompletes.Count -ne 1 -or $stageEnds.Count -ne 1 -or
            $stageEnds[0].Data.Status -ne 'completed' -or
            $stageFailures.Count -ne 0) {
        $problems.Add('harness-stage-end-not-completed')
    }

    $guardSummary = $null
    if (Test-Path -LiteralPath $guardSummaryPath -PathType Leaf) {
        try {
            $guardSummary = [IO.File]::ReadAllText($guardSummaryPath) |
                ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            $problems.Add('guard-summary-invalid')
        }
    }
    else {
        $problems.Add('guard-summary-missing')
    }
    $guardRecords = @()
    try {
        $guardRecords = @(Get-JsonRecords -Path $guardLedgerPath)
    }
    catch {
        $problems.Add('guard-ledger-invalid')
    }
    if ($null -ne $guardSummary) {
        try {
            if ($guardSummary.Status -ne 'OK') {
                $problems.Add('guard-summary-not-ok')
            }
            if ($guardSummary.AbortCreated -ne $false) {
                $problems.Add('guard-summary-abort-created-invalid')
            }
            if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [IO.Path]::GetFullPath([string]$guardSummary.Path),
                    [IO.Path]::GetFullPath($guardLedgerPath)) -or
                    -not [StringComparer]::OrdinalIgnoreCase.Equals(
                        [IO.Path]::GetFullPath([string]$guardSummary.AbortPath),
                        [IO.Path]::GetFullPath($guardAbortPath))) {
                $problems.Add('guard-summary-path-mismatch')
            }
            if ([Math]::Abs([double]$guardSummary.CpuMaximumC -
                        $CpuMaximumC) -gt 0.0001 -or
                    [Math]::Abs([double]$guardSummary.SystemMaximumC -
                        $SystemMaximumC) -gt 0.0001) {
                $problems.Add('guard-summary-threshold-mismatch')
            }
            if ($ExtendedThermalGuard) {
                if ([Math]::Abs([double]$guardSummary.GpuMaximumC -
                            $GpuMaximumC) -gt 0.0001 -or
                        [Math]::Abs([double]$guardSummary.DimmMaximumC -
                            $DimmMaximumC) -gt 0.0001) {
                    $problems.Add('guard-summary-extended-threshold-mismatch')
                }
            }
            elseif ($null -ne $guardSummary.GpuMaximumC -or
                    $null -ne $guardSummary.DimmMaximumC) {
                $problems.Add('guard-summary-unexpected-extended-threshold')
            }
            if (-not (Test-ExpectedControlSummaryValue `
                        -ActualMode $guardSummary.ExpectedCpuControlMode `
                        -ActualPercent $guardSummary.ExpectedCpuControlPercent `
                        -ExpectedMode $expectedCpuControlMode `
                        -Expected $ExpectedCpuControlPercent) -or
                    -not (Test-ExpectedControlSummaryValue `
                        -ActualMode $guardSummary.ExpectedSystemControlMode `
                        -ActualPercent $guardSummary.ExpectedSystemControlPercent `
                        -ExpectedMode $expectedSystemControlMode `
                        -Expected $ExpectedSystemControlPercent) -or
                    [int]$guardSummary.MinimumSystemRpm -ne
                        $MinimumSystemRpm) {
                $problems.Add('guard-summary-control-expectation-mismatch')
            }
            $summarySamples = [long]$guardSummary.Samples
            $summarySuccessful = [long]$guardSummary.SuccessfulRpcSamples
            $summaryDuration = [double]$guardSummary.DurationSeconds
            if ($summarySamples -lt 1 -or
                    $summarySuccessful -ne $summarySamples -or
                    $guardRecords.Count -ne $summarySamples) {
                $problems.Add('guard-summary-sample-count-invalid')
            }
            if ($summaryDuration -lt $guardDuration -or
                    $summaryDuration -gt
                        ($guardDuration + $guardHardGraceSeconds)) {
                $problems.Add('guard-summary-duration-invalid')
            }
        }
        catch {
            $problems.Add('guard-summary-schema-invalid')
        }
    }
    if ($guardRecords.Count -ne 0) {
        try {
            for ($index = 0; $index -lt $guardRecords.Count; $index++) {
                $record = $guardRecords[$index]
                if ([long]$record.Sequence -ne $index -or $record.Error -or
                        @($record.Violations).Count -ne 0) {
                    throw "invalid guard record at sequence $index"
                }
                $cpuControl = Get-MonitoredValue -Record $record `
                    -Identifier $cpuControlId
                $systemControl = Get-MonitoredValue -Record $record `
                    -Identifier $systemControlId
                if (-not (Test-ExpectedControlValue -Entry $cpuControl `
                            -Mode $expectedCpuControlMode `
                            -Expected $ExpectedCpuControlPercent) -or
                        -not (Test-ExpectedControlValue -Entry $systemControl `
                            -Mode $expectedSystemControlMode `
                            -Expected $ExpectedSystemControlPercent)) {
                    throw "control state invalid at sequence $index"
                }
                if ($MinimumSystemRpm -gt 0) {
                    $systemRpm = Get-MonitoredValue -Record $record `
                        -Identifier $systemRpmId
                    if (-not $systemRpm.Found -or
                            $null -eq $systemRpm.Value -or
                            [double]$systemRpm.Value -lt $MinimumSystemRpm) {
                        throw "system RPM invalid at sequence $index"
                    }
                }
                if ($record.StartupGraceActive -eq $false) {
                    $requiredTelemetryIds = @(
                        $cpuRpmId, $systemRpmId, $cpuTemperatureId,
                        $systemTemperatureId)
                    if ($ExtendedThermalGuard) {
                        $requiredTelemetryIds += @(
                            $cpuPackageTemperatureId, $gpuTemperatureId,
                            $dimm0TemperatureId, $dimm1TemperatureId)
                    }
                    foreach ($identifier in $requiredTelemetryIds) {
                        $entry = Get-MonitoredValue -Record $record `
                            -Identifier $identifier
                        if (-not $entry.Found -or $null -eq $entry.Value) {
                            throw "telemetry missing at sequence $index"
                        }
                    }
                }
            }
            if ($guardRecords[-1].StartupGraceActive -ne $false) {
                throw 'guard never left startup grace'
            }
        }
        catch {
            $problems.Add('guard-ledger-consistency-invalid')
        }
    }

    if ($Workload -ne 'idle') {
        $watchdogStarts = @($finalWatchdogRecords | Where-Object {
            $_.Kind -eq 'watchdog-start'
        })
        $watchdogTerminals = @($finalWatchdogRecords | Where-Object {
            $_.Kind -in @('workloads-exited', 'watchdog-stopped',
                'deadline-enforced')
        })
        $watchdogDeadlines = @($finalWatchdogRecords | Where-Object {
            $_.Kind -eq 'deadline-enforced'
        })
        $watchdogInspectionErrors = @($finalWatchdogRecords | Where-Object {
            $_.Kind -eq 'watchdog-inspection-error'
        })
        if ($watchdogStarts.Count -ne 1 -or
                $watchdogTerminals.Count -ne 1) {
            $problems.Add('workload-watchdog-evidence-invalid')
        }
        if ($watchdogDeadlines.Count -ne 0) {
            $problems.Add('workload-watchdog-enforced-deadline')
        }
        if ($watchdogInspectionErrors.Count -ne 0) {
            $problems.Add('workload-watchdog-inspection-error')
        }
    }
    else {
        $idleWatchdogStarts = @($finalWatchdogRecords | Where-Object {
            $_.Kind -eq 'watchdog-start'
        })
        if ($idleWatchdogStarts.Count -ne 0) {
            $problems.Add('idle-workload-watchdog-unexpected')
        }
    }

    $remainingTargets = @(Get-LiveTargets -Targets $allTargets)
    if ($remainingTargets.Count -ne 0) {
        $problems.Add('exact-workload-target-still-running')
    }
    if (@(Get-GlobalWorkloadRows).Count -ne 0) {
        $problems.Add('global-workload-process-still-running')
    }
    $fanControl = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    if ($fanControl.Count -ne 1 -or
            -not (Test-IdentityAlive -Target $expectedFanControlIdentity) -or
            -not $fanControl[0].Responding) {
        $problems.Add('fancontrol-identity-changed')
    }

    if ($problems.Count -ne 0) {
        throw "Guarded stage failed: $($problems -join ', ')"
    }
    $pendingSummary = [ordered]@{
        Status = 'PASS'
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        Name = $Name
        Workload = $Workload
        DurationSeconds = $Duration
        GuardDurationSeconds = $guardDuration
        ExpectedCpuControlPercent = $ExpectedCpuControlPercent
        ExpectedCpuControlMode = $expectedCpuControlMode
        ExpectedSystemControlPercent = $ExpectedSystemControlPercent
        ExpectedSystemControlMode = $expectedSystemControlMode
        MinimumSystemRpm = $MinimumSystemRpm
        ExtendedThermalGuard = [bool]$ExtendedThermalGuard
        CpuMaximumC = $CpuMaximumC
        SystemMaximumC = $SystemMaximumC
        GpuMaximumC = $GpuMaximumC
        DimmMaximumC = $DimmMaximumC
        HarnessExitCode = $harnessExitCode
        GuardExitCode = $guardExitCode
        Problems = $problems.ToArray()
        AbortPath = $guardAbortPath
        HarnessDirectory = $harnessDirectory
        GuardLedgerPath = $guardLedgerPath
        GuardSummary = $guardSummary
    }
    Write-WrapperRecord -Kind 'wrapper-end' -Data @{
        Status = $pendingSummary.Status
        Problems = $pendingSummary.Problems
    }
}
catch {
    Add-WrapperFailure -Message $_.Exception.Message
    $harnessMayBeLive = $false
    if ($null -ne $harnessProcess) {
        try {
            $harnessMayBeLive = $null -eq $expectedHarnessIdentity -or
                (Test-IdentityAlive -Target $expectedHarnessIdentity)
        }
        catch {
            $harnessMayBeLive = $true
            Add-WrapperFailure -Message (
                "unable to determine harness state: $($_.Exception.Message)")
        }
    }
    if ($harnessMayBeLive) {
        try {
            New-DurableAbort -Reason 'wrapper-exception' -Details @{
                Error = $wrapperFailure
            }
        }
        catch {
            Add-WrapperFailure -Message (
                "failed to create the external abort: $($_.Exception.Message)")
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($abortExitGraceSeconds)
        try {
            while ((Test-IdentityAlive -Target $expectedHarnessIdentity) -and
                    [DateTimeOffset]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds $pollMilliseconds
            }
        }
        catch {
            Add-WrapperFailure -Message (
                "failed while waiting for harness abort: $($_.Exception.Message)")
        }
    }
    try {
        Wait-ForGuardAfterFailure
    }
    catch {
        Add-WrapperFailure -Message (
            "supervised failure cleanup did not complete: $($_.Exception.Message)")
    }
    Try-WriteWrapperRecord -Kind 'wrapper-failure' -Data @{
        Error = $wrapperFailure
    }
}
finally {
    if ($null -ne $harnessProcess) {
        try {
            Stop-LingeringHarness -Reason 'wrapper-finally'
        }
        catch {
            Add-WrapperFailure -Message (
                "final harness cleanup failed: $($_.Exception.Message)")
        }
    }
    $safeToStopGuard = $false
    try {
        $safeToStopGuard = Test-SafeToStopGuard
    }
    catch {
        Add-WrapperFailure -Message (
            "final workload-absence proof failed: $($_.Exception.Message)")
    }
    if ($null -ne $guardProcess) {
        try {
            $guardProcess.Refresh()
            if (-not $guardProcess.HasExited) {
                if ($safeToStopGuard -and $null -ne $expectedGuardIdentity) {
                    Stop-TrackedProcessExact -Process $guardProcess `
                        -Identity $expectedGuardIdentity -Label 'A2 guard'
                    Try-WriteWrapperRecord -Kind 'guard-stopped-in-finally' `
                        -Data @{ ProcessId = $guardProcess.Id }
                }
                else {
                    Add-WrapperFailure -Message (
                        'the guard was left running because final workload absence was not proven')
                }
            }
        }
        catch {
            Add-WrapperFailure -Message (
                "final guard cleanup failed: $($_.Exception.Message)")
        }
    }
}

if ($null -eq $pendingSummary -and -not $wrapperFailure) {
    Add-WrapperFailure -Message 'the wrapper completed without a pending PASS summary'
}
if ($wrapperFailure) {
    $failureSummary = [ordered]@{
        Status = 'FAIL'
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        Name = $Name
        Workload = $Workload
        ExpectedCpuControlPercent = $ExpectedCpuControlPercent
        ExpectedCpuControlMode = $expectedCpuControlMode
        ExpectedSystemControlPercent = $ExpectedSystemControlPercent
        ExpectedSystemControlMode = $expectedSystemControlMode
        MinimumSystemRpm = $MinimumSystemRpm
        ExtendedThermalGuard = [bool]$ExtendedThermalGuard
        Error = $wrapperFailure
        HarnessExitCode = $harnessExitCode
        GuardExitCode = $guardExitCode
        AbortPath = $guardAbortPath
    }
    try {
        Write-DurableReplaceJson -Path $wrapperSummaryPath `
            -Value $failureSummary
    }
    catch {
        $wrapperFailure = "$wrapperFailure; failed to write FAIL summary: $($_.Exception.Message)"
    }
    Write-Error $wrapperFailure
    exit 1
}

try {
    Write-DurableNewJson -Path $wrapperSummaryPath -Value $pendingSummary
}
catch {
    Add-WrapperFailure -Message (
        "failed to commit PASS summary: $($_.Exception.Message)")
    $failureSummary = [ordered]@{
        Status = 'FAIL'
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        Name = $Name
        Workload = $Workload
        Error = $wrapperFailure
        HarnessExitCode = $harnessExitCode
        GuardExitCode = $guardExitCode
        AbortPath = $guardAbortPath
    }
    try {
        Write-DurableReplaceJson -Path $wrapperSummaryPath `
            -Value $failureSummary
    }
    catch {
        $wrapperFailure = "$wrapperFailure; failed to replace summary with FAIL: $($_.Exception.Message)"
    }
    Write-Error $wrapperFailure
    exit 1
}

Write-Output "A2 stage complete: $resolvedOutputRoot"
