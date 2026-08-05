[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $IpcExecutable,

    [Parameter(Mandatory = $true)]
    [string] $IpcAssembly,

    [Parameter(Mandatory = $true)]
    [string] $FanControlExecutable,

    [Parameter(Mandatory = $true)]
    [string] $ConfigDirectory,

    [Parameter(Mandatory = $true)]
    [string] $PristineConfigDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ExternalAbortPath,

    [ValidateRange(5, 60)]
    [int] $HoldSeconds = 10,

    [ValidateRange(1, 10)]
    [int] $Repeat = 1,

    [switch] $ValidateOnly,

    [int[]] $Codes = @(10, 18, 30, 18, 10, 18)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)

$cpuControlId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4'
$systemControlId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2'
$cpuRpmId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1'
$systemRpmId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2'
$cpuTemperatureId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature'
$systemTemperatureId =
    'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature'

$resolvedIpcExecutable = [IO.Path]::GetFullPath($IpcExecutable)
$resolvedIpcAssembly = [IO.Path]::GetFullPath($IpcAssembly)
$resolvedFanControlExecutable = [IO.Path]::GetFullPath($FanControlExecutable)
$resolvedConfigDirectory = [IO.Path]::GetFullPath($ConfigDirectory)
$resolvedPristineDirectory = [IO.Path]::GetFullPath($PristineConfigDirectory)
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedAbortPath = [IO.Path]::GetFullPath($ExternalAbortPath)
$ledgerPath = Join-Path $resolvedOutputDirectory 'sequence.jsonl'
$summaryPath = Join-Path $resolvedOutputDirectory 'summary.json'
$latestSnapshotPath = Join-Path $resolvedOutputDirectory 'latest-sensors.json'

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

function Get-SensorValue {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Identifier
    )

    $matches = @($Snapshot.Sensors | Where-Object Identifier -eq $Identifier)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one sensor '$Identifier'; found $($matches.Count)."
    }
    return $matches[0].Value
}

function Write-DurableJsonLine {
    param([Parameter(Mandatory = $true)] $Value)

    $writer.WriteLine(($Value | ConvertTo-Json -Depth 8 -Compress))
    $writer.Flush()
    $stream.Flush($true)
}

function Write-DurableAbort {
    param([Parameter(Mandatory = $true)][string] $Reason)

    if (Test-Path -LiteralPath $resolvedAbortPath) {
        return
    }
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($resolvedAbortPath)) | Out-Null
    try {
        $abortStream = [IO.FileStream]::new(
            $resolvedAbortPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try {
            $abortWriter = [IO.StreamWriter]::new(
                $abortStream, $utf8NoBom, 4096, $true)
            try {
                $abortWriter.WriteLine((([ordered]@{
                    Status = 'ABORT'
                    Utc = [DateTimeOffset]::UtcNow.ToString('o')
                    Source = 'Run-A5DynamicConfigSequence'
                    Reason = $Reason
                }) | ConvertTo-Json -Compress))
                $abortWriter.Flush()
                $abortStream.Flush($true)
            }
            finally {
                $abortWriter.Dispose()
            }
        }
        finally {
            $abortStream.Dispose()
        }
    }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $resolvedAbortPath)) {
            throw
        }
    }
}

function Invoke-Ipc {
    param(
        [Parameter(Mandatory = $true)][string] $Command,
        [string[]] $CommandArguments = @(),
        [Parameter(Mandatory = $true)][string] $OutputPath
    )

    & $resolvedIpcExecutable $resolvedIpcAssembly $Command @CommandArguments `
        --output $OutputPath
    if ($LASTEXITCODE -ne 0) {
        $detail = if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
            [IO.File]::ReadAllText($OutputPath)
        }
        else {
            'No IPC output was written.'
        }
        throw "$Command failed with exit $LASTEXITCODE`: $detail"
    }
    return [IO.File]::ReadAllText($OutputPath)
}

function Assert-FanControlIdentity {
    $processes = @(Get-Process -Name FanControl -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1 -or $processes[0].Id -ne $fanControlId -or
            $processes[0].StartTime.ToUniversalTime().Ticks -ne
                $fanControlStartTicks -or -not $processes[0].Responding -or
            -not [StringComparer]::OrdinalIgnoreCase.Equals(
                $processes[0].Path, $resolvedFanControlExecutable)) {
        throw 'The expected Fan Control process changed or stopped responding.'
    }
}

