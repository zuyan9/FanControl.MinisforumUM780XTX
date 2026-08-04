[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseConfig,

    [Parameter(Mandatory = $true)]
    [string] $Name,

    [ValidateRange(0, 51)]
    [Nullable[int]] $CpuCode,

    [ValidateRange(0, 51)]
    [Nullable[int]] $SystemCode,

    [Parameter(Mandatory = $true)]
    [string] $EvidenceDirectory,

    [switch] $SystemFirst,

    [Parameter(Mandatory = $true)]
    [string] $FanControlConfigDirectory
)

$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Name may contain only letters, numbers, dot, underscore, and dash.'
}

$config = [System.IO.File]::ReadAllText($BaseConfig) | ConvertFrom-Json
$controls = @($config.FanControl.Controls)
if ($controls.Count -ne 2 -or
    $controls[0].Identifier -notmatch 'cpu-native-v3$' -or
    $controls[1].Identifier -notmatch 'system-raw-v2$') {
    throw 'The base config does not contain the exact two expected controls.'
}

function Set-Control {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Control,

        [Nullable[int]] $Code
    )

    $Control.Enable = $null -ne $Code
    $Control.ManualControl = $true
    $Control.SelectedFanCurve = $null
    $Control.ForceApply = $false
    if ($null -ne $Code) {
        $Control.ManualControlValue = [int][Math]::Round(
            [double]$Code * 100.0 / 51.0,
            [MidpointRounding]::AwayFromZero)
    }
}

Set-Control -Control $controls[0] -Code $CpuCode
Set-Control -Control $controls[1] -Code $SystemCode
if ($SystemFirst) {
    $config.FanControl.Controls = @($controls[1], $controls[0])
}

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
    CpuCode = $CpuCode
    SystemCode = $SystemCode
    SystemFirst = [bool]$SystemFirst
    EvidencePath = $evidencePath
    DeployedPath = $deployedPath
    Sha256 = (Get-FileHash -LiteralPath $deployedPath -Algorithm SHA256).Hash
}
