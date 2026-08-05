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
    [string] $OutputDirectory,

    [switch] $FanControlRequired,

    [string] $CpuBurnPath,

    [string] $ExternalAbortPath
)

<#
.SYNOPSIS
Runs one bounded CPU/iGPU reproduction stage without changing Fan Control or
the EC.  It is deliberately a workload/evidence collector, not a fan-control
driver.  Give every invocation a fresh, empty OutputDirectory.  When supplied,
ExternalAbortPath is a fail-closed signal file: it must not exist at preflight,
and creating it aborts the active, drain, or cooldown phase with its text saved
in the stage ledger.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
if ([string]::IsNullOrWhiteSpace($CpuBurnPath)) {
    $CpuBurnPath = Join-Path $PSScriptRoot `
        'CpuBurn\bin\Release\net10.0-windows\CpuBurn.exe'
}
$resolvedExternalAbortPath = if ([string]::IsNullOrWhiteSpace($ExternalAbortPath)) {
    $null
} else {
    [IO.Path]::GetFullPath($ExternalAbortPath)
}
$stageStartUtc = [DateTimeOffset]::UtcNow
$stageStopwatch = [Diagnostics.Stopwatch]::StartNew()
$children = [System.Collections.Generic.List[Diagnostics.Process]]::new()
$recordedChildExitIds = @{}
$heartbeatProcess = $null
$heartbeatStopPath = $null
$workloadWatchdogProcess = $null
$workloadWatchdogStopPath = $null
$workloadWatchdogPath = $null
$checkpointPath = $null
$checkpointStream = $null
$checkpointWriter = $null
$failure = $null
$abortReason = $null
$baselineLiveKernelReports = $null
$afterLiveKernelReports = $null
$expectedFanControlIdentity = $null
$bodyCompleted = $false
$failureCheckpointWritten = $false
$observedEventRecordIds = @{}
$cooldownSeconds = 20
$naturalExitGraceSeconds = 10
$workloadWatchdogGraceSeconds = 15
$loadEvidence = [ordered]@{
    ActiveSamples = 0
    CpuHighSamples = 0
    GpuHighSamples = 0
    CpuThresholdPercent = 80.0
    GpuThresholdPercent = 50.0
}

function Write-DurableText {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Text,
        [switch] $CreateNew
    )

    $mode = if ($CreateNew) {
        [IO.FileMode]::CreateNew
    } else {
        [IO.FileMode]::Append
    }
    $stream = [IO.FileStream]::new(
        $Path, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $writer = [IO.StreamWriter]::new($stream, $utf8NoBom, 4096, $true)
        try {
            $writer.Write($Text)
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

function Write-Checkpoint {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    $entry = [ordered]@{
        Sequence = $script:checkpointSequence
        Kind = $Kind
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        MonotonicMilliseconds = $stageStopwatch.Elapsed.TotalMilliseconds
        Data = $Data
    }
    $script:checkpointSequence++
    $json = $entry | ConvertTo-Json -Depth 10 -Compress
    $checkpointWriter.WriteLine($json)
    $checkpointWriter.Flush()
    $checkpointStream.Flush($true)
}

function Register-StageFailure {
    param(
        [Parameter(Mandatory = $true)][string] $Reason,
        [Parameter(Mandatory = $true)][string] $ErrorMessage
    )

    if (-not $script:failure) {
        $script:failure = $ErrorMessage
        $script:abortReason = $Reason
    }
    if (-not $script:failureCheckpointWritten -and
            $null -ne $script:checkpointWriter) {
        try {
            Write-Checkpoint -Kind 'stage-failure' -Data @{
                AbortReason = $script:abortReason
                Error = $script:failure
            }
            $script:failureCheckpointWritten = $true
        }
        catch {
            # Preserve the original failure. The final Write-Error still makes
            # the process fail even if the checkpoint device is unavailable.
        }
    }
}

function Invoke-DurableProbe {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Phase,
        [Parameter(Mandatory = $true)][scriptblock] $Action
    )

    $probe = [Diagnostics.Stopwatch]::StartNew()
    Write-Checkpoint -Kind 'probe-begin' -Data @{
        Name = $Name
        Phase = $Phase
    }
    try {
        $result = & $Action
        Write-Checkpoint -Kind 'probe-end' -Data @{
            Name = $Name
            Phase = $Phase
            Success = $true
            DurationMilliseconds = $probe.Elapsed.TotalMilliseconds
        }
        return $result
    }
    catch {
        $probeError = $_.Exception.Message
        if (-not $script:abortReason) {
            $script:abortReason = "probe-failed:$Name"
        }
        try {
            Write-Checkpoint -Kind 'probe-end' -Data @{
                Name = $Name
                Phase = $Phase
                Success = $false
                DurationMilliseconds = $probe.Elapsed.TotalMilliseconds
                Error = $probeError
            }
        }
        catch { }
        throw
    }
}

function Get-LiveKernelReportInventory {
    $root = 'C:\Windows\LiveKernelReports'
    try {
        $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
            ForEach-Object {
                [ordered]@{
                    Path = $_.FullName
                    Length = $_.Length
                    LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o')
                }
            })
        return [ordered]@{
            Root = $root
            Accessible = $true
            Files = $files
        }
    }
    catch {
        return [ordered]@{
            Root = $root
            Accessible = $false
            Error = $_.Exception.Message
            Files = @()
        }
    }
}

function Compare-LiveKernelReportInventory {
    param(
        [Parameter(Mandatory = $true)] $Before,
        [Parameter(Mandatory = $true)] $After
    )

    if (-not $Before.Accessible) {
        throw "The baseline live-kernel report inventory was inaccessible: $($Before.Error)"
    }
    if (-not $After.Accessible) {
        throw "The final live-kernel report inventory was inaccessible: $($After.Error)"
    }

    $beforeByPath = @{}
    foreach ($file in @($Before.Files)) {
        $beforeByPath[[string]$file.Path] = $file
    }
    $afterByPath = @{}
    foreach ($file in @($After.Files)) {
        $afterByPath[[string]$file.Path] = $file
    }

    $changes = [System.Collections.Generic.List[object]]::new()
    foreach ($path in @($beforeByPath.Keys)) {
        if (-not $afterByPath.ContainsKey($path)) {
            $changes.Add([ordered]@{ Change = 'removed'; Path = $path })
            continue
        }
        $beforeFile = $beforeByPath[$path]
        $afterFile = $afterByPath[$path]
        if ([long]$beforeFile.Length -ne [long]$afterFile.Length -or
                [string]$beforeFile.LastWriteTimeUtc -ne
                [string]$afterFile.LastWriteTimeUtc) {
            $changes.Add([ordered]@{
                Change = 'changed'
                Path = $path
                BeforeLength = [long]$beforeFile.Length
                AfterLength = [long]$afterFile.Length
                BeforeLastWriteTimeUtc = [string]$beforeFile.LastWriteTimeUtc
                AfterLastWriteTimeUtc = [string]$afterFile.LastWriteTimeUtc
            })
        }
    }
    foreach ($path in @($afterByPath.Keys)) {
        if (-not $beforeByPath.ContainsKey($path)) {
            $file = $afterByPath[$path]
            $changes.Add([ordered]@{
                Change = 'added'
                Path = $path
                Length = [long]$file.Length
                LastWriteTimeUtc = [string]$file.LastWriteTimeUtc
            })
        }
    }
    return $changes.ToArray()
}

function Get-EventRecords {
    param([datetime] $StartTime)

    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($logName in @('System', 'Application')) {
        try {
            $events = @(Get-WinEvent -FilterHashtable @{
                LogName = $logName
                StartTime = $StartTime
            } -ErrorAction Stop)
            foreach ($event in $events) {
                $provider = [string]$event.ProviderName
                $message = [string]$event.Message
                $isWhea = $provider -eq 'Microsoft-Windows-WHEA-Logger'
                $isWhea17 = $isWhea -and $event.Id -eq 17
                $isDisplay = $provider -match
                    '(^Display$|amdwddmg|amdkmdag|DxgKrnl|DisplayDriver)'
                $isVolmgr = $provider -match '(^volmgr$|volmgr)'
                $isWatchdog = $provider -match 'Windows Error Reporting|Watchdog' -and
                    $message -match 'LiveKernelEvent|141|117|WATCHDOG'
                if ($isWhea -or $isDisplay -or $isVolmgr -or $isWatchdog) {
                    $result.Add([ordered]@{
                        LogName = $logName
                        RecordId = [long]$event.RecordId
                        TimeCreatedUtc = $event.TimeCreated.ToUniversalTime().ToString('o')
                        Provider = $provider
                        Id = $event.Id
                        Level = $event.LevelDisplayName
                        WHEA = $isWhea
                        WHEA17 = $isWhea17
                        Display = $isDisplay
                        Volmgr = $isVolmgr
                        Watchdog = $isWatchdog
                        Message = $message
                    })
                }
            }
        }
        catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
                continue
            }
            throw "Unable to query the $logName event log: $($_.Exception.Message)"
        }
    }
    return $result.ToArray()
}

function Get-PerformanceSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('active', 'workload-drain', 'cooldown')]
        [string] $Phase
    )

    $snapshot = [ordered]@{}
    try {
        $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor `
            -Filter "Name='_Total'" -ErrorAction Stop
        $snapshot.CpuUtilityPercent = [double]$cpu.PercentProcessorTime
    }
    catch {
        throw "Unable to read CPU utilization: $($_.Exception.Message)"
    }

    $gpuCounterError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $counter = Get-Counter -Counter `
                '\GPU Engine(*engtype_3D)\Utilization Percentage', `
                '\GPU Adapter Memory(*)\Dedicated Usage', `
                '\GPU Adapter Memory(*)\Shared Usage' -ErrorAction Stop
            $threeD = @($counter.CounterSamples | Where-Object {
                $_.Path -match 'GPU Engine.*engtype_3D.*Utilization Percentage$'
            } | ForEach-Object { [double]$_.CookedValue })
            $dedicated = @($counter.CounterSamples | Where-Object {
                $_.Path -match 'GPU Adapter Memory.*Dedicated Usage$'
            } | ForEach-Object { [double]$_.CookedValue })
            $shared = @($counter.CounterSamples | Where-Object {
                $_.Path -match 'GPU Adapter Memory.*Shared Usage$'
            } | ForEach-Object { [double]$_.CookedValue })
            $snapshot.Gpu3DUtilizationSumPercent =
                if ($threeD.Count) { ($threeD | Measure-Object -Sum).Sum } else { $null }
            $snapshot.Gpu3DUtilizationMaxPercent =
                if ($threeD.Count) { ($threeD | Measure-Object -Maximum).Maximum } else { $null }
            $snapshot.GpuDedicatedMemoryBytes =
                if ($dedicated.Count) { ($dedicated | Measure-Object -Sum).Sum } else { $null }
            $snapshot.GpuSharedMemoryBytes =
                if ($shared.Count) { ($shared | Measure-Object -Sum).Sum } else { $null }
            $gpuCounterError = $null
            break
        }
        catch {
            $gpuCounterError = $_.Exception.Message
            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds 100
            }
        }
    }
    if ($gpuCounterError) {
        if ($Phase -eq 'active') {
            throw "Unable to read GPU performance counters after three attempts: $gpuCounterError"
        }
        $snapshot.Gpu3DUtilizationSumPercent = $null
        $snapshot.Gpu3DUtilizationMaxPercent = $null
        $snapshot.GpuDedicatedMemoryBytes = $null
        $snapshot.GpuSharedMemoryBytes = $null
        $snapshot.GpuCounterUnavailable = $gpuCounterError
    }
    if ($null -eq $snapshot.CpuUtilityPercent) {
        throw 'The CPU utilization counter returned no value.'
    }
    if ($Phase -eq 'active' -and
            ($Workload -eq 'igpu' -or $Workload -eq 'combined') -and
            $null -eq $snapshot.Gpu3DUtilizationMaxPercent) {
        throw 'The GPU 3D utilization counter returned no value for a GPU workload.'
    }
    return $snapshot
}

function Get-ChildSnapshot {
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($process in @($children)) {
        try {
            $process.Refresh()
            $hasExited = $process.HasExited
            $processName = try { $process.ProcessName } catch { 'unknown' }
            $exitCode = if ($hasExited) {
                try { $process.ExitCode } catch { $null }
            } else { $null }
            $result.Add([ordered]@{
                Name = $processName
                Id = $process.Id
                HasExited = $hasExited
                ExitCode = $exitCode
                Responding = if ($hasExited) { $null } else {
                    try { $process.Responding } catch { $null }
                }
                CpuSeconds = if ($hasExited) { $null } else {
                    try { $process.TotalProcessorTime.TotalSeconds } catch { $null }
                }
                WorkingSetBytes = if ($hasExited) { $null } else {
                    try { $process.WorkingSet64 } catch { $null }
                }
            })
        }
        catch {
            $result.Add([ordered]@{ Id = $process.Id; SnapshotError = $_.Exception.Message })
        }
    }
    return $result.ToArray()
}

function Get-ProcessIdentity {
    param([Parameter(Mandatory = $true)][Diagnostics.Process] $Process)

    $Process.Refresh()
    $path = $null
    try { $path = [string]$Process.Path } catch { }
    if ([string]::IsNullOrWhiteSpace($path)) {
        try { $path = [string]$Process.MainModule.FileName } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Unable to resolve executable path for process $($Process.Id)."
    }
    return [ordered]@{
        Id = $Process.Id
        StartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        StartTimeUtc = $Process.StartTime.ToUniversalTime().ToString('o')
        Path = [IO.Path]::GetFullPath($path)
    }
}

function Get-FanControlSnapshot {
    $processes = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    $snapshot = [ordered]@{ Count = $processes.Count }
    if ($processes.Count -eq 1) {
        $process = $processes[0]
        try {
            $identity = Get-ProcessIdentity -Process $process
            $snapshot.Id = $identity.Id
            $snapshot.StartTimeUtcTicks = $identity.StartTimeUtcTicks
            $snapshot.StartTimeUtc = $identity.StartTimeUtc
            $snapshot.Path = $identity.Path
            $snapshot.Responding = [bool]$process.Responding
            $snapshot.CpuSeconds = $process.TotalProcessorTime.TotalSeconds
            $snapshot.WorkingSetBytes = $process.WorkingSet64
        }
        catch {
            $snapshot.IdentityError = $_.Exception.Message
        }
    }
    return $snapshot
}

function Stop-NewlyStartedWorkload {
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

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Contains('"')) {
        throw 'Process arguments containing a quote are not supported.'
    }
    return '"' + $Value + '"'
}

function Start-WorkloadProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $stdout = Join-Path $resolvedOutput "$Label.stdout.log"
    $stderr = Join-Path $resolvedOutput "$Label.stderr.log"
    $process = $null
    $startTimeUtcTicks = $null
    $registered = $false
    try {
        $quoted = @($ArgumentList | ForEach-Object {
            Quote-ProcessArgument -Value ([string]$_)
        })
        $process = Start-Process -FilePath $FilePath -ArgumentList $quoted `
            -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $children.Add($process)
        $registered = $true
        $startTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        $identity = Get-ProcessIdentity -Process $process
        # Windows PowerShell can otherwise release the redirected process handle,
        # leaving ExitCode as $null even after WaitForExit(). Retain it explicitly.
        $process.EnableRaisingEvents = $true
        $null = $process.Handle
        Write-Checkpoint -Kind 'workload-start' -Data @{
            Label = $Label
            FilePath = $FilePath
            Arguments = $ArgumentList
            ProcessId = $process.Id
            Identity = $identity
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    catch {
        $launchError = $_.Exception.Message
        $cleanupError = $null
        if ($null -ne $process) {
            try {
                if ($null -ne $startTimeUtcTicks) {
                    Stop-NewlyStartedWorkload -Process $process `
                        -StartTimeUtcTicks $startTimeUtcTicks -Label $Label
                }
                else {
                    # The process is already registered in $children when
                    # possible. Kill through the original object immediately;
                    # the normal finally path remains a second cleanup layer.
                    $process.Refresh()
                    if (-not $process.HasExited) {
                        $process.Kill()
                        [void]$process.WaitForExit(5000)
                        if (-not $process.HasExited) {
                            throw "$Label did not exit after launch cleanup."
                        }
                    }
                }
            }
            catch {
                $cleanupError = $_.Exception.Message
            }
        }
        if (-not $registered -and $null -ne $process) {
            # The process was not reachable through the normal finally path.
            $process.Dispose()
        }
        if ($cleanupError) {
            throw "Workload initialization failed: $launchError Launch cleanup also failed: $cleanupError"
        }
        throw "Workload initialization failed: $launchError"
    }
}

function Stop-ChildProcesses {
    param([ValidateRange(0, 10000)][int] $GracefulWaitMilliseconds = 0)

    foreach ($process in @($children)) {
        if ($recordedChildExitIds.ContainsKey([string]$process.Id)) {
            continue
        }
        try {
            $processName = try { $process.ProcessName } catch { 'unknown' }
            if (-not $process.HasExited -and $GracefulWaitMilliseconds -gt 0) {
                [void]$process.WaitForExit($GracefulWaitMilliseconds)
            }
            if ($process.HasExited) {
                [void]$process.WaitForExit()
                $process.Refresh()
                $exitCode = try { $process.ExitCode } catch { $null }
                Write-Checkpoint -Kind 'workload-exit' -Data @{
                    ProcessId = $process.Id
                    ProcessName = $processName
                    ExitCode = $exitCode
                    Natural = $true
                }
                $recordedChildExitIds[[string]$process.Id] = $true
                continue
            }
            if (-not $process.HasExited) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
                if (-not $process.HasExited) {
                    throw "Workload PID $($process.Id) did not exit after Kill()."
                }
                Write-Checkpoint -Kind 'workload-stop' -Data @{
                    ProcessId = $process.Id
                    ProcessName = $processName
                    Natural = $false
                }
                $recordedChildExitIds[[string]$process.Id] = $true
            }
        }
        catch {
            $cleanupError = $_.Exception.Message
            Write-Checkpoint -Kind 'cleanup-error' -Data @{
                ProcessId = $process.Id
                Error = $cleanupError
            }
            if (-not $script:failure) {
                Register-StageFailure -Reason 'workload-cleanup-error' `
                    -ErrorMessage $cleanupError
            }
        }
    }
}

function Record-NaturalChildExits {
    foreach ($process in @($children)) {
        if ($recordedChildExitIds.ContainsKey([string]$process.Id)) {
            continue
        }
        $process.Refresh()
        if (-not $process.HasExited) {
            continue
        }
        [void]$process.WaitForExit()
        $process.Refresh()
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode) {
            $script:abortReason = 'workload-exit-code-unavailable'
            throw "Workload process $($process.Id) exited, but its exit code was unavailable."
        }
        $processName = try { $process.ProcessName } catch { 'unknown' }
        Write-Checkpoint -Kind 'workload-exit' -Data @{
            ProcessId = $process.Id
            ProcessName = $processName
            ExitCode = [int]$exitCode
            Natural = $true
        }
        $recordedChildExitIds[[string]$process.Id] = $true
        if ([int]$exitCode -ne 0) {
            $script:abortReason = 'workload-exit-code-nonzero'
            throw "Workload process $($process.Id) exited with code $exitCode."
        }
    }
}

