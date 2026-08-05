[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LedgerPath,

    [Parameter(Mandatory = $true)]
    [string] $StopPath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $ParentProcessId,

    [Parameter(Mandatory = $true)]
    [long] $ParentStartTimeUtcTicks,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $SessionId
)

<#
.SYNOPSIS
Writes an independent, durable heartbeat for a Cinebench soak session.

.DESCRIPTION
This helper performs no fan, EC, Fan Control, or workload operations. It exits
when its exact parent identity disappears or when StopPath is created. The
separate ledger distinguishes a runner stall from a whole-machine stall.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$resolvedLedger = [IO.Path]::GetFullPath($LedgerPath)
$resolvedStop = [IO.Path]::GetFullPath($StopPath)

if ([IO.File]::Exists($resolvedLedger) -or
    [IO.Directory]::Exists($resolvedLedger)) {
    throw "Heartbeat ledger already exists: $resolvedLedger"
}
if ([IO.File]::Exists($resolvedStop) -or
    [IO.Directory]::Exists($resolvedStop)) {
    throw "Heartbeat stop path already exists: $resolvedStop"
}

$parent = [IO.Path]::GetDirectoryName($resolvedLedger)
if (-not $parent) {
    throw 'Heartbeat ledger must have a parent directory.'
}
[IO.Directory]::CreateDirectory($parent) | Out-Null

$stream = [IO.FileStream]::new(
    $resolvedLedger,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::Read,
    4096,
    [IO.FileOptions]::WriteThrough)
$writer = [IO.StreamWriter]::new($stream, $utf8NoBom, 4096, $true)

function Write-Heartbeat {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    $record = [ordered]@{
        Sequence = $script:sequence
        Kind = $Kind
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        SessionId = $SessionId
        ParentProcessId = $ParentProcessId
        Data = $Data
    }
    $script:sequence++
    $writer.WriteLine(($record | ConvertTo-Json -Depth 6 -Compress))
    $writer.Flush()
    $stream.Flush($true)
}

$script:sequence = 0
try {
    Write-Heartbeat -Kind 'heartbeat-start' -Data @{
        ParentStartTimeUtcTicks = $ParentStartTimeUtcTicks
    }

    while ($true) {
        if ([IO.File]::Exists($resolvedStop)) {
            Write-Heartbeat -Kind 'heartbeat-stopped'
            break
        }

        $process = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            Write-Heartbeat -Kind 'parent-exited'
            break
        }

        $actualTicks = try {
            $process.StartTime.ToUniversalTime().Ticks
        }
        catch {
            Write-Heartbeat -Kind 'parent-inspection-error' -Data @{
                Error = $_.Exception.Message
            }
            exit 1
        }
        if ($actualTicks -ne $ParentStartTimeUtcTicks) {
            Write-Heartbeat -Kind 'parent-identity-mismatch' -Data @{
                ActualStartTimeUtcTicks = $actualTicks
            }
            exit 2
        }

        Write-Heartbeat -Kind 'heartbeat'
        Start-Sleep -Seconds 1
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
