[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseConfig,

    [Parameter(Mandatory = $true)]
    [string] $CurveSourceConfig,

    [Parameter(Mandatory = $true)]
    [string] $Name,

    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string] $FanControlConfigDirectory,

    [string] $CpuControlId =
        'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4'
)

$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Name may contain only letters, numbers, dot, underscore, and dash.'
}

$config = [System.IO.File]::ReadAllText($BaseConfig) | ConvertFrom-Json
$curveSource = [System.IO.File]::ReadAllText($CurveSourceConfig) |
    ConvertFrom-Json
$controls = @($config.FanControl.Controls)
if ($controls.Count -ne 2 -or
    $controls[0].Identifier -ne $CpuControlId -or
    $controls[1].Identifier -notmatch 'system-raw-v2$') {
    throw 'The base config does not contain the exact two expected controls.'
}

$cpuCurve = @($curveSource.FanControl.FanCurves |
    Where-Object Name -eq 'CPU')
$systemCurve = @($curveSource.FanControl.FanCurves |
    Where-Object Name -eq 'System')
if ($cpuCurve.Count -ne 1 -or $systemCurve.Count -ne 1) {
    throw 'The curve source must contain exactly one CPU and one System curve.'
}
if ($cpuCurve[0].SelectedTempSource.Identifier -notmatch
        'cpu-temperature$' -or
    $systemCurve[0].SelectedTempSource.Identifier -notmatch
        'system-temperature$') {
    throw 'The curve source does not use the exact expected temperatures.'
}

$cpuCurve = $cpuCurve[0] | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$systemCurve = $systemCurve[0] | ConvertTo-Json -Depth 20 |
    ConvertFrom-Json
$cpuCurve.Name = 'CPU Cool-Stop Curve v4'
$systemCurve.Name = 'System Raw Curve v2'
$config.FanControl.FanCurves = @($cpuCurve, $systemCurve)

$controls[0].Enable = $true
$controls[0].ManualControl = $false
$controls[0].SelectedFanCurve = [pscustomobject]@{ Name = $cpuCurve.Name }
$controls[0].ForceApply = $false
$controls[1].Enable = $true
$controls[1].ManualControl = $false
$controls[1].SelectedFanCurve = [pscustomobject]@{ Name = $systemCurve.Name }
$controls[1].ForceApply = $false

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $FanControlConfigDirectory -Force |
    Out-Null
$fileName = "$Name.json"
$evidencePath = Join-Path $EvidenceDirectory $fileName
$deployedPath = Join-Path $FanControlConfigDirectory $fileName
$json = $config | ConvertTo-Json -Depth 20
$json | Set-Content -LiteralPath $evidencePath -Encoding UTF8
$json | Set-Content -LiteralPath $deployedPath -Encoding UTF8

[pscustomobject]@{
    Name = $Name
    Curves = @($cpuCurve.Name, $systemCurve.Name)
    EvidencePath = $evidencePath
    DeployedPath = $deployedPath
    Sha256 = (Get-FileHash -LiteralPath $deployedPath -Algorithm SHA256).Hash
}
