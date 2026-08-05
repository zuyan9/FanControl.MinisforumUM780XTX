[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $WorkloadProcessId,

    [Parameter(Mandatory = $true)]
    [long] $WorkloadStartTimeUtcTicks,

    [Parameter(Mandatory = $true)]
    [datetime] $DeadlineUtc,

    [Parameter(Mandatory = $true)]
    [string] $LedgerPath,

    [Parameter(Mandatory = $true)]
    [string] $StopPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $PhaseId
)

<#
.SYNOPSIS
Enforces one Cinebench phase deadline from an independent process.

.DESCRIPTION
The watchdog kills only the exact PID/start-time identity supplied by the
campaign runner. It never accesses Fan Control or the EC. A stop file disarms
it after the runner has proved the workload exited.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$resolvedLedger = [IO.Path]::GetFullPath($LedgerPath)
$resolvedStop = [IO.Path]::GetFullPath($StopPath)
$deadline = [DateTimeOffset]::new($DeadlineUtc.ToUniversalTime())

if ([IO.File]::Exists($resolvedLedger) -or
    [IO.Directory]::Exists($resolvedLedger)) {
    throw "Watchdog ledger already exists: $resolvedLedger"
}
if ([IO.File]::Exists($resolvedStop) -or
    [IO.Directory]::Exists($resolvedStop)) {
    throw "Watchdog stop path already exists: $resolvedStop"
}
$parent = [IO.Path]::GetDirectoryName($resolvedLedger)
if (-not $parent) {
    throw 'Watchdog ledger must have a parent directory.'
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

function Write-WatchdogRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    $record = [ordered]@{
        Sequence = $script:sequence
        Kind = $Kind
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        PhaseId = $PhaseId
        WorkloadProcessId = $WorkloadProcessId
        Data = $Data
    }
    $script:sequence++
    $writer.WriteLine(($record | ConvertTo-Json -Depth 6 -Compress))
    $writer.Flush()
    $stream.Flush($true)
}

$script:sequence = 0
try {
    Write-WatchdogRecord -Kind 'watchdog-start' -Data @{
        WorkloadStartTimeUtcTicks = $WorkloadStartTimeUtcTicks
        DeadlineUtc = $deadline.ToString('o')
    }

    while ($true) {
        if ([IO.File]::Exists($resolvedStop)) {
            Write-WatchdogRecord -Kind 'watchdog-stopped'
            exit 0
        }

        $process = Get-Process -Id $WorkloadProcessId `
            -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            Write-WatchdogRecord -Kind 'workload-exited'
            exit 0
        }

        $actualTicks = try {
            $process.StartTime.ToUniversalTime().Ticks
        }
        catch {
            Write-WatchdogRecord -Kind 'watchdog-inspection-error' -Data @{
                Error = $_.Exception.Message
            }
            exit 1
        }
        if ($actualTicks -ne $WorkloadStartTimeUtcTicks) {
            Write-WatchdogRecord -Kind 'workload-identity-mismatch' -Data @{
                ActualStartTimeUtcTicks = $actualTicks
            }
            exit 2
        }

        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $killErrors = [Collections.Generic.List[string]]::new()
            for ($attempt = 1; $attempt -le 2; $attempt++) {
                try {
                    $process.Refresh()
                    if ($process.HasExited) {
                        break
                    }
                    $actualTicks = $process.StartTime.ToUniversalTime().Ticks
                    if ($actualTicks -ne $WorkloadStartTimeUtcTicks) {
                        Write-WatchdogRecord `
                            -Kind 'workload-identity-mismatch' -Data @{
                                ActualStartTimeUtcTicks = $actualTicks
                                DuringDeadlineEnforcement = $true
                            }
                        exit 2
                    }
                    $process.Kill()
                    [void]$process.WaitForExit(3000)
                }
                catch {
                    $message = $_.Exception.Message
                    $killErrors.Add($message)
                    Write-WatchdogRecord `
                        -Kind 'deadline-kill-attempt-failed' -Data @{
                            Attempt = $attempt
                            Error = $message
                        }
                }
            }
            $exited = $false
            try {
                $process.Refresh()
                $exited = $process.HasExited
            }
            catch {
                $killErrors.Add($_.Exception.Message)
            }
            if (-not $exited) {
                Write-WatchdogRecord -Kind 'deadline-kill-failed' -Data @{
                    Attempts = 2
                    Errors = $killErrors.ToArray()
                }
                exit 4
            }
            Write-WatchdogRecord -Kind 'deadline-enforced' -Data @{
                Attempts = if ($killErrors.Count -eq 0) { 1 } else { 2 }
                PriorAttemptErrors = $killErrors.ToArray()
            }
            exit 3
        }

        Start-Sleep -Milliseconds 500
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
