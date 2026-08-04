[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $IpcExecutable,

    [Parameter(Mandatory = $true)]
    [string] $IpcAssembly,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [ValidateRange(1, 100)]
    [int] $Count = 10,

    [ValidateRange(1, 30)]
    [int] $IntervalSeconds = 4
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$ledgerPath = Join-Path $OutputDirectory 'refresh-ledger.jsonl'
$writer = [System.IO.StreamWriter]::new($ledgerPath, $false)
$writer.AutoFlush = $true
try {
    for ($index = 1; $index -le $Count; $index++) {
        $replyPath = Join-Path $OutputDirectory "refresh-$index.json"
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        & $IpcExecutable $IpcAssembly refresh --output $replyPath
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()
        $reply = if (Test-Path -LiteralPath $replyPath) {
            [System.IO.File]::ReadAllText($replyPath)
        } else {
            $null
        }
        $writer.WriteLine((([ordered]@{
            Sequence = $index
            Utc = [DateTimeOffset]::UtcNow.ToString('o')
            DurationMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
            ExitCode = $exitCode
            Reply = $reply
        }) | ConvertTo-Json -Compress))
        if ($exitCode -ne 0 -or $reply -notmatch '"status":\s*"OK"') {
            throw "Refresh $index failed: exit $exitCode; $reply"
        }
        if ($index -lt $Count) {
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
}
finally {
    $writer.Dispose()
}

Write-Output $ledgerPath
