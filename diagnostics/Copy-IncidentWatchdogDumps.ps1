[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot `
        'hardware-results\2026-08-03-freeze-forensics\watchdog-dumps'
}
$statusDirectory = Join-Path $PSScriptRoot `
    'hardware-results\2026-08-03-freeze-forensics'
[IO.Directory]::CreateDirectory($statusDirectory) | Out-Null
$statusPath = Join-Path $statusDirectory 'dump-copy-status.log'
[IO.File]::WriteAllText(
    $statusPath,
    "$(Get-Date -Format o) START pid=$PID`r`n",
    [Text.UTF8Encoding]::new($false))
trap {
    [IO.File]::AppendAllText(
        $statusPath,
        "$(Get-Date -Format o) ERROR $($_.Exception.Message)`r`n",
        [Text.UTF8Encoding]::new($false))
    exit 1
}

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this read-only collector from an elevated PowerShell session.'
}

$sources = @(
    'C:\Windows\LiveKernelReports\WATCHDOG-20260803-1234.dmp',
    'C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-20260803-1759.dmp',
    'C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-20260803-1904.dmp'
)

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'hardware-results'))
if (-not $resolvedOutput.StartsWith(
        $allowedRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain below $allowedRoot"
}

[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$inventory = foreach ($source in $sources) {
    if (-not [IO.File]::Exists($source)) {
        throw "Expected incident dump is missing: $source"
    }

    $destination = Join-Path $resolvedOutput ([IO.Path]::GetFileName($source))
    [IO.File]::AppendAllText(
        $statusPath,
        "$(Get-Date -Format o) COPY $source`r`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Copy($source, $destination, $true)
    $item = Get-Item -LiteralPath $destination
    $hash = Get-FileHash -LiteralPath $destination -Algorithm SHA256
    [ordered]@{
        Source = $source
        Destination = $destination
        Length = $item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
        Sha256 = $hash.Hash
    }
}

$manifestPath = Join-Path $resolvedOutput 'manifest.json'
$json = $inventory | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))

$stream = [IO.File]::Open(
    $manifestPath,
    [IO.FileMode]::Open,
    [IO.FileAccess]::Read,
    [IO.FileShare]::Read)
try {
    $stream.Flush($true)
} finally {
    $stream.Dispose()
}

$inventory | Format-Table -AutoSize
Write-Host "Manifest: $manifestPath"
[IO.File]::AppendAllText(
    $statusPath,
    "$(Get-Date -Format o) COMPLETE manifest=$manifestPath`r`n",
    [Text.UTF8Encoding]::new($false))