function Get-CurrentPowerShellHostPath {
    $hostProcess = Get-Process -Id $PID -ErrorAction Stop
    $hostPath = $null
    try { $hostPath = [string]$hostProcess.Path } catch { }
    if ([string]::IsNullOrWhiteSpace($hostPath)) {
        $hostPath = [string]$hostProcess.MainModule.FileName
    }
    if ([string]::IsNullOrWhiteSpace($hostPath)) {
        throw 'Unable to resolve the current PowerShell host executable.'
    }
    return $hostPath
}

function Start-IndependentHeartbeat {
    param([Parameter(Mandatory = $true)][string] $Path)

    $parentProcess = Get-Process -Id $PID -ErrorAction Stop
    $parentStartTimeUtcTicks = $parentProcess.StartTime.ToUniversalTime().Ticks
    $scriptText = @"
`$p = '$($Path.Replace("'", "''"))'
`$stop = '$($heartbeatStopPath.Replace("'", "''"))'
`$encoding = [Text.UTF8Encoding]::new(`$false)
`$clock = [Diagnostics.Stopwatch]::StartNew()
function Write-HeartbeatRecord(`$record) {
    `$line = (`$record | ConvertTo-Json -Compress) + [Environment]::NewLine
    `$stream = [IO.FileStream]::new(`$p, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { `$writer = [IO.StreamWriter]::new(`$stream, `$encoding, 4096, `$true); try { `$writer.Write(`$line); `$writer.Flush(); `$stream.Flush(`$true) } finally { `$writer.Dispose() } } finally { `$stream.Dispose() }
}
while (-not [IO.File]::Exists(`$stop)) {
    `$parentAlive = `$false
    try {
        `$parent = Get-Process -Id $PID -ErrorAction Stop
        `$parentAlive = `$parent.StartTime.ToUniversalTime().Ticks -eq $parentStartTimeUtcTicks
    } catch { }
    if (-not `$parentAlive) {
        Write-HeartbeatRecord ([ordered]@{ Kind = 'parent-gone'; Utc = [DateTimeOffset]::UtcNow.ToString('o'); MonotonicMilliseconds = `$clock.Elapsed.TotalMilliseconds; ParentProcessId = $PID })
        break
    }
    Write-HeartbeatRecord ([ordered]@{ Kind = 'heartbeat'; Utc = [DateTimeOffset]::UtcNow.ToString('o'); MonotonicMilliseconds = `$clock.Elapsed.TotalMilliseconds; ParentProcessId = $PID })
    Start-Sleep -Milliseconds 1000
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($scriptText))
    return Start-Process -FilePath (Get-CurrentPowerShellHostPath) `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) `
        -PassThru -WindowStyle Hidden
}

