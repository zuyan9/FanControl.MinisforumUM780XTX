[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseConfig,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string] $Name,

    [ValidateSet(10, 18, 30)]
    [int] $InitialNativeCode = 18,

    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string] $FanControlConfigDirectory
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

function New-FlatCpuCurve {
    param(
        [Parameter(Mandatory = $true)][string] $CurveName,
        [Parameter(Mandatory = $true)][ValidateRange(0, 51)][int] $NativeCode
    )

    $percent = [double]$NativeCode * 100.0 / 51.0
    $percentText = $percent.ToString(
        '0.################', [Globalization.CultureInfo]::InvariantCulture)
    return [pscustomobject][ordered]@{
        Name = $CurveName
        IsHidden = $false
        CommandMode = 0
        SelectedTempSource = [pscustomobject][ordered]@{
            Identifier = $cpuTemperatureId
        }
        Points = @("20,$percentText", "120,$percentText")
        MaximumTemperature = 120
        MinimumTemperature = 20
        MaximumCommand = 100
        HysteresisConfig = [pscustomobject][ordered]@{
            ResponseTimeUp = 1
            ResponseTimeDown = 3
            HysteresisValueUp = 0
            HysteresisValueDown = 3
            IgnoreHysteresisAtLimits = $true
        }
    }
}

$resolvedBase = [IO.Path]::GetFullPath($BaseConfig)
$resolvedEvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$resolvedConfigDirectory = [IO.Path]::GetFullPath($FanControlConfigDirectory)
if (-not (Test-Path -LiteralPath $resolvedBase -PathType Leaf)) {
    throw "Base config is missing: $resolvedBase"
}

$config = [IO.File]::ReadAllText($resolvedBase) | ConvertFrom-Json
if ([string]$config.__VERSION__ -ne '272') {
    throw 'The base config is not a Fan Control V272 config.'
}
$controls = @($config.FanControl.Controls)
if ($controls.Count -ne 2 -or
        $controls[0].Identifier -ne $cpuControlId -or
        $controls[1].Identifier -ne $systemControlId) {
    throw 'The base config does not contain exactly the expected controls in order.'
}
if ($controls[0].PairedFanSensor.Identifier -ne $cpuRpmId -or
        $controls[1].PairedFanSensor.Identifier -ne $systemRpmId) {
    throw 'The base config does not pair each control with its expected tachometer.'
}
if (@($config.FanControl.FanSensors).Count -ne 2 -or
        $config.FanControl.FanSensors[0].Identifier -ne $cpuRpmId -or
        $config.FanControl.FanSensors[1].Identifier -ne $systemRpmId) {
    throw 'The base config does not expose exactly the expected fan sensors.'
}
$lhm = $config.Sensors.LibreHardwareMonitorSettings
if ($lhm.Controller -ne $false -or $lhm.EmbeddedEC -ne $false -or
        $lhm.Motherboard -ne $false) {
    throw 'The base config enables conflicting LHM controller/EC/motherboard polling.'
}

$curve10 = New-FlatCpuCurve -CurveName 'A5 CPU Flat Native 10' -NativeCode 10
$curve18 = New-FlatCpuCurve -CurveName 'A5 CPU Flat Native 18' -NativeCode 18
$curve30 = New-FlatCpuCurve -CurveName 'A5 CPU Flat Native 30' -NativeCode 30
$config.FanControl.FanCurves = @($curve10, $curve18, $curve30)
$initialCurve = switch ($InitialNativeCode) {
    10 { $curve10 }
    18 { $curve18 }
    30 { $curve30 }
}

$cpuControl = $controls[0]
$cpuControl.Enable = $true
$cpuControl.ManualControl = $false
$cpuControl.ManualControlValue = 35
$cpuControl.SelectedFanCurve = [pscustomobject]@{ Name = $initialCurve.Name }
$cpuControl.SelectedCommandStepUp = 100
$cpuControl.SelectedCommandStepDown = 100
$cpuControl.ForceApply = $false

$systemControl = $controls[1]
$systemControl.Enable = $true
$systemControl.ManualControl = $true
$systemControl.ManualControlValue = 100
$systemControl.SelectedFanCurve = $null
$systemControl.SelectedCommandStepUp = 100
$systemControl.SelectedCommandStepDown = 100
$systemControl.ForceApply = $false

$fileName = "$Name.json"
$evidencePath = Join-Path $resolvedEvidenceDirectory $fileName
$deployedPath = Join-Path $resolvedConfigDirectory $fileName
foreach ($path in @($evidencePath, $deployedPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to overwrite existing A5 config: $path"
    }
}
[IO.Directory]::CreateDirectory($resolvedEvidenceDirectory) | Out-Null
[IO.Directory]::CreateDirectory($resolvedConfigDirectory) | Out-Null
$json = $config | ConvertTo-Json -Depth 20
$nonce = [Guid]::NewGuid().ToString('N')
$evidenceTempPath = "$evidencePath.$nonce.tmp"
$deployedTempPath = "$deployedPath.$nonce.tmp"
$evidenceCommitted = $false
$deployedCommitted = $false
try {
    [IO.File]::WriteAllText($evidenceTempPath, $json, $utf8NoBom)
    [IO.File]::WriteAllText($deployedTempPath, $json, $utf8NoBom)
    if ((Get-FileHash -LiteralPath $evidenceTempPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $deployedTempPath -Algorithm SHA256).Hash) {
        throw 'The staged pristine and deployed A5 configs differ.'
    }
    [IO.File]::Move($evidenceTempPath, $evidencePath)
    $evidenceCommitted = $true
    [IO.File]::Move($deployedTempPath, $deployedPath)
    $deployedCommitted = $true
}
catch {
    foreach ($temporaryPath in @($evidenceTempPath, $deployedTempPath)) {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
    if ($deployedCommitted -and [IO.File]::Exists($deployedPath)) {
        [IO.File]::Delete($deployedPath)
    }
    if ($evidenceCommitted -and [IO.File]::Exists($evidencePath)) {
        [IO.File]::Delete($evidencePath)
    }
    throw
}

$evidenceHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
$deployedHash = (Get-FileHash -LiteralPath $deployedPath -Algorithm SHA256).Hash
if ($evidenceHash -ne $deployedHash) {
    throw 'The pristine and deployed A5 configs differ immediately after creation.'
}

[pscustomobject]@{
    Name = $Name
    Curves = @($curve10.Name, $curve18.Name, $curve30.Name)
    InitialNativeCode = $InitialNativeCode
    InitialCurve = $initialCurve.Name
    SystemManualPercent = 100
    EvidencePath = $evidencePath
    DeployedPath = $deployedPath
    Sha256 = $deployedHash
}