function Get-ProfilePath {
    param([Parameter(Mandatory = $true)][int] $Code)

    return Join-Path $resolvedConfigDirectory `
        "a5-dynamic-cpu$Code-system51.json"
}

function Assert-Profile {
    param([Parameter(Mandatory = $true)][int] $Code)

    $fileName = "a5-dynamic-cpu$Code-system51.json"
    $deployed = Join-Path $resolvedConfigDirectory $fileName
    $pristine = Join-Path $resolvedPristineDirectory $fileName
    foreach ($path in @($deployed, $pristine)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required A5 profile is missing: $path"
        }
    }
    if ((Get-FileHash -LiteralPath $deployed -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $pristine -Algorithm SHA256).Hash) {
        throw "Deployed A5 profile differs from pristine evidence: $fileName"
    }
    $config = [IO.File]::ReadAllText($deployed) | ConvertFrom-Json
    if ([string]$config.__VERSION__ -ne '272') {
        throw "A5 profile is not a Fan Control V272 config: $fileName"
    }
    $controls = @($config.FanControl.Controls)
    $expectedCurveName = "A5 CPU Flat Native $Code"
    if ($controls.Count -ne 2 -or
            $controls[0].Identifier -ne $cpuControlId -or
            $controls[1].Identifier -ne $systemControlId -or
            $controls[0].PairedFanSensor.Identifier -ne $cpuRpmId -or
            $controls[1].PairedFanSensor.Identifier -ne $systemRpmId -or
            $controls[0].Enable -ne $true -or
            $controls[0].ManualControl -ne $false -or
            [double]$controls[0].ManualControlValue -ne 35 -or
            $controls[0].SelectedFanCurve.Name -ne $expectedCurveName -or
            [double]$controls[0].SelectedCommandStepUp -ne 100 -or
            [double]$controls[0].SelectedCommandStepDown -ne 100 -or
            $controls[0].ForceApply -ne $false -or
            $controls[1].Enable -ne $true -or
            $controls[1].ManualControl -ne $true -or
            [double]$controls[1].ManualControlValue -ne 100 -or
            $null -ne $controls[1].SelectedFanCurve -or
            [double]$controls[1].SelectedCommandStepUp -ne 100 -or
            [double]$controls[1].SelectedCommandStepDown -ne 100 -or
            $controls[1].ForceApply -ne $false) {
        throw "A5 profile failed control validation: $fileName"
    }
    $fanSensors = @($config.FanControl.FanSensors)
    if ($fanSensors.Count -ne 2 -or
            $fanSensors[0].Identifier -ne $cpuRpmId -or
            $fanSensors[1].Identifier -ne $systemRpmId) {
        throw "A5 profile failed tachometer validation: $fileName"
    }
    $curves = @($config.FanControl.FanCurves)
    $expectedCurves = [ordered]@{
        'A5 CPU Flat Native 10' = 10
        'A5 CPU Flat Native 18' = 18
        'A5 CPU Flat Native 30' = 30
    }
    if ($curves.Count -ne $expectedCurves.Count -or
            @($curves.Name | Sort-Object -Unique).Count -ne
                $expectedCurves.Count) {
        throw "A5 profile failed curve-count validation: $fileName"
    }
    foreach ($curveName in $expectedCurves.Keys) {
        $matches = @($curves | Where-Object Name -eq $curveName)
        if ($matches.Count -ne 1) {
            throw "A5 profile is missing exact curve '$curveName': $fileName"
        }
        $curve = $matches[0]
        $curveCode = [int]$expectedCurves[$curveName]
        $expectedPercent = [double]$curveCode * 100.0 / 51.0
        if ($curve.CommandMode -ne 0 -or
                $curve.SelectedTempSource.Identifier -ne $cpuTemperatureId -or
                [double]$curve.MinimumTemperature -ne 20 -or
                [double]$curve.MaximumTemperature -ne 120 -or
                [double]$curve.MaximumCommand -ne 100 -or
                $curve.IsHidden -ne $false) {
            throw "A5 profile curve metadata is invalid for '$curveName': $fileName"
        }
        $points = @($curve.Points)
        if ($points.Count -ne 2) {
            throw "A5 profile curve '$curveName' is not exactly two points: $fileName"
        }
        for ($pointIndex = 0; $pointIndex -lt 2; $pointIndex++) {
            if ($points[$pointIndex] -isnot [string]) {
                throw "A5 profile curve '$curveName' has a non-string point: $fileName"
            }
            $parts = ([string]$points[$pointIndex]).Split(',')
            $temperature = 0.0
            $percent = 0.0
            if ($parts.Count -ne 2 -or
                    -not [double]::TryParse(
                        $parts[0], [Globalization.NumberStyles]::Float,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [ref]$temperature) -or
                    -not [double]::TryParse(
                        $parts[1], [Globalization.NumberStyles]::Float,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [ref]$percent) -or
                    [double]::IsNaN($temperature) -or
                    [double]::IsInfinity($temperature) -or
                    [double]::IsNaN($percent) -or
                    [double]::IsInfinity($percent) -or
                    [Math]::Abs(
                        $temperature - @(20.0, 120.0)[$pointIndex]) -gt
                        0.000001 -or
                    [Math]::Abs($percent - $expectedPercent) -gt 0.000001) {
                throw "A5 profile curve '$curveName' has an invalid point: $fileName"
            }
        }
    }
    $lhm = $config.Sensors.LibreHardwareMonitorSettings
    if ($lhm.Controller -ne $false -or $lhm.EmbeddedEC -ne $false -or
            $lhm.Motherboard -ne $false) {
        throw "A5 profile enables conflicting LHM polling: $fileName"
    }
}

foreach ($path in @(
        $resolvedIpcExecutable, $resolvedIpcAssembly,
        $resolvedFanControlExecutable)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required executable or assembly is missing: $path"
    }
}
if (Test-Path -LiteralPath $resolvedOutputDirectory) {
    throw "OutputDirectory must not exist: $resolvedOutputDirectory"
}
if (Test-Path -LiteralPath $resolvedAbortPath) {
    throw "The external abort already exists: $resolvedAbortPath"
}
if ($Codes.Count -lt 3 -or @($Codes | Sort-Object -Unique).Count -lt 3 -or
        $Codes[-1] -ne 18) {
    throw 'Codes must contain at least three distinct targets and end at 18.'
}
foreach ($code in $Codes) {
    if ($code -notin @(10, 18, 30)) {
        throw "Unsupported A5 native code: $code"
    }
}
foreach ($code in @(10, 18, 30)) {
    Assert-Profile -Code $code
}
if ($ValidateOnly) {
    [pscustomobject]@{
        Status = 'VALID'
        Codes = $Codes
        HoldSeconds = $HoldSeconds
        Repeat = $Repeat
        Profiles = @(10, 18, 30 | ForEach-Object {
            $path = Get-ProfilePath -Code $_
            [pscustomobject]@{
                Code = $_
                Path = $path
                Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
        })
    } | ConvertTo-Json -Depth 5
    return
}

$fanControlProcesses = @(Get-Process -Name FanControl -ErrorAction SilentlyContinue)
if ($fanControlProcesses.Count -ne 1 -or
        -not $fanControlProcesses[0].Responding -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals(
            $fanControlProcesses[0].Path, $resolvedFanControlExecutable)) {
    throw 'Exactly one responsive expected Fan Control process is required.'
}
$fanControlId = $fanControlProcesses[0].Id
$fanControlStartTicks =
    $fanControlProcesses[0].StartTime.ToUniversalTime().Ticks

[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null
$stream = [IO.FileStream]::new(
    $ledgerPath, [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, $utf8NoBom, 4096, $true)
$completedPhases = [Collections.Generic.List[string]]::new()
$failure = $null
$finalCode18Verified = $false
$phaseNumber = 0
$clock = [Diagnostics.Stopwatch]::StartNew()

try {
    for ($cycle = 1; $cycle -le $Repeat; $cycle++) {
        foreach ($code in $Codes) {
            if (Test-Path -LiteralPath $resolvedAbortPath) {
                throw 'The external guard abort was observed before a transition.'
            }
            Assert-FanControlIdentity
            Assert-Profile -Code $code
            $phaseNumber++
            $label = 'cycle-{0:D2}-phase-{1:D2}-code-{2:D2}' -f `
                $cycle, $phaseNumber, $code
            $loadReplyPath = Join-Path $resolvedOutputDirectory "$label-load.json"
            $loadReply = Invoke-Ipc -Command load `
                -CommandArguments @((Get-ProfilePath -Code $code)) `
                -OutputPath $loadReplyPath | ConvertFrom-Json
            if (Test-Path -LiteralPath $resolvedAbortPath) {
                throw 'The external guard abort appeared during a transition.'
            }
            if ($loadReply.status -ne 'OK') {
                throw "Fan Control rejected $label."
            }
            Assert-FanControlIdentity
            Write-DurableJsonLine -Value ([ordered]@{
                Kind = 'transition'
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                MonotonicMilliseconds = $clock.Elapsed.TotalMilliseconds
                Cycle = $cycle
                Phase = $phaseNumber
                Code = $code
                Label = $label
            })

            $expectedCpuPercent = [double]$code * 100.0 / 51.0
            for ($sample = 0; $sample -lt $HoldSeconds; $sample++) {
                Start-Sleep -Seconds 1
                if (Test-Path -LiteralPath $resolvedAbortPath) {
                    throw 'The external guard abort was observed during a hold.'
                }
                Assert-FanControlIdentity
                $snapshot = Invoke-Ipc -Command plugin-sensors `
                    -OutputPath $latestSnapshotPath | ConvertFrom-Json
                if (Test-Path -LiteralPath $resolvedAbortPath) {
                    throw 'The external guard abort appeared during a sensor RPC.'
                }
                $cpuControl = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $cpuControlId
                $systemControl = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $systemControlId
                $cpuRpm = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $cpuRpmId
                $systemRpm = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $systemRpmId
                $cpuTemperature = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $cpuTemperatureId
                $systemTemperature = Get-SensorValue -Snapshot $snapshot `
                    -Identifier $systemTemperatureId
                Write-DurableJsonLine -Value ([ordered]@{
                    Kind = 'sample'
                    Utc = [DateTimeOffset]::UtcNow.ToString('o')
                    MonotonicMilliseconds = $clock.Elapsed.TotalMilliseconds
                    Cycle = $cycle
                    Phase = $phaseNumber
                    Code = $code
                    Sequence = $sample
                    CpuControlPercent = $cpuControl
                    SystemControlPercent = $systemControl
                    CpuRpm = $cpuRpm
                    SystemRpm = $systemRpm
                    CpuTemperatureC = $cpuTemperature
                    SystemTemperatureC = $systemTemperature
                })
                if ($sample -ge 2) {
                    if (-not (Test-ControlNumber -Value $cpuControl) -or
                            [Math]::Abs(
                                [double]$cpuControl - $expectedCpuPercent) -gt 0.1) {
                        throw "CPU control did not settle at native code $code."
                    }
                    if (-not (Test-ControlNumber -Value $systemControl) -or
                            [Math]::Abs([double]$systemControl - 100.0) -gt 0.1) {
                        throw 'System control did not remain exactly 100 percent.'
                    }
                    if ($null -eq $cpuRpm -or $null -eq $systemRpm -or
                            $null -eq $cpuTemperature -or
                            $null -eq $systemTemperature) {
                        throw 'Plugin telemetry was incomplete after transition grace.'
                    }
                    if ([double]$systemRpm -lt 3000) {
                        throw "System fan fell below 3000 RPM: $systemRpm"
                    }
                    if ([double]$cpuTemperature -gt 95 -or
                            [double]$systemTemperature -gt 69) {
                        throw 'A plugin temperature exceeded the campaign maximum.'
                    }
                }
            }
            if (Test-Path -LiteralPath $resolvedAbortPath) {
                throw 'The external guard abort appeared at the end of a hold.'
            }
            $completedPhases.Add($label)
        }
    }
}
catch {
    $failure = $_.Exception.ToString()
    Write-DurableAbort -Reason $failure
}
finally {
    if (-not $failure) {
        try {
            if (Test-Path -LiteralPath $resolvedAbortPath) {
                throw 'The external guard abort appeared before final verification.'
            }
            Assert-FanControlIdentity
            if ($completedPhases.Count -ne ($Codes.Count * $Repeat) -or
                    $Codes[-1] -ne 18) {
                throw 'The sequence did not complete on native code 18.'
            }
            $finalCode18Verified = $true
        }
        catch {
            $failure = "Final native-18 verification failed: $($_.Exception)"
            Write-DurableAbort -Reason $failure
        }
    }
    if ($finalCode18Verified) {
        Write-DurableJsonLine -Value ([ordered]@{
            Kind = 'finally-code18-verified'
            Utc = [DateTimeOffset]::UtcNow.ToString('o')
            MonotonicMilliseconds = $clock.Elapsed.TotalMilliseconds
            Status = 'verified-without-extra-load'
        })
    }
    $writer.Dispose()
    $stream.Dispose()
}

$summary = [ordered]@{
    Status = if ($failure) { 'FAIL' } else { 'PASS' }
    Utc = [DateTimeOffset]::UtcNow.ToString('o')
    DurationSeconds = $clock.Elapsed.TotalSeconds
    HoldSeconds = $HoldSeconds
    Repeat = $Repeat
    Codes = $Codes
    CompletedPhases = $completedPhases.ToArray()
    Failure = $failure
    FinalCode18Verified = $finalCode18Verified
    LedgerPath = $ledgerPath
    ExternalAbortPath = $resolvedAbortPath
}
[IO.File]::WriteAllText(
    $summaryPath, ($summary | ConvertTo-Json -Depth 8), $utf8NoBom)
$summary | ConvertTo-Json -Depth 8
if ($failure) {
    exit 1
}
