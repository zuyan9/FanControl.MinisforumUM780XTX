[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $DestinationPath,

    [ValidateRange(-1, 51)]
    [int] $CpuCode = 10,

    [ValidateRange(-1, 51)]
    [int] $SystemCode = -1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginName = 'Minisforum UM780 XTX (F7BSD)'
$acceptedCpuIds = @(
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4",
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-raw-v1"
)
$rawCpuId = "$pluginName/minisforum.um780xtx.f7bsd.cpu-raw-v1"
$systemId = "$pluginName/minisforum.um780xtx.f7bsd.system-raw-v2"
$rawCpuName = 'UM780 XTX CPU Fan Raw Target'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

$source = [System.IO.Path]::GetFullPath($SourcePath)
$destination = [System.IO.Path]::GetFullPath($DestinationPath)
if (-not [System.IO.File]::Exists($source)) {
    throw "Source configuration does not exist: $source"
}
if ([System.IO.File]::Exists($destination) -or
    [System.IO.Directory]::Exists($destination)) {
    throw "Destination already exists; refusing to overwrite it: $destination"
}
$parent = [System.IO.Path]::GetDirectoryName($destination)
if (-not $parent -or -not [System.IO.Directory]::Exists($parent)) {
    throw "Destination directory does not exist: $parent"
}

$config = [System.IO.File]::ReadAllText($source) | ConvertFrom-Json
$controls = @($config.FanControl.Controls)
$cpuMatches = @($controls | Where-Object {
    $acceptedCpuIds -contains [string]$_.Identifier
})
$systemMatches = @($controls | Where-Object {
    [string]$_.Identifier -eq $systemId
})
if ($controls.Count -ne 2 -or
    $cpuMatches.Count -ne 1 -or $systemMatches.Count -ne 1) {
    throw 'The source must contain exactly the expected UM780 CPU and system controls.'
}

function Set-RawControl {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Control,

        [Parameter(Mandatory = $true)]
        [int] $Code
    )

    $Control.Enable = $Code -ge 0
    $Control.ManualControl = $true
    $Control.SelectedFanCurve = $null
    $Control.MinimumPercent = 0
    $Control.SelectedStart = 0
    $Control.SelectedStop = 0
    $Control.SelectedCommandStepUp = 100
    $Control.SelectedCommandStepDown = 100
    $Control.ForceApply = $false
    if ($Code -ge 0) {
        $Control.ManualControlValue = [int][Math]::Round(
            [double]$Code * 100.0 / 51.0,
            [MidpointRounding]::AwayFromZero)
    }
}

$cpu = $cpuMatches[0]
$system = $systemMatches[0]
$cpu.Identifier = $rawCpuId
$cpu.NickName = $rawCpuName
Set-RawControl -Control $cpu -Code $CpuCode
Set-RawControl -Control $system -Code $SystemCode

foreach ($sensor in @($config.FanControl.FanSensors)) {
    if ([string]$sensor.Identifier -eq
        "$pluginName/minisforum.um780xtx.f7bsd.fan1") {
        $sensor.NickName = 'UM780 XTX CPU Fan'
    }
}

$json = $config | ConvertTo-Json -Depth 100
$stream = [System.IO.FileStream]::new(
    $destination,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::Read,
    4096,
    [System.IO.FileOptions]::WriteThrough)
try {
    $writer = [System.IO.StreamWriter]::new(
        $stream, $utf8NoBom, 4096, $true)
    try {
        $writer.Write($json)
        $writer.Write([Environment]::NewLine)
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

[pscustomobject]@{
    SourcePath = $source
    DestinationPath = $destination
    CpuControlId = $cpu.Identifier
    CpuCode = if ($CpuCode -ge 0) { $CpuCode } else { $null }
    CpuConfiguredPercent = if ($CpuCode -ge 0) {
        [int][Math]::Round(
            [double]$CpuCode * 100.0 / 51.0,
            [MidpointRounding]::AwayFromZero)
    } else {
        $null
    }
    CpuConfirmedPercent = if ($CpuCode -ge 0) {
        [double]$CpuCode * 100.0 / 51.0
    } else {
        $null
    }
    SystemCode = if ($SystemCode -ge 0) { $SystemCode } else { $null }
    SystemConfiguredPercent = if ($SystemCode -ge 0) {
        [int][Math]::Round(
            [double]$SystemCode * 100.0 / 51.0,
            [MidpointRounding]::AwayFromZero)
    } else {
        $null
    }
    SystemConfirmedPercent = if ($SystemCode -ge 0) {
        [double]$SystemCode * 100.0 / 51.0
    } else {
        $null
    }
    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
