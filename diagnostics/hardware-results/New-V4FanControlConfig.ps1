<#
.SYNOPSIS
Creates a v4-compatible Fan Control configuration without changing the source.

.DESCRIPTION
Migrates only the UM780 CPU control contract from v3 to v4, sets explicit Fan
Control command-step limits, and optionally disables both controls for a safe
campaign start. The destination must not already exist.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $DestinationPath,

    [ValidateRange(1, 100)]
    [int] $CommandStep = 100,

    [switch] $PreserveEnabledState
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginName = 'Minisforum UM780 XTX (F7BSD)'
$v3CpuId = "$pluginName/minisforum.um780xtx.f7bsd.cpu-native-v3"
$v4CpuId = "$pluginName/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4"
$systemId = "$pluginName/minisforum.um780xtx.f7bsd.system-raw-v2"
$v4CpuName = 'UM780 XTX CPU Fan Target (Cool-Stop Thermal Tail)'
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
    $_.Identifier -eq $v3CpuId -or $_.Identifier -eq $v4CpuId
})
$systemMatches = @($controls | Where-Object { $_.Identifier -eq $systemId })
if ($cpuMatches.Count -ne 1 -or $systemMatches.Count -ne 1) {
    throw 'The source must contain exactly one expected UM780 CPU and system control.'
}

$cpu = $cpuMatches[0]
$system = $systemMatches[0]
$cpu.Identifier = $v4CpuId
$cpu.NickName = $v4CpuName
foreach ($control in @($cpu, $system)) {
    $control.MinimumPercent = 0
    $control.SelectedCommandStepUp = $CommandStep
    $control.SelectedCommandStepDown = $CommandStep
    $control.ForceApply = $false
    if (-not $PreserveEnabledState) {
        $control.Enable = $false
    }
}

$json = $config | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText(
    $destination,
    $json + [Environment]::NewLine,
    $utf8NoBom)

[pscustomobject]@{
    SourcePath = $source
    DestinationPath = $destination
    CpuControlId = $cpu.Identifier
    CpuEnabled = [bool]$cpu.Enable
    SystemEnabled = [bool]$system.Enable
    CommandStepUp = $CommandStep
    CommandStepDown = $CommandStep
    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
