[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string] $CpuControlId =
        'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-cool-stop-v4'
)

$ErrorActionPreference = 'Stop'
$ids = [ordered]@{
    CpuRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan1'
    SystemRpm = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.fan2'
    CpuTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.cpu-temperature'
    SystemTemperature = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-temperature'
    CpuControl = $CpuControlId
    SystemControl = 'Minisforum UM780 XTX (F7BSD)/minisforum.um780xtx.f7bsd.system-raw-v2'
}

$samples = @(Get-Content -LiteralPath $Path | ForEach-Object {
    $_ | ConvertFrom-Json
})
if ($samples.Count -eq 0) {
    throw "No samples found in $Path."
}

function Get-Values {
    param([string] $Identifier)

    @($samples | ForEach-Object {
        $_.Values.PSObject.Properties[$Identifier].Value
    } | Where-Object { $null -ne $_ })
}

function Get-Range {
    param([object[]] $Values)

    if ($Values.Count -eq 0) {
        return $null
    }

    $measure = $Values | Measure-Object -Minimum -Maximum -Average
    [ordered]@{
        Minimum = $measure.Minimum
        Maximum = $measure.Maximum
        Average = $measure.Average
    }
}

$rpc = @($samples.RpcMilliseconds | Sort-Object)
$intervals = for ($index = 1; $index -lt $samples.Count; $index++) {
    [double]$samples[$index].MonotonicMilliseconds -
        [double]$samples[$index - 1].MonotonicMilliseconds
}
$p95Index = [Math]::Min(
    $rpc.Count - 1,
    [Math]::Floor(($rpc.Count - 1) * 0.95))

[pscustomobject]@{
    Path = (Resolve-Path $Path).Path
    Count = $samples.Count
    FirstUtc = $samples[0].Utc
    LastUtc = $samples[-1].Utc
    DurationSeconds =
        ([double]$samples[-1].MonotonicMilliseconds -
            [double]$samples[0].MonotonicMilliseconds) / 1000.0
    RpcMilliseconds = [ordered]@{
        Average = ($rpc | Measure-Object -Average).Average
        P95 = $rpc[$p95Index]
        Maximum = ($rpc | Measure-Object -Maximum).Maximum
    }
    ErrorSamples = @($samples | Where-Object { $_.Error }).Count
    MissingValueSamples = [ordered]@{
        CpuRpm = $samples.Count - (Get-Values $ids.CpuRpm).Count
        SystemRpm = $samples.Count - (Get-Values $ids.SystemRpm).Count
        CpuTemperature =
            $samples.Count - (Get-Values $ids.CpuTemperature).Count
        SystemTemperature =
            $samples.Count - (Get-Values $ids.SystemTemperature).Count
        CpuControl = $samples.Count - (Get-Values $ids.CpuControl).Count
        SystemControl = $samples.Count -
            (Get-Values $ids.SystemControl).Count
    }
    MaximumSampleIntervalMilliseconds = if ($intervals.Count) {
        ($intervals | Measure-Object -Maximum).Maximum
    } else {
        $null
    }
    CpuRpm = Get-Range (Get-Values $ids.CpuRpm)
    SystemRpm = Get-Range (Get-Values $ids.SystemRpm)
    CpuTemperatureC = Get-Range (Get-Values $ids.CpuTemperature)
    SystemTemperatureC = Get-Range (Get-Values $ids.SystemTemperature)
    CpuControlValues = @(
        Get-Values $ids.CpuControl | Sort-Object -Unique)
    SystemControlValues = @(
        Get-Values $ids.SystemControl | Sort-Object -Unique)
}
