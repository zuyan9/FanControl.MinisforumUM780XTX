[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $IpcExecutable,

    [Parameter(Mandatory = $true)]
    [string] $IpcAssembly,

    [Parameter(Mandatory = $true)]
    [string] $ConfigDirectory,

    [Parameter(Mandatory = $true)]
    [string] $SequencePath,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $CpuBurnExecutable
)

$ErrorActionPreference = 'Stop'
$sequence = [System.IO.File]::ReadAllText($SequencePath) |
    ConvertFrom-Json
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$ledgerPath = Join-Path $OutputDirectory 'sequence-telemetry.jsonl'
$summaryPath = Join-Path $OutputDirectory 'sequence-summary.json'
$writer = [System.IO.StreamWriter]::new($ledgerPath, $false)
$writer.AutoFlush = $true

$ids = [ordered]@{
    CpuRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1'
    SystemRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2'
    CpuTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature'
    SystemTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature'
    CpuControl = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-native-v3'
    SystemControl = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2'
}

function Invoke-Ipc {
    param(
        [string] $Command,
        [string[]] $CommandArguments,
        [string] $OutputPath
    )

    & $IpcExecutable $IpcAssembly $Command @CommandArguments `
        --output $OutputPath
    if ($LASTEXITCODE -ne 0) {
        $detail = if (Test-Path -LiteralPath $OutputPath) {
            [System.IO.File]::ReadAllText($OutputPath)
        } else {
            'No IPC output was written.'
        }
        throw "$Command failed with exit $LASTEXITCODE`: $detail"
    }
    [System.IO.File]::ReadAllText($OutputPath)
}

function Load-Config {
    param([string] $FileName, [string] $Label)

    $path = Join-Path $ConfigDirectory $FileName
    $replyPath = Join-Path $OutputDirectory "$Label-load.json"
    $reply = Invoke-Ipc -Command load -CommandArguments @($path) `
        -OutputPath $replyPath
    if ($reply -notmatch '"status":\s*"OK"') {
        throw "Fan Control rejected $FileName`: $reply"
    }
}

function Get-SensorValue {
    param($Snapshot, [string] $Identifier)

    ($Snapshot.Sensors | Where-Object Identifier -eq $Identifier |
        Select-Object -First 1).Value
}

function Test-ControlValue {
    param($Value, [bool] $Required)

    if ($null -eq $Value) {
        return -not $Required
    }
    $number = [double]$Value
    -not [double]::IsNaN($number) -and
        -not [double]::IsInfinity($number) -and
        $number -ge 0 -and $number -le 100
}

