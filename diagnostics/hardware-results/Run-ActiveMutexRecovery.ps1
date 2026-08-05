[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $IpcExecutable,

    [Parameter(Mandatory = $true)]
    [string] $IpcAssembly,

    [Parameter(Mandatory = $true)]
    [string] $ConfigDirectory,

    [Parameter(Mandatory = $true)]
    [string] $MutexExecutable,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $CpuControlId =
        'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$ids = [ordered]@{
    CpuRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1'
    SystemRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2'
    CpuTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature'
    SystemTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature'
    CpuControl = $CpuControlId
    SystemControl = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2'
}

function Invoke-Ipc {
    param([string] $Command, [string[]] $Arguments, [string] $OutputName)

    $outputPath = Join-Path $OutputDirectory $OutputName
    & $IpcExecutable $IpcAssembly $Command @Arguments --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        $detail = if (Test-Path -LiteralPath $outputPath) {
            [System.IO.File]::ReadAllText($outputPath)
        } else {
            'No IPC output was written.'
        }
        throw "$Command failed with exit $LASTEXITCODE`: $detail"
    }
    [System.IO.File]::ReadAllText($outputPath)
}

function Load-Config {
    param([string] $Name, [string] $OutputName)

    $reply = Invoke-Ipc load @((Join-Path $ConfigDirectory $Name)) $OutputName
    if ($reply -notmatch '"status":\s*"OK"') {
        throw "Fan Control rejected $Name`: $reply"
    }
}

function Get-Value {
    param($Snapshot, [string] $Identifier)

    ($Snapshot.Sensors | Where-Object Identifier -eq $Identifier |
        Select-Object -First 1).Value
}

function Get-Snapshot {
    param([string] $OutputName)

    Invoke-Ipc plugin-sensors @() $OutputName | ConvertFrom-Json
}

function Wait-ForState {
    param([bool] $Active, [string] $Prefix, [int] $Attempts)

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        Start-Sleep -Seconds 1
        $snapshot = Get-Snapshot "$Prefix-$attempt.json"
        $process = Get-Process FanControl -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $cpuRpm = Get-Value $snapshot $ids.CpuRpm
        $systemRpm = Get-Value $snapshot $ids.SystemRpm
        $cpuTemperature = Get-Value $snapshot $ids.CpuTemperature
        $systemTemperature = Get-Value $snapshot $ids.SystemTemperature
        $cpuControl = Get-Value $snapshot $ids.CpuControl
        $systemControl = Get-Value $snapshot $ids.SystemControl
        $complete = [bool]($process -and $process.Responding) -and
            $null -ne $cpuRpm -and $null -ne $systemRpm -and
            $null -ne $cpuTemperature -and $null -ne $systemTemperature
        $controlsMatch = if ($Active) {
            $null -ne $cpuControl -and $null -ne $systemControl -and
            [Math]::Abs([double]$cpuControl - 18.0 * 100.0 / 51.0) -le 0.1 -and
            [Math]::Abs([double]$systemControl - 100.0) -le 0.1
        } else {
            $null -eq $cpuControl -and $null -eq $systemControl
        }
        if ($complete -and $controlsMatch) {
            return [ordered]@{
                Attempt = $attempt
                FanControlResponding = $true
                CpuRpm = $cpuRpm
                SystemRpm = $systemRpm
                CpuTemperatureC = $cpuTemperature
                SystemTemperatureC = $systemTemperature
                CpuControlPercent = $cpuControl
                SystemControlPercent = $systemControl
            }
        }
    }
    throw "Fan Control did not reach the requested active=$Active state."
}

$failure = $null
$cleanupFailure = $null
$activeState = $null
$duringHold = $null
$finalState = $null
$mutexProcess = $null
try {
    Load-Config 'fc-disabled.json' 'pre-disabled-load.json'
    $null = Wait-ForState $false 'pre-disabled-sensors' 5
    Load-Config 'fc-combined18-51.json' 'active-load.json'
    $activeState = Wait-ForState $true 'active-sensors' 10

    $mutexOut = Join-Path $OutputDirectory 'mutex-hold.log'
    $mutexError = Join-Path $OutputDirectory 'mutex-hold-error.log'
    $mutexProcess = Start-Process -FilePath $MutexExecutable `
        -ArgumentList @('hold', '5') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $mutexOut -RedirectStandardError $mutexError
    Start-Sleep -Seconds 2
    $duringHold = Get-Snapshot 'during-hold-sensors.json'
    if (-not $mutexProcess.WaitForExit(15000)) {
        throw 'The bounded mutex helper did not exit; it was not force-stopped.'
    }
    $mutexProcess.WaitForExit()
    $mutexLog = [System.IO.File]::ReadAllText($mutexOut)
    $mutexErrorText = [System.IO.File]::ReadAllText($mutexError)
    if ($mutexLog -notmatch 'Released the ISA mutex' -or
        -not [string]::IsNullOrWhiteSpace($mutexErrorText)) {
        throw "The mutex helper did not report a clean release: $mutexErrorText"
    }
    Start-Sleep -Seconds 4
}
catch {
    $failure = $_.Exception.ToString()
}
finally {
    if ($mutexProcess -and -not $mutexProcess.HasExited) {
        $null = $mutexProcess.WaitForExit(20000)
    }
    try {
        Load-Config 'fc-disabled.json' 'cleanup-disabled-load.json'
        Start-Sleep -Seconds 2
        $null = Invoke-Ipc refresh @() 'cleanup-refresh.json'
        Start-Sleep -Seconds 4
        Load-Config 'fc-disabled.json' 'post-refresh-disabled-load.json'
        $finalState = Wait-ForState $false 'final-sensors' 5
    }
    catch {
        $cleanupFailure = $_.Exception.ToString()
    }
}

$summary = [ordered]@{
    Status = if ($failure -or $cleanupFailure) { 'FAILED' } else { 'PASS' }
    ActiveState = $activeState
    DuringHold = $duringHold
    FinalState = $finalState
    Failure = $failure
    CleanupFailure = $cleanupFailure
    CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
$summary | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') `
        -Encoding UTF8
$summary | ConvertTo-Json -Depth 8
if ($failure -or $cleanupFailure) {
    exit 1
}
