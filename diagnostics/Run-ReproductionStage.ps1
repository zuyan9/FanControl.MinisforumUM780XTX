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

    [bool] $FanControlRequired = $false,

    [string] $CpuBurnPath
)

<#
.SYNOPSIS
Runs one bounded CPU/iGPU reproduction stage without changing Fan Control or
the EC.  It is deliberately a workload/evidence collector, not a fan-control
driver.  Give every invocation a fresh, empty OutputDirectory.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
if ([string]::IsNullOrWhiteSpace($CpuBurnPath)) {
    $CpuBurnPath = Join-Path $PSScriptRoot `
        'CpuBurn\bin\Release\net10.0-windows\CpuBurn.exe'
}
$stageStartUtc = [DateTimeOffset]::UtcNow
$stageStopwatch = [Diagnostics.Stopwatch]::StartNew()
$children = [System.Collections.Generic.List[Diagnostics.Process]]::new()
$heartbeatProcess = $null
$heartbeatStopPath = $null
$checkpointPath = $null
$checkpointStream = $null
$checkpointWriter = $null
$failure = $null
$abortReason = $null
$baselineLiveKernelReports = $null
$observedEventRecordIds = @{}

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
                $isWhea17 = $provider -eq 'Microsoft-Windows-WHEA-Logger' -and
                    $event.Id -eq 17
                $isDisplay = $provider -match
                    '(^Display$|amdwddmg|amdkmdag|DxgKrnl|DisplayDriver)'
                $isVolmgr = $provider -match '(^volmgr$|volmgr)'
                $isWatchdog = $provider -match 'Windows Error Reporting|Watchdog' -and
                    $message -match 'LiveKernelEvent|141|117|WATCHDOG'
                if ($isWhea17 -or $isDisplay -or $isVolmgr -or $isWatchdog) {
                    $result.Add([ordered]@{
                        LogName = $logName
                        RecordId = [long]$event.RecordId
                        TimeCreatedUtc = $event.TimeCreated.ToUniversalTime().ToString('o')
                        Provider = $provider
                        Id = $event.Id
                        Level = $event.LevelDisplayName
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
            $result.Add([ordered]@{
                LogName = $logName
                RecordId = $null
                WHEA17 = $false
                Display = $false
                Volmgr = $false
                Watchdog = $false
                QueryError = $_.Exception.Message
            })
        }
    }
    return $result.ToArray()
}

function Get-PerformanceSnapshot {
    $snapshot = [ordered]@{}
    try {
        $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor `
            -Filter "Name='_Total'" -ErrorAction Stop
        $snapshot.CpuUtilityPercent = [double]$cpu.PercentProcessorTime
    }
    catch {
        $snapshot.CpuUtilityError = $_.Exception.Message
    }

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
    }
    catch {
        $snapshot.GpuCounterError = $_.Exception.Message
    }
    return $snapshot
}