$completed = @()
$failure = $null
$activeBurn = $null
$finallyDisabled = $null
$repeat = if ($null -eq $sequence.Repeat) { 1 } else { [int]$sequence.Repeat }
if ($repeat -lt 1 -or $repeat -gt 100) {
    throw 'Sequence Repeat must be between 1 and 100.'
}
try {
    Load-Config -FileName $sequence.DisabledConfigFile `
        -Label 'pre-sequence-disabled'
    $disabledConfirmed = $false
    for ($attempt = 1; -not $disabledConfirmed -and $attempt -le 5;
         $attempt++) {
        Start-Sleep -Seconds 1
        $snapshotPath = Join-Path $OutputDirectory `
            'pre-sequence-disabled-sensors.json'
        $snapshot = Invoke-Ipc -Command plugin-sensors `
            -CommandArguments @() -OutputPath $snapshotPath |
            ConvertFrom-Json
        $fanControl = Get-Process FanControl -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $cpuRpm = Get-SensorValue $snapshot $ids.CpuRpm
        $systemRpm = Get-SensorValue $snapshot $ids.SystemRpm
        $cpuTemperature = Get-SensorValue $snapshot $ids.CpuTemperature
        $systemTemperature = Get-SensorValue $snapshot $ids.SystemTemperature
        $cpuControl = Get-SensorValue $snapshot $ids.CpuControl
        $systemControl = Get-SensorValue $snapshot $ids.SystemControl
        $disabledConfirmed = [bool]($fanControl -and
            $fanControl.Responding) -and
            $null -ne $cpuRpm -and $null -ne $systemRpm -and
            $null -ne $cpuTemperature -and
            $null -ne $systemTemperature -and
            $null -eq $cpuControl -and $null -eq $systemControl
        $writer.WriteLine((([ordered]@{
            Stage = 'pre-sequence-disabled'
            Phase = 'Preflight'
            Sequence = $attempt
            Utc = [DateTimeOffset]::UtcNow.ToString('o')
            FanControlResponding = [bool]($fanControl -and
                $fanControl.Responding)
            CpuRpm = $cpuRpm
            SystemRpm = $systemRpm
            CpuTemperatureC = $cpuTemperature
            SystemTemperatureC = $systemTemperature
            CpuControlPercent = $cpuControl
            SystemControlPercent = $systemControl
            Verified = $disabledConfirmed
        }) | ConvertTo-Json -Compress))
    }
    if (-not $disabledConfirmed) {
        throw 'The pre-sequence disabled state did not settle.'
    }

    for ($cycle = 1; $cycle -le $repeat; $cycle++) {
      foreach ($stage in $sequence.Stages) {
        $expectControlsActive = [bool]$stage.ExpectControlsActive
        $stageLabel = if ($repeat -eq 1) {
            [string]$stage.Name
        } else {
            '{0}-cycle-{1:D2}' -f $stage.Name, $cycle
        }
        Load-Config -FileName $stage.ConfigFile -Label $stageLabel
        $confirmed = -not $expectControlsActive -and
            $null -eq $stage.CpuCode -and
            $null -eq $stage.SystemCode
        for ($attempt = 1; -not $confirmed -and $attempt -le 10; $attempt++) {
            Start-Sleep -Seconds 1
            $snapshotPath = Join-Path $OutputDirectory `
                'preflight-sensors.json'
            $snapshot = Invoke-Ipc -Command plugin-sensors `
                -CommandArguments @() -OutputPath $snapshotPath |
                ConvertFrom-Json
            $fanControl = Get-Process FanControl -ErrorAction SilentlyContinue |
                Select-Object -First 1
            $cpuRpm = Get-SensorValue $snapshot $ids.CpuRpm
            $systemRpm = Get-SensorValue $snapshot $ids.SystemRpm
            $cpuTemperature = Get-SensorValue $snapshot $ids.CpuTemperature
            $systemTemperature = Get-SensorValue $snapshot $ids.SystemTemperature
            $cpuControl = Get-SensorValue $snapshot $ids.CpuControl
            $systemControl = Get-SensorValue $snapshot $ids.SystemControl
            $writer.WriteLine((([ordered]@{
                Stage = $stageLabel
                Cycle = $cycle
                Phase = 'Preflight'
                Sequence = $attempt
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                FanControlResponding = [bool]($fanControl -and
                    $fanControl.Responding)
                CpuRpm = $cpuRpm
                SystemRpm = $systemRpm
                CpuTemperatureC = $cpuTemperature
                SystemTemperatureC = $systemTemperature
                CpuControlPercent = $cpuControl
                SystemControlPercent = $systemControl
            }) | ConvertTo-Json -Compress))
            if (-not $fanControl -or -not $fanControl.Responding) {
                throw 'Fan Control stopped responding during control preflight.'
            }
            if ($null -ne $cpuTemperature -and
                $cpuTemperature -ge [double]$stage.CpuAbortC) {
                throw "CPU reached $cpuTemperature C during control preflight."
            }
            if ($null -ne $systemTemperature -and
                $systemTemperature -ge [double]$stage.SystemAbortC) {
                throw "System sensor reached $systemTemperature C during control preflight."
            }

            $cpuConfirmed = if ($expectControlsActive) {
                Test-ControlValue $cpuControl $true
            } else {
                $null -eq $stage.CpuCode -and $null -eq $cpuControl
            }
            if (-not $expectControlsActive -and
                $null -ne $stage.CpuCode -and $null -ne $cpuControl) {
                $expected = [double]$stage.CpuCode * 100.0 / 51.0
                $cpuConfirmed =
                    [Math]::Abs([double]$cpuControl - $expected) -le 0.1
            }
            $systemConfirmed = if ($expectControlsActive) {
                Test-ControlValue $systemControl $true
            } else {
                $null -eq $stage.SystemCode -and $null -eq $systemControl
            }
            if (-not $expectControlsActive -and
                $null -ne $stage.SystemCode -and $null -ne $systemControl) {
                $expected = [double]$stage.SystemCode * 100.0 / 51.0
                $systemConfirmed =
                    [Math]::Abs([double]$systemControl - $expected) -le 0.1
            }
            $telemetryConfirmed = $null -ne $cpuRpm -and
                $null -ne $systemRpm -and
                $null -ne $cpuTemperature -and
                $null -ne $systemTemperature
            $confirmed = $cpuConfirmed -and $systemConfirmed -and
                $telemetryConfirmed
        }
        if (-not $confirmed) {
            throw 'Control confirmation did not arrive within ten seconds.'
        }
        if ($null -ne $stage.CpuBurnSeconds) {
            if (-not $CpuBurnExecutable -or
                -not (Test-Path -LiteralPath $CpuBurnExecutable)) {
                throw 'The sequence requires a valid CpuBurnExecutable.'
            }
            $burnOut = Join-Path $OutputDirectory "$stageLabel-burn.log"
            $burnError = Join-Path $OutputDirectory `
                "$stageLabel-burn-error.log"
            $burnArguments = @([string]$stage.CpuBurnSeconds)
            if ($null -ne $stage.CpuBurnWorkers) {
                $burnArguments += [string]$stage.CpuBurnWorkers
            }
            $activeBurn = Start-Process -FilePath $CpuBurnExecutable `
                -ArgumentList $burnArguments -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput $burnOut `
                -RedirectStandardError $burnError
        }
        $stageClock = [System.Diagnostics.Stopwatch]::StartNew()
        for ($sample = 0; $sample -lt [int]$stage.HoldSeconds; $sample++) {
            Start-Sleep -Seconds 1
            $snapshotPath = Join-Path $OutputDirectory 'latest-sensors.json'
            $snapshot = Invoke-Ipc -Command plugin-sensors `
                -CommandArguments @() -OutputPath $snapshotPath |
                ConvertFrom-Json
            $fanControl = Get-Process FanControl -ErrorAction SilentlyContinue |
                Select-Object -First 1
            $cpuRpm = Get-SensorValue $snapshot $ids.CpuRpm
            $systemRpm = Get-SensorValue $snapshot $ids.SystemRpm
            $cpuTemperature = Get-SensorValue $snapshot $ids.CpuTemperature
            $systemTemperature = Get-SensorValue $snapshot $ids.SystemTemperature
            $cpuControl = Get-SensorValue $snapshot $ids.CpuControl
            $systemControl = Get-SensorValue $snapshot $ids.SystemControl
            $record = [ordered]@{
                Stage = $stageLabel
                Cycle = $cycle
                Sequence = $sample
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                StageMilliseconds = $stageClock.Elapsed.TotalMilliseconds
                FanControlResponding = [bool]($fanControl -and
                    $fanControl.Responding)
                CpuRpm = $cpuRpm
                SystemRpm = $systemRpm
                CpuTemperatureC = $cpuTemperature
                SystemTemperatureC = $systemTemperature
                CpuControlPercent = $cpuControl
                SystemControlPercent = $systemControl
            }
            $writer.WriteLine(($record | ConvertTo-Json -Compress))

            if (-not $record.FanControlResponding) {
                throw 'Fan Control stopped responding.'
            }
            if ($sample -ge 2 -and
                ($null -eq $cpuRpm -or $null -eq $systemRpm -or
                 $null -eq $cpuTemperature -or
                 $null -eq $systemTemperature)) {
                throw 'Plugin telemetry remained incomplete after the grace period.'
            }
            if ($null -ne $cpuTemperature -and
                $cpuTemperature -ge [double]$stage.CpuAbortC) {
                throw "CPU reached $cpuTemperature C."
            }
            if ($null -ne $systemTemperature -and
                $systemTemperature -ge [double]$stage.SystemAbortC) {
                throw "System sensor reached $systemTemperature C."
            }
            if ($sample -ge 2 -and
                $null -ne $stage.MinimumSystemRpm -and
                $null -ne $systemRpm -and
                $systemRpm -lt [double]$stage.MinimumSystemRpm) {
                throw "System fan fell to $systemRpm RPM."
            }
            if ($sample -ge 2 -and
                $null -ne $stage.MinimumCpuRpm -and
                $null -ne $cpuRpm -and
                $cpuRpm -lt [double]$stage.MinimumCpuRpm) {
                throw "CPU fan fell to $cpuRpm RPM."
            }
            if ($sample -ge 2 -and $expectControlsActive -and
                ($null -eq $cpuControl -or $null -eq $systemControl)) {
                throw 'A temperature-curve control became inactive.'
            }
            if (-not (Test-ControlValue $cpuControl $false) -or
                -not (Test-ControlValue $systemControl $false)) {
                throw 'Fan Control reported a control outside 0-100 percent.'
            }
            if ($sample -ge 2 -and -not $expectControlsActive -and
                (($null -eq $stage.CpuCode -and
                  $null -ne $cpuControl) -or
                 ($null -eq $stage.SystemCode -and
                  $null -ne $systemControl))) {
                throw 'A control expected to be disabled became active.'
            }
            if ($sample -ge 2 -and $null -ne $stage.CpuCode) {
                $expected = [double]$stage.CpuCode * 100.0 / 51.0
                if ($null -eq $cpuControl -or
                    [Math]::Abs([double]$cpuControl - $expected) -gt 0.1) {
                    throw "CPU control confirmation did not match code $($stage.CpuCode)."
                }
            }
            if ($sample -ge 2 -and $null -ne $stage.SystemCode) {
                $expected = [double]$stage.SystemCode * 100.0 / 51.0
                if ($null -eq $systemControl -or
                    [Math]::Abs([double]$systemControl - $expected) -gt 0.1) {
                    throw "System control confirmation did not match code $($stage.SystemCode)."
                }
            }
        }
        if ($activeBurn) {
            $null = $activeBurn.WaitForExit(5000)
            $activeBurn.Refresh()
            if (-not $activeBurn.HasExited) {
                throw 'CpuBurn did not stop after its configured duration.'
            }
            $burnExitCode = $activeBurn.ExitCode
            if ($null -ne $burnExitCode -and $burnExitCode -ne 0) {
                throw "CpuBurn exited with code $($activeBurn.ExitCode)."
            }
            if (-not (Test-Path -LiteralPath $burnOut) -or
                [System.IO.File]::ReadAllText($burnOut) -notmatch
                    'CPU_BURN_END') {
                throw 'CpuBurn did not write its completion marker.'
            }
            $activeBurn = $null
        }
        $completed += $stageLabel
      }
    }
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    if ($activeBurn -and -not $activeBurn.HasExited) {
        Stop-Process -Id $activeBurn.Id -Force -ErrorAction SilentlyContinue
        $null = $activeBurn.WaitForExit(5000)
    }
    try {
        Load-Config -FileName $sequence.DisabledConfigFile `
            -Label 'finally-disabled'
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            Start-Sleep -Seconds 1
            $snapshotPath = Join-Path $OutputDirectory `
                'finally-disabled-sensors.json'
            $snapshot = Invoke-Ipc -Command plugin-sensors `
                -CommandArguments @() -OutputPath $snapshotPath |
                ConvertFrom-Json
            $fanControl = Get-Process FanControl -ErrorAction SilentlyContinue |
                Select-Object -First 1
            $finallyDisabled = [ordered]@{
                Attempt = $attempt
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                FanControlResponding = [bool]($fanControl -and
                    $fanControl.Responding)
                CpuRpm = Get-SensorValue $snapshot $ids.CpuRpm
                SystemRpm = Get-SensorValue $snapshot $ids.SystemRpm
                CpuTemperatureC = Get-SensorValue $snapshot $ids.CpuTemperature
                SystemTemperatureC = Get-SensorValue $snapshot $ids.SystemTemperature
                CpuControlPercent = Get-SensorValue $snapshot $ids.CpuControl
                SystemControlPercent = Get-SensorValue $snapshot $ids.SystemControl
                Verified = $false
            }
            $finallyDisabled.Verified =
                $finallyDisabled.FanControlResponding -and
                $null -ne $finallyDisabled.CpuRpm -and
                $null -ne $finallyDisabled.SystemRpm -and
                $null -ne $finallyDisabled.CpuTemperatureC -and
                $null -ne $finallyDisabled.SystemTemperatureC -and
                $null -eq $finallyDisabled.CpuControlPercent -and
                $null -eq $finallyDisabled.SystemControlPercent
            if ($finallyDisabled.Verified) {
                break
            }
        }
        if (-not $finallyDisabled.Verified) {
            throw 'Disabled-state telemetry/control verification did not settle.'
        }
    }
    catch {
        $cleanupFailure = $_.Exception.ToString()
        $failure = if ($failure) {
            "$failure`nCleanup failure: $cleanupFailure"
        } else {
            "Cleanup failure: $cleanupFailure"
        }
    }
    $writer.Dispose()
}

$summary = [ordered]@{
    Status = if ($failure) { 'FAILED' } else { 'PASS' }
    CompletedStages = $completed
    Failure = $failure
    FinallyDisabled = $finallyDisabled
    LedgerPath = $ledgerPath
    CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
$summary | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $summaryPath -Encoding UTF8
$summary | ConvertTo-Json -Depth 5
if ($failure) {
    exit 1
}