function Start-IndependentWorkloadWatchdog {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][DateTimeOffset] $DeadlineUtc
    )

    $targets = @($children | ForEach-Object {
        $identity = Get-ProcessIdentity -Process $_
        [ordered]@{
            Id = $identity.Id
            StartTimeUtcTicks = $identity.StartTimeUtcTicks
            Path = $identity.Path
        }
    })
    $targetsJson = $targets | ConvertTo-Json -Depth 5 -Compress
    $targetsBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($targetsJson))
    $scriptText = @"
`$p = '$($Path.Replace("'", "''"))'
`$stop = '$($workloadWatchdogStopPath.Replace("'", "''"))'
`$deadline = [DateTimeOffset]::Parse('$($DeadlineUtc.ToString('o'))')
`$encoding = [Text.UTF8Encoding]::new(`$false)
`$targetsJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$targetsBase64'))
`$decodedTargets = `$targetsJson | ConvertFrom-Json
`$targets = @()
foreach (`$decodedTarget in `$decodedTargets) {
    `$targets += `$decodedTarget
}
function Write-WatchdogRecord(`$record) {
    `$line = (`$record | ConvertTo-Json -Depth 5 -Compress) + [Environment]::NewLine
    `$stream = [IO.FileStream]::new(`$p, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { `$writer = [IO.StreamWriter]::new(`$stream, `$encoding, 4096, `$true); try { `$writer.Write(`$line); `$writer.Flush(); `$stream.Flush(`$true) } finally { `$writer.Dispose() } } finally { `$stream.Dispose() }
}
Write-WatchdogRecord ([ordered]@{ Kind = 'watchdog-start'; Utc = [DateTimeOffset]::UtcNow.ToString('o'); DeadlineUtc = `$deadline.ToString('o'); Targets = `$targets })
`$reportedInspectionErrors = @{}
while (`$true) {
    if ([IO.File]::Exists(`$stop)) {
        Write-WatchdogRecord ([ordered]@{ Kind = 'watchdog-stopped'; Utc = [DateTimeOffset]::UtcNow.ToString('o') })
        exit 0
    }
    `$live = @()
    `$newInspectionErrors = @()
    foreach (`$target in `$targets) {
        try {
            `$process = Get-Process -Id ([int]`$target.Id) -ErrorAction Stop
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            continue
        }
        catch {
            `$live += `$target
            `$message = "`$(`$target.Id): `$(`$_.Exception.Message)"
            if (-not `$reportedInspectionErrors.ContainsKey(`$message)) {
                `$reportedInspectionErrors[`$message] = `$true
                `$newInspectionErrors += `$message
            }
            continue
        }
        try {
            if (`$process.StartTime.ToUniversalTime().Ticks -eq
                    [long]`$target.StartTimeUtcTicks) {
                `$live += `$target
            }
        }
        catch {
            `$live += `$target
            `$message = "`$(`$target.Id): `$(`$_.Exception.Message)"
            if (-not `$reportedInspectionErrors.ContainsKey(`$message)) {
                `$reportedInspectionErrors[`$message] = `$true
                `$newInspectionErrors += `$message
            }
        }
    }
    if (`$newInspectionErrors.Count -ne 0) {
        Write-WatchdogRecord ([ordered]@{ Kind = 'watchdog-inspection-error'; Utc = [DateTimeOffset]::UtcNow.ToString('o'); Errors = `$newInspectionErrors })
    }
    if (`$live.Count -eq 0) {
        Write-WatchdogRecord ([ordered]@{ Kind = 'workloads-exited'; Utc = [DateTimeOffset]::UtcNow.ToString('o') })
        exit 0
    }
    if ([DateTimeOffset]::UtcNow -ge `$deadline) {
        `$errors = @()
        foreach (`$target in `$live) {
            try {
                `$process = Get-Process -Id ([int]`$target.Id) -ErrorAction Stop
                `$process.EnableRaisingEvents = `$true
                `$null = `$process.Handle
                `$identityPath = `$null
                try { `$identityPath = [string]`$process.Path } catch { }
                if ([string]::IsNullOrWhiteSpace(`$identityPath)) {
                    try { `$identityPath = [string]`$process.MainModule.FileName } catch { }
                }
                if (`$process.StartTime.ToUniversalTime().Ticks -eq [long]`$target.StartTimeUtcTicks -and
                        [StringComparer]::OrdinalIgnoreCase.Equals(
                            [IO.Path]::GetFullPath(`$identityPath),
                            [IO.Path]::GetFullPath([string]`$target.Path))) {
                    `$process.Kill()
                    [void]`$process.WaitForExit(5000)
                    if (-not `$process.HasExited) {
                        throw "PID `$(`$process.Id) did not exit after Kill()."
                    }
                }
            }
            catch { `$errors += "`$(`$target.Id): `$(`$_.Exception.Message)" }
        }
        Write-WatchdogRecord ([ordered]@{ Kind = 'deadline-enforced'; Utc = [DateTimeOffset]::UtcNow.ToString('o'); Targets = `$live; Errors = `$errors })
        if (`$errors.Count -eq 0) { exit 2 } else { exit 3 }
    }
    Start-Sleep -Milliseconds 250
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($scriptText))
    return Start-Process -FilePath (Get-CurrentPowerShellHostPath) `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) `
        -PassThru -WindowStyle Hidden
}

function Assert-Preflight {
    if ($resolvedExternalAbortPath -and
            (Test-Path -LiteralPath $resolvedExternalAbortPath)) {
        throw "ExternalAbortPath must not exist before the stage: $resolvedExternalAbortPath"
    }

    if ($Workload -eq 'igpu' -or $Workload -eq 'combined') {
        $principal = [Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'WinSAT DWM stages require an elevated PowerShell session.'
        }
    }

    $conflicts = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('CpuBurn', 'WinSAT')
    })
    if ($conflicts.Count -ne 0) {
        throw "Refusing to start while workload process is already running: $($conflicts.ProcessName -join ', ')"
    }

    $fanControl = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    if ($FanControlRequired -and $fanControl.Count -ne 1) {
        throw "This stage requires exactly one FanControl.exe process; found $($fanControl.Count)."
    }
    if (-not $FanControlRequired -and $fanControl.Count -ne 0) {
        throw 'This baseline stage requires FanControl.exe to be stopped.'
    }
    if ($FanControlRequired -and -not $fanControl[0].Responding) {
        throw 'FanControl.exe is not responding; refusing to add a workload.'
    }
    if ($FanControlRequired) {
        $script:expectedFanControlIdentity =
            Get-ProcessIdentity -Process $fanControl[0]
    }
}

function Get-NewRelevantEventRecords {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Events
    )

    $newEvents = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        $eventKey = "$($event.LogName)|$($event.RecordId)"
        if (-not $observedEventRecordIds.ContainsKey($eventKey)) {
            $observedEventRecordIds[$eventKey] = $true
            $newEvents.Add($event)
        }
    }
    return $newEvents.ToArray()
}

function Get-ExternalAbortContent {
    if (-not $resolvedExternalAbortPath -or
            -not (Test-Path -LiteralPath $resolvedExternalAbortPath)) {
        return $null
    }
    try {
        $content = [string](Get-Content -LiteralPath $resolvedExternalAbortPath `
            -Raw -ErrorAction Stop)
        $content = $content.Trim()
        if ([string]::IsNullOrWhiteSpace($content)) {
            $content = 'requested'
        }
    }
    catch {
        $content = "unreadable abort file: $($_.Exception.Message)"
    }
    if ($content.Length -gt 512) {
        $content = $content.Substring(0, 512)
    }
    return $content
}

function Invoke-MonitorSample {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('active', 'workload-drain', 'cooldown')]
        [string] $Phase
    )

    $externalAbort = Get-ExternalAbortContent
    if ($null -ne $externalAbort) {
        $script:abortReason = "external-abort:$externalAbort"
        Write-Checkpoint -Kind 'external-abort' -Data @{
            Path = $resolvedExternalAbortPath
            Content = $externalAbort
            Phase = $Phase
        }
        throw "Stage aborted by ExternalAbortPath: $externalAbort"
    }

    $probeData = Invoke-DurableProbe -Name 'monitor-sample' -Phase $Phase -Action {
        $allEvents = @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)
        $newEvents = @(Get-NewRelevantEventRecords -Events $allEvents)
        $childrenSnapshot = @(Get-ChildSnapshot)
        foreach ($child in $childrenSnapshot) {
            if ($child.Contains('SnapshotError')) {
                throw "Unable to inspect workload process $($child.Id): $($child.SnapshotError)"
            }
        }
        $fanControlSnapshot = Get-FanControlSnapshot
        if ($fanControlSnapshot.Contains('IdentityError')) {
            throw "Unable to inspect Fan Control: $($fanControlSnapshot.IdentityError)"
        }
        $performance = Get-PerformanceSnapshot -Phase $Phase
        return [ordered]@{
            Events = $newEvents
            Children = $childrenSnapshot
            FanControl = $fanControlSnapshot
            Performance = $performance
        }
    }

    $reasons = [System.Collections.Generic.List[string]]::new()
    foreach ($event in @($probeData.Events)) {
        if ($event.WHEA) { $reasons.Add("new-whea-$($event.Id)") }
        if ($event.Display) { $reasons.Add('new-display-event') }
        if ($event.Watchdog) { $reasons.Add('new-watchdog-event') }
        if ($event.Volmgr) { $reasons.Add('new-volmgr-event') }
    }

    $exitedChildren = @($probeData.Children | Where-Object {
        $true -eq $_.HasExited
    })
    $runningChildren = @($probeData.Children | Where-Object {
        $false -eq $_.HasExited
    })
    if ($Phase -eq 'active' -and $exitedChildren.Count -ne 0) {
        $reasons.Add('workload-exited-before-stage-end')
    }
    if ($Phase -eq 'cooldown' -and $runningChildren.Count -ne 0) {
        $reasons.Add('workload-running-during-cooldown')
    }

    $fanControlSnapshot = $probeData.FanControl
    if ($FanControlRequired -and $fanControlSnapshot.Count -ne 1) {
        $reasons.Add('fancontrol-process-count-changed')
    }
    elseif ($FanControlRequired) {
        if ($fanControlSnapshot.Id -ne $expectedFanControlIdentity.Id -or
                [long]$fanControlSnapshot.StartTimeUtcTicks -ne
                [long]$expectedFanControlIdentity.StartTimeUtcTicks -or
                -not [StringComparer]::OrdinalIgnoreCase.Equals(
                    [string]$fanControlSnapshot.Path,
                    [string]$expectedFanControlIdentity.Path)) {
            $reasons.Add('fancontrol-process-identity-changed')
        }
        elseif ($fanControlSnapshot.Responding -ne $true) {
            $reasons.Add('fancontrol-not-responding')
        }
    }
    elseif ($fanControlSnapshot.Count -ne 0) {
        $reasons.Add('fancontrol-started-during-baseline')
    }

    if ($Phase -eq 'active') {
        $loadEvidence.ActiveSamples++
        if ([double]$probeData.Performance.CpuUtilityPercent -ge
                [double]$loadEvidence.CpuThresholdPercent) {
            $loadEvidence.CpuHighSamples++
        }
        if ($null -ne $probeData.Performance.Gpu3DUtilizationMaxPercent -and
                [double]$probeData.Performance.Gpu3DUtilizationMaxPercent -ge
                [double]$loadEvidence.GpuThresholdPercent) {
            $loadEvidence.GpuHighSamples++
        }
    }

    $externalAbort = Get-ExternalAbortContent
    if ($null -ne $externalAbort) {
        $reasons.Add("external-abort:$externalAbort")
    }
    $sampleAbortReason = @($reasons | Select-Object -Unique) -join ','
    Write-Checkpoint -Kind 'sample' -Data @{
        Phase = $Phase
        Performance = $probeData.Performance
        Children = $probeData.Children
        FanControl = $fanControlSnapshot
        NewRelevantEvents = $probeData.Events
        ExternalAbort = $externalAbort
        AbortReason = if ($sampleAbortReason) { $sampleAbortReason } else { $null }
    }
    if ($sampleAbortReason) {
        $script:abortReason = $sampleAbortReason
        throw "Stage aborted: $sampleAbortReason"
    }
}

