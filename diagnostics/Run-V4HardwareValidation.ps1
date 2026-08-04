[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath,

    [ValidateSet('responsive', 'zero-load')]
    [string] $Mode = 'responsive'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$probePath = Join-Path `
    $PSScriptRoot `
    'bin\Release\net10.0-windows\FanControl.MinisforumUM780XTX.Diagnostics.exe'
$validationStart = Get-Date
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$baselineDumps = @{}

function Write-ValidationLog {
    param([Parameter(Mandatory = $true)][string] $Message)

    $line = "$(Get-Date -Format o) $Message"
    Write-Output $line
    [System.IO.File]::AppendAllText(
        $LogPath,
        $line + [Environment]::NewLine,
        $utf8NoBom)
}

function Get-LiveKernelDumpKeys {
    $keys = @{}
    Get-ChildItem -LiteralPath 'C:\Windows\LiveKernelReports' -Recurse -File `
        -ErrorAction SilentlyContinue | ForEach-Object {
            $key = "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
            $keys[$key] = $_
        }
    $keys
}

function Get-RelevantEvents {
    $systemEvents = Get-WinEvent -FilterHashtable @{
        LogName = 'System'
        StartTime = $validationStart
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.ProviderName -match 'WHEA-Logger|Display|amdwddmg|amdkmdag|BugCheck' -or
        ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -eq 41)
    }

    $applicationEvents = Get-WinEvent -FilterHashtable @{
        LogName = 'Application'
        StartTime = $validationStart
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.ProviderName -eq 'Windows Error Reporting' -and
        $_.Id -eq 1001 -and
        $_.Message -match 'LiveKernelEvent|BlueScreen|hardware error'
    }

    @($systemEvents) + @($applicationEvents)
}

function Invoke-Probe {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    Write-ValidationLog ("RUN " + ($Arguments -join ' '))
    & $probePath @Arguments 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Output $line
        [System.IO.File]::AppendAllText(
            $LogPath,
            $line + [Environment]::NewLine,
            $utf8NoBom)
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Probe failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Assert-NoNewFailureEvidence {
    param([Parameter(Mandatory = $true)][string] $Stage)

    Start-Sleep -Seconds 2
    $events = @(Get-RelevantEvents)
    if ($events.Count -ne 0) {
        $formatted = $events |
            Select-Object TimeCreated, Id, ProviderName, RecordId, Message |
            Format-List | Out-String
        Write-ValidationLog $formatted
        throw "Relevant Windows event appeared after $Stage."
    }

    $currentDumps = Get-LiveKernelDumpKeys
    $newDumps = @($currentDumps.Keys | Where-Object {
        -not $baselineDumps.ContainsKey($_)
    })
    if ($newDumps.Count -ne 0) {
        $formatted = $newDumps | ForEach-Object { $currentDumps[$_] } |
            Select-Object FullName, Length, LastWriteTime |
            Format-List | Out-String
        Write-ValidationLog $formatted
        throw "A new LiveKernelReport appeared after $Stage."
    }

    Write-ValidationLog "EVENT_AUDIT_PASS $Stage"
}

if (-not [System.IO.File]::Exists($probePath)) {
    throw "Release diagnostics executable is missing: $probePath"
}

$logDirectory = [System.IO.Path]::GetDirectoryName(
    [System.IO.Path]::GetFullPath($LogPath))
[System.IO.Directory]::CreateDirectory($logDirectory) | Out-Null
[System.IO.File]::WriteAllText($LogPath, '', $utf8NoBom)

$conflicts = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -like '*FanControl*' -or
    $_.ProcessName -like '*HWiNFO*' -or
    $_.ProcessName -like '*AIDA*' -or
    $_.ProcessName -like '*HardwareMonitor*'
})
if ($conflicts.Count -ne 0) {
    throw "Conflicting monitor process: $($conflicts.ProcessName -join ', ')"
}

$baselineDumps = Get-LiveKernelDumpKeys
$passed = $false
try {
    Write-ValidationLog "VALIDATION_START mode=$Mode"
    Invoke-Probe -Arguments @('stock')
    Assert-NoNewFailureEvidence -Stage 'initial-stock'

    switch ($Mode) {
        'responsive' {
            Invoke-Probe -Arguments @('plugin-cpu-step')
            Invoke-Probe -Arguments @('stock')
            Assert-NoNewFailureEvidence -Stage 'plugin-cpu-step-v4'

            Invoke-Probe -Arguments @('plugin-cpu-burst')
            Invoke-Probe -Arguments @('stock')
            Assert-NoNewFailureEvidence -Stage 'plugin-cpu-burst-v4'

            Invoke-Probe -Arguments @('cpu-stop-start')
            Invoke-Probe -Arguments @('stock')
            Assert-NoNewFailureEvidence -Stage 'cpu-stop-start-v4'
        }
        'zero-load' {
            Invoke-Probe -Arguments @('cpu-zero-load')
            Invoke-Probe -Arguments @('stock')
            Assert-NoNewFailureEvidence -Stage 'cpu-zero-load-v4'
        }
    }

    $passed = $true
    Write-ValidationLog 'VALIDATION_PASS'
}
catch {
    Write-ValidationLog "VALIDATION_FAIL $($_.Exception.Message)"
    throw
}
finally {
    try {
        Invoke-Probe -Arguments @('stock')
        Write-ValidationLog 'FINAL_STOCK_PASS'
    }
    catch {
        Write-ValidationLog "FINAL_STOCK_FAIL $($_.Exception.Message)"
        if ($passed) {
            throw
        }
    }
}