function Get-ChildSnapshot {
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($process in @($children)) {
        try {
            $process.Refresh()
            $result.Add([ordered]@{
                Name = $process.ProcessName
                Id = $process.Id
                HasExited = $process.HasExited
                Responding = if ($process.HasExited) { $null } else {
                    try { $process.Responding } catch { $null }
                }
                CpuSeconds = if ($process.HasExited) { $null } else {
                    try { $process.TotalProcessorTime.TotalSeconds } catch { $null }
                }
                WorkingSetBytes = if ($process.HasExited) { $null } else {
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

function Get-FanControlSnapshot {
    $processes = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue)
    $snapshot = [ordered]@{ Count = $processes.Count }
    if ($processes.Count -eq 1) {
        $process = $processes[0]
        $snapshot.Id = $process.Id
        $snapshot.Responding = try { [bool]$process.Responding } catch { $false }
        $snapshot.CpuSeconds = try {
            $process.TotalProcessorTime.TotalSeconds
        } catch {
            $null
        }
        $snapshot.WorkingSetBytes = try { $process.WorkingSet64 } catch { $null }
    }
    return $snapshot
}

function Start-WorkloadProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $stdout = Join-Path $resolvedOutput "$Label.stdout.log"
    $stderr = Join-Path $resolvedOutput "$Label.stderr.log"
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList `
        -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $children.Add($process)
    Write-Checkpoint -Kind 'workload-start' -Data @{
        Label = $Label
        FilePath = $FilePath
        Arguments = $ArgumentList
        ProcessId = $process.Id
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

function Stop-ChildProcesses {
    foreach ($process in @($children)) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                Write-Checkpoint -Kind 'workload-stop' -Data @{
                    ProcessId = $process.Id
                    ProcessName = $process.ProcessName
                }
            }
        }
        catch {
            Write-Checkpoint -Kind 'cleanup-error' -Data @{
                ProcessId = $process.Id
                Error = $_.Exception.Message
            }
        }
    }
}

function Start-IndependentHeartbeat {
    param([Parameter(Mandatory = $true)][string] $Path)

    $scriptText = @"
`$p = '$($Path.Replace("'", "''"))'
`$stop = '$($heartbeatStopPath.Replace("'", "''"))'
`$encoding = [Text.UTF8Encoding]::new(`$false)
`$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not [IO.File]::Exists(`$stop)) {
    `$record = [ordered]@{ Utc = [DateTimeOffset]::UtcNow.ToString('o'); MonotonicMilliseconds = `$clock.Elapsed.TotalMilliseconds; ParentProcessId = $PID }
    `$line = (`$record | ConvertTo-Json -Compress) + [Environment]::NewLine
    `$stream = [IO.FileStream]::new(`$p, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { `$writer = [IO.StreamWriter]::new(`$stream, `$encoding, 4096, `$true); try { `$writer.Write(`$line); `$writer.Flush(); `$stream.Flush(`$true) } finally { `$writer.Dispose() } } finally { `$stream.Dispose() }
    Start-Sleep -Milliseconds 1000
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($scriptText))
    return Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) `
        -PassThru -WindowStyle Hidden
}

function Assert-Preflight {
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
if ((Test-Path -LiteralPath $checkpointPath) -or
    (Test-Path -LiteralPath $heartbeatPath) -or
    (Test-Path -LiteralPath $heartbeatStopPath)) {
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
    $baselineLiveKernelReports = Get-LiveKernelReportInventory
    Write-DurableText -Path (Join-Path $resolvedOutput 'livekernel-before.json') `
        -Text (($baselineLiveKernelReports | ConvertTo-Json -Depth 8) + [Environment]::NewLine) `
        -CreateNew
    $baselineEvents = @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)
    foreach ($event in $baselineEvents) {
        if ($null -ne $event.RecordId) {
            $observedEventRecordIds["$($event.LogName)|$($event.RecordId)"] = $true
        }
    }
    $stageStopwatch.Restart()
    Write-Checkpoint -Kind 'stage-start' -Data @{
        Name = $Name
        Workload = $Workload
        DurationSeconds = $Duration
        FanControlRequired = $FanControlRequired
        ProcessId = $PID
        StartUtc = $stageStartUtc.ToString('o')
        BaselineLiveKernelReports = $baselineLiveKernelReports
        BaselineRelevantEvents = $baselineEvents
    }

    $heartbeatProcess = Start-IndependentHeartbeat -Path $heartbeatPath
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

    while ($stageStopwatch.Elapsed.TotalSeconds -lt $Duration) {
        $sampleStartMilliseconds = $stageStopwatch.Elapsed.TotalMilliseconds
        $events = [System.Collections.Generic.List[object]]::new()
        foreach ($event in @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)) {
            if ($null -eq $event.RecordId) {
                $events.Add($event)
                continue
            }
            $eventKey = "$($event.LogName)|$($event.RecordId)"
            if (-not $observedEventRecordIds.ContainsKey($eventKey)) {
                $observedEventRecordIds[$eventKey] = $true
                $events.Add($event)
            }
        }
        $childrenSnapshot = @(Get-ChildSnapshot)
        $fanControlSnapshot = Get-FanControlSnapshot
        $exitedChildren = @($childrenSnapshot | Where-Object {
            $true -eq $_.HasExited
        })
        $reasons = [System.Collections.Generic.List[string]]::new()
        foreach ($event in $events) {
            if ($event.WHEA17) { $reasons.Add('new-whea17') }
            if ($event.Display) { $reasons.Add('new-display-event') }
            if ($event.Watchdog) { $reasons.Add('new-watchdog-event') }
        }
        if ($exitedChildren.Count -ne 0) {
            $reasons.Add('workload-exited-before-stage-end')
        }
        if ($FanControlRequired -and $fanControlSnapshot.Count -ne 1) {
            $reasons.Add('fancontrol-process-count-changed')
        }
        elseif ($FanControlRequired -and
            $fanControlSnapshot.Responding -ne $true) {
            $reasons.Add('fancontrol-not-responding')
        }
        elseif (-not $FanControlRequired -and $fanControlSnapshot.Count -ne 0) {
            $reasons.Add('fancontrol-started-during-baseline')
        }
        $abortReason = @($reasons | Select-Object -Unique) -join ','
        Write-Checkpoint -Kind 'sample' -Data @{
            Performance = Get-PerformanceSnapshot
            Children = $childrenSnapshot
            FanControl = $fanControlSnapshot
            NewRelevantEvents = $events
            AbortReason = if ($abortReason) { $abortReason } else { $null }
        }
        if ($abortReason) {
            throw "Stage aborted: $abortReason"
        }
        $sampleWorkMilliseconds =
            $stageStopwatch.Elapsed.TotalMilliseconds - $sampleStartMilliseconds
        $remainingMilliseconds = [int][Math]::Floor(
            1000.0 - $sampleWorkMilliseconds)
        if ($remainingMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $remainingMilliseconds
        }
    }
    Write-Checkpoint -Kind 'stage-complete' -Data @{ Status = 'completed' }
}
catch {
    $failure = $_.Exception.Message
    if (-not $abortReason) { $abortReason = 'script-error' }
    if ($null -ne $checkpointWriter) {
        Write-Checkpoint -Kind 'stage-failure' -Data @{
            AbortReason = $abortReason
            Error = $failure
        }
    }
}
finally {
    if ($null -ne $checkpointWriter) {
        Stop-ChildProcesses
    }
    if ($heartbeatStopPath) {
        try { Write-DurableText -Path $heartbeatStopPath -Text 'stop' -CreateNew } catch { }
    }
    if ($heartbeatProcess) {
        try { $heartbeatProcess.WaitForExit(3000) | Out-Null } catch { }
        try {
            if (-not $heartbeatProcess.HasExited) {
                Stop-Process -Id $heartbeatProcess.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    if ($null -ne $checkpointWriter) {
        $afterLiveKernelReports = Get-LiveKernelReportInventory
        try {
            Write-DurableText -Path (Join-Path $resolvedOutput 'livekernel-after.json') `
                -Text (($afterLiveKernelReports | ConvertTo-Json -Depth 8) + [Environment]::NewLine) `
                -CreateNew
            Write-Checkpoint -Kind 'stage-end' -Data @{
                Status = if ($failure) { 'failed' } else { 'completed' }
                AbortReason = $abortReason
                Error = $failure
                AfterLiveKernelReports = $afterLiveKernelReports
                FinalRelevantEvents = @(Get-EventRecords -StartTime $stageStartUtc.UtcDateTime)
            }
        }
        finally {
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