function Invoke-OneSecondMonitoredSample {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('active', 'workload-drain', 'cooldown')]
        [string] $Phase
    )

    $sampleStartMilliseconds = $stageStopwatch.Elapsed.TotalMilliseconds
    Invoke-MonitorSample -Phase $Phase
    $sampleWorkMilliseconds =
        $stageStopwatch.Elapsed.TotalMilliseconds - $sampleStartMilliseconds
    $remainingMilliseconds = [int][Math]::Floor(1000.0 - $sampleWorkMilliseconds)
    if ($remainingMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $remainingMilliseconds
    }
}

function Wait-ForNaturalWorkloadExit {
    if ($children.Count -eq 0) {
        return
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($naturalExitGraceSeconds)
    while ($true) {
        Record-NaturalChildExits
        if ($recordedChildExitIds.Count -eq $children.Count) {
            return
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $runningIds = @($children | Where-Object { -not $_.HasExited } |
                ForEach-Object { $_.Id })
            $script:abortReason = 'workload-natural-exit-timeout'
            throw "Workload processes did not exit naturally within $naturalExitGraceSeconds seconds: $($runningIds -join ', ')"
        }
        Invoke-OneSecondMonitoredSample -Phase 'workload-drain'
    }
}

function Assert-WorkloadEvidence {
    if ($Workload -eq 'idle') {
        Write-Checkpoint -Kind 'workload-evidence' -Data @{
            Status = 'not-applicable'
            Evidence = $loadEvidence
        }
        return
    }

    $minimumSamples = [Math]::Min(5,
        [Math]::Max(2, [int][Math]::Floor($Duration / 3.0)))
    $requiredHighSamples = [Math]::Max(2,
        [int][Math]::Ceiling($loadEvidence.ActiveSamples * 0.5))
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($loadEvidence.ActiveSamples -lt $minimumSamples) {
        $problems.Add("only-$($loadEvidence.ActiveSamples)-valid-active-samples")
    }
    if (($Workload -eq 'cpu' -or $Workload -eq 'combined') -and
            $loadEvidence.CpuHighSamples -lt $requiredHighSamples) {
        $problems.Add("cpu-load-below-$($loadEvidence.CpuThresholdPercent)-percent")
    }
    if (($Workload -eq 'igpu' -or $Workload -eq 'combined') -and
            $loadEvidence.GpuHighSamples -lt $requiredHighSamples) {
        $problems.Add("gpu-load-below-$($loadEvidence.GpuThresholdPercent)-percent")
    }
    Write-Checkpoint -Kind 'workload-evidence' -Data @{
        Status = if ($problems.Count) { 'failed' } else { 'passed' }
        MinimumSamples = $minimumSamples
        RequiredHighSamples = $requiredHighSamples
        Problems = $problems.ToArray()
        Evidence = $loadEvidence
    }
    if ($problems.Count) {
        $script:abortReason = 'invalid-workload-evidence'
        throw "Invalid workload evidence: $($problems -join ', ')"
    }
}

function Invoke-MonitoredCooldown {
    Write-Checkpoint -Kind 'cooldown-start' -Data @{
        DurationSeconds = $cooldownSeconds
    }
    $cooldown = [Diagnostics.Stopwatch]::StartNew()
    while ($cooldown.Elapsed.TotalSeconds -lt $cooldownSeconds) {
        Invoke-OneSecondMonitoredSample -Phase 'cooldown'
    }
    Write-Checkpoint -Kind 'cooldown-complete' -Data @{
        DurationSeconds = $cooldownSeconds
    }
}

function Get-WorkloadWatchdogRecords {
    if (-not $workloadWatchdogPath -or
            -not (Test-Path -LiteralPath $workloadWatchdogPath)) {
        return @()
    }
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($line in @(Get-Content -LiteralPath $workloadWatchdogPath `
            -ErrorAction Stop)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $records.Add(($line | ConvertFrom-Json -ErrorAction Stop)) }
        catch { throw "Invalid workload-watchdog record: $($_.Exception.Message)" }
    }
    return $records.ToArray()
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    if ((Get-ChildItem -LiteralPath $resolvedOutput -Force | Measure-Object).Count -ne 0) {
        throw "OutputDirectory must be new or empty: $resolvedOutput"
    }
}
else {
    [IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
}

$checkpointPath = Join-Path $resolvedOutput 'checkpoint.jsonl'
$heartbeatPath = Join-Path $resolvedOutput 'heartbeat.jsonl'
$heartbeatStopPath = Join-Path $resolvedOutput 'heartbeat.stop'
$workloadWatchdogPath = Join-Path $resolvedOutput 'workload-watchdog.jsonl'
$workloadWatchdogStopPath = Join-Path $resolvedOutput 'workload-watchdog.stop'
if ((Test-Path -LiteralPath $checkpointPath) -or
    (Test-Path -LiteralPath $heartbeatPath) -or
    (Test-Path -LiteralPath $heartbeatStopPath) -or
    (Test-Path -LiteralPath $workloadWatchdogPath) -or
    (Test-Path -LiteralPath $workloadWatchdogStopPath)) {
    throw "OutputDirectory already contains a stage ledger: $resolvedOutput"
}
if (($Workload -eq 'cpu' -or $Workload -eq 'combined') -and
    -not (Test-Path -LiteralPath $CpuBurnPath -PathType Leaf)) {
    throw "CpuBurn executable is missing: $CpuBurnPath"
}

$checkpointStream = [IO.FileStream]::new(
    $checkpointPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
    [IO.FileShare]::Read)
$checkpointWriter = [IO.StreamWriter]::new($checkpointStream, $utf8NoBom, 4096, $true)
$script:checkpointSequence = 0

try {
    Assert-Preflight
    $baselineLiveKernelReports = Invoke-DurableProbe `
        -Name 'livekernel-inventory' -Phase 'preflight' -Action {
            Get-LiveKernelReportInventory
        }
    if (-not $baselineLiveKernelReports.Accessible) {
        $abortReason = 'livekernel-report-inaccessible'
        throw "Live-kernel reports are inaccessible: $($baselineLiveKernelReports.Error)"
    }
    Write-DurableText -Path (Join-Path $resolvedOutput 'livekernel-before.json') `
        -Text (($baselineLiveKernelReports | ConvertTo-Json -Depth 8) + [Environment]::NewLine) `
        -CreateNew
    $baselineEvents = @(Invoke-DurableProbe -Name 'event-query' `
        -Phase 'preflight' -Action {
            @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)
        })
    foreach ($event in $baselineEvents) {
        $observedEventRecordIds["$($event.LogName)|$($event.RecordId)"] = $true
    }
    if ($baselineEvents.Count -ne 0) {
        $abortReason = 'relevant-event-during-preflight'
        throw 'A relevant hardware event appeared during stage preflight.'
    }
    $stageStopwatch.Restart()
    Write-Checkpoint -Kind 'stage-start' -Data @{
        Name = $Name
        Workload = $Workload
        DurationSeconds = $Duration
        CooldownSeconds = $cooldownSeconds
        FanControlRequired = [bool]$FanControlRequired
        ExpectedFanControlIdentity = $expectedFanControlIdentity
        ExternalAbortPath = $resolvedExternalAbortPath
        ProcessId = $PID
        StartUtc = $stageStartUtc.ToString('o')
        BaselineLiveKernelReports = $baselineLiveKernelReports
        BaselineRelevantEvents = $baselineEvents
    }

    $heartbeatProcess = Start-IndependentHeartbeat -Path $heartbeatPath
    $heartbeatProcess.EnableRaisingEvents = $true
    $null = $heartbeatProcess.Handle
    Write-Checkpoint -Kind 'heartbeat-start' -Data @{ ProcessId = $heartbeatProcess.Id }

    if ($Workload -eq 'cpu' -or $Workload -eq 'combined') {
        Start-WorkloadProcess -FilePath ([IO.Path]::GetFullPath($CpuBurnPath)) `
            -ArgumentList @([string]$Duration, [string][Environment]::ProcessorCount) `
            -Label 'cpu-burn'
    }
    if ($Workload -eq 'igpu' -or $Workload -eq 'combined') {
        $winsat = (Get-Command 'WinSAT.exe' -ErrorAction Stop).Source
        Start-WorkloadProcess -FilePath $winsat -ArgumentList @(
            'dwm', '-normalw', '20', '-glassw', '8', '-time', [string]$Duration,
            '-v', '-fullscreen', '-xml', (Join-Path $resolvedOutput 'winsat.xml')) `
            -Label 'winsat-dwm'
    }
    if ($children.Count -ne 0) {
        $workloadWatchdogDeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(
            $Duration + $workloadWatchdogGraceSeconds)
        $workloadWatchdogProcess = Start-IndependentWorkloadWatchdog `
            -Path $workloadWatchdogPath -DeadlineUtc $workloadWatchdogDeadlineUtc
        $workloadWatchdogProcess.EnableRaisingEvents = $true
        $null = $workloadWatchdogProcess.Handle
        Write-Checkpoint -Kind 'workload-watchdog-start' -Data @{
            ProcessId = $workloadWatchdogProcess.Id
            DeadlineUtc = $workloadWatchdogDeadlineUtc.ToString('o')
            GraceSeconds = $workloadWatchdogGraceSeconds
        }
    }

    while ($stageStopwatch.Elapsed.TotalSeconds -lt $Duration) {
        Invoke-OneSecondMonitoredSample -Phase 'active'
    }
    Wait-ForNaturalWorkloadExit
    Assert-WorkloadEvidence
    Invoke-MonitoredCooldown
    $bodyCompleted = $true
}
catch {
    $failureReason = if ($abortReason) { $abortReason } else { 'script-error' }
    Register-StageFailure -Reason $failureReason `
        -ErrorMessage $_.Exception.Message
}
finally {
    $childrenProvenExited = $children.Count -eq 0
    $liveTrackedChildren = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $checkpointWriter) {
        Stop-ChildProcesses -GracefulWaitMilliseconds 0
        foreach ($child in @($children)) {
            try {
                $child.Refresh()
                if (-not $child.HasExited) {
                    $liveTrackedChildren.Add((Get-ProcessIdentity -Process $child))
                }
            }
            catch {
                $liveTrackedChildren.Add([ordered]@{
                    Id = $child.Id
                    InspectionError = $_.Exception.Message
                })
            }
        }
        $childrenProvenExited = $liveTrackedChildren.Count -eq 0
        if (-not $childrenProvenExited -and -not $failure) {
            Register-StageFailure -Reason 'workload-cleanup-incomplete' `
                -ErrorMessage 'One or more tracked workloads remained or could not be inspected after local cleanup.'
        }
    }
    if ($workloadWatchdogProcess) {
        if ($childrenProvenExited) {
            if ($workloadWatchdogStopPath -and
                    -not (Test-Path -LiteralPath $workloadWatchdogStopPath)) {
                try {
                    Write-DurableText -Path $workloadWatchdogStopPath `
                        -Text 'stop' -CreateNew
                } catch { }
            }
            try {
                $workloadWatchdogProcess.WaitForExit(3000) | Out-Null
            } catch { }
            try {
                if (-not $workloadWatchdogProcess.HasExited) {
                    $workloadWatchdogProcess.Kill()
                    [void]$workloadWatchdogProcess.WaitForExit(5000)
                    if (-not $workloadWatchdogProcess.HasExited) {
                        throw 'The independent workload watchdog did not exit after Kill().'
                    }
                    if (-not $failure) {
                        Register-StageFailure -Reason 'workload-watchdog-hung' `
                            -ErrorMessage 'The independent workload watchdog did not exit.'
                    }
                }
            } catch { }
        }
        else {
            Write-Checkpoint -Kind 'workload-watchdog-left-armed' -Data @{
                ProcessId = $workloadWatchdogProcess.Id
                LiveOrUnknownChildren = $liveTrackedChildren.ToArray()
                DeadlineUtc = $workloadWatchdogDeadlineUtc.ToString('o')
            }
        }
    }

    $watchdogRecords = @()
    try {
        $watchdogRecords = @(Get-WorkloadWatchdogRecords)
        $deadlineRecords = @($watchdogRecords | Where-Object {
            $_.Kind -eq 'deadline-enforced'
        })
        $inspectionErrors = @($watchdogRecords | Where-Object {
            $_.Kind -eq 'watchdog-inspection-error'
        })
        $watchdogStarts = @($watchdogRecords | Where-Object {
            $_.Kind -eq 'watchdog-start'
        })
        $watchdogTerminals = @($watchdogRecords | Where-Object {
            $_.Kind -in @('workloads-exited', 'watchdog-stopped',
                'deadline-enforced')
        })
        if ($children.Count -ne 0 -and
                ($watchdogStarts.Count -ne 1 -or
                 $watchdogTerminals.Count -eq 0) -and -not $failure) {
            Register-StageFailure -Reason 'workload-watchdog-evidence-missing' `
                -ErrorMessage 'The independent workload watchdog did not produce a complete record.'
        }
        if ($deadlineRecords.Count -ne 0 -and -not $failure) {
            Register-StageFailure -Reason 'workload-watchdog-deadline' `
                -ErrorMessage 'The independent watchdog enforced the workload deadline.'
        }
        if ($inspectionErrors.Count -ne 0 -and -not $failure) {
            Register-StageFailure -Reason 'workload-watchdog-inspection-error' `
                -ErrorMessage 'The independent watchdog could not inspect a tracked workload.'
        }
    }
    catch {
        if (-not $failure) {
            Register-StageFailure -Reason 'workload-watchdog-evidence-error' `
                -ErrorMessage $_.Exception.Message
        }
    }

    if ($null -ne $checkpointWriter) {
        $dumpChanges = @()
        $finalEvents = @()
        $lateRelevantEvents = @()
        try {
            $afterLiveKernelReports = Invoke-DurableProbe `
                -Name 'livekernel-inventory' -Phase 'final' -Action {
                    Get-LiveKernelReportInventory
                }
            Write-DurableText -Path (Join-Path $resolvedOutput 'livekernel-after.json') `
                -Text (($afterLiveKernelReports | ConvertTo-Json -Depth 8) + [Environment]::NewLine) `
                -CreateNew
            if ($null -ne $baselineLiveKernelReports) {
                $dumpChanges = @(Compare-LiveKernelReportInventory `
                    -Before $baselineLiveKernelReports -After $afterLiveKernelReports)
                if ($dumpChanges.Count -ne 0 -and -not $failure) {
                    Register-StageFailure -Reason 'livekernel-report-changed' `
                        -ErrorMessage 'The live-kernel report inventory changed during the stage.'
                }
            }
            elseif (-not $afterLiveKernelReports.Accessible -and -not $failure) {
                Register-StageFailure -Reason 'livekernel-report-inaccessible' `
                    -ErrorMessage "Live-kernel reports are inaccessible: $($afterLiveKernelReports.Error)"
            }
        }
        catch {
            if (-not $failure) {
                Register-StageFailure -Reason 'livekernel-evidence-error' `
                    -ErrorMessage $_.Exception.Message
            }
        }

        try {
            $finalEvents = @(Invoke-DurableProbe -Name 'event-query' `
                -Phase 'final' -Action {
                    @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)
                })
            $lateRelevantEvents = @(Get-NewRelevantEventRecords -Events $finalEvents)
            $lateFailureEvents = @($lateRelevantEvents | Where-Object {
                $_.WHEA -or $_.Display -or $_.Watchdog -or $_.Volmgr
            })
            if ($lateFailureEvents.Count -ne 0 -and -not $failure) {
                Register-StageFailure -Reason 'late-relevant-event' `
                    -ErrorMessage 'A relevant hardware event appeared after the final cooldown sample.'
            }
        }
        catch {
            if (-not $failure) {
                Register-StageFailure -Reason 'event-evidence-error' `
                    -ErrorMessage $_.Exception.Message
            }
        }

        $finalExternalAbort = Get-ExternalAbortContent
        if ($null -ne $finalExternalAbort -and -not $failure) {
            Register-StageFailure -Reason "external-abort:$finalExternalAbort" `
                -ErrorMessage "Stage aborted by ExternalAbortPath: $finalExternalAbort"
        }

        try {
            if ($bodyCompleted -and -not $failure) {
                Write-Checkpoint -Kind 'stage-complete' -Data @{
                    Status = 'completed'
                }
            }
            Write-Checkpoint -Kind 'stage-end' -Data @{
                Status = if ($failure) { 'failed' } else { 'completed' }
                AbortReason = $abortReason
                Error = $failure
                AfterLiveKernelReports = $afterLiveKernelReports
                LiveKernelReportChanges = $dumpChanges
                FinalRelevantEvents = $finalEvents
                LateRelevantEvents = $lateRelevantEvents
                WorkloadWatchdogRecords = $watchdogRecords
            }
        }
        finally {
            if ($heartbeatStopPath -and
                    -not (Test-Path -LiteralPath $heartbeatStopPath)) {
                try {
                    Write-DurableText -Path $heartbeatStopPath `
                        -Text 'stop' -CreateNew
                } catch { }
            }
            if ($heartbeatProcess) {
                try { $heartbeatProcess.WaitForExit(3000) | Out-Null } catch { }
                try {
                    if (-not $heartbeatProcess.HasExited) {
                        $heartbeatProcess.Kill()
                        [void]$heartbeatProcess.WaitForExit(5000)
                    }
                } catch { }
            }
            $checkpointWriter.Dispose()
            $checkpointStream.Dispose()
            $checkpointWriter = $null
            $checkpointStream = $null
        }
    }
}

if ($failure) {
    Write-Error $failure
    exit 1
}

Write-Output "Stage complete: $resolvedOutput"
