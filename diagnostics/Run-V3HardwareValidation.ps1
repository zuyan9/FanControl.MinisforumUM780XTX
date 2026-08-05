[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath,

    [ValidateSet('static', 'extended')]
    [string] $Mode = 'static'
)

$ErrorActionPreference = 'Stop'
$probePath = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\FanControl.MinisforumUM780XTX.Diagnostics.exe'
$validationStart = Get-Date

function Write-ValidationLog {
    param([string] $Message)

    $line = "$(Get-Date -Format o) $Message"
    Write-Output $line
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
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

function Get-LiveKernelDumpKeys {
    $keys = @{}
    $root = Join-Path $env:SystemRoot 'LiveKernelReports'
    Get-ChildItem -LiteralPath $root -Recurse -File `
        -ErrorAction SilentlyContinue | ForEach-Object {
            $key = "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
            $keys[$key] = $_
        }
    $keys
}

function Invoke-Probe {
    param([string[]] $Arguments)

    Write-ValidationLog ("RUN " + ($Arguments -join ' '))
    & $probePath @Arguments 2>&1 | ForEach-Object {
        Write-Output $_
        Add-Content -LiteralPath $LogPath -Value $_ -Encoding UTF8
    }
    $probeExitCode = $LASTEXITCODE
    if ($probeExitCode -ne 0) {
        throw "Probe failed with exit code ${probeExitCode}: $($Arguments -join ' ')"
    }
}

function Assert-NoNewFailureEvidence {
    param(
        [hashtable] $BaselineDumps,
        [string] $Stage
    )

    Start-Sleep -Seconds 2
    $events = @(Get-RelevantEvents)
    if ($events.Count -ne 0) {
        $events | Select-Object TimeCreated, Id, ProviderName, RecordId, Message |
            Format-List | Out-String | ForEach-Object {
                Write-Output $_
                Add-Content -LiteralPath $LogPath -Value $_ -Encoding UTF8
            }
        throw "Relevant Windows event appeared after $Stage."
    }

    $currentDumps = Get-LiveKernelDumpKeys
    $newDumps = @($currentDumps.Keys | Where-Object {
        -not $BaselineDumps.ContainsKey($_)
    })
    if ($newDumps.Count -ne 0) {
        $newDumps | ForEach-Object { $currentDumps[$_] } |
            Select-Object FullName, Length, LastWriteTime |
            Format-List | Out-String | ForEach-Object {
                Write-Output $_
                Add-Content -LiteralPath $LogPath -Value $_ -Encoding UTF8
            }
        throw "A new LiveKernelReport appeared after $Stage."
    }

    Write-ValidationLog "EVENT_AUDIT_PASS $Stage"
}

try {
    Set-Content -LiteralPath $LogPath -Value '' -Encoding UTF8
    Write-ValidationLog "VALIDATION_START $($validationStart.ToString('o'))"

    $conflicts = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like '*FanControl*' -or
        $_.ProcessName -like '*HWiNFO*' -or
        $_.ProcessName -like '*AIDA*' -or
        $_.ProcessName -like '*HardwareMonitor*'
    })
    if ($conflicts.Count -ne 0) {
        throw "Conflicting hardware-monitor process is running: $($conflicts.ProcessName -join ', ')"
    }

    $baselineDumps = Get-LiveKernelDumpKeys
    Invoke-Probe -Arguments @('stock')
    Assert-NoNewFailureEvidence -BaselineDumps $baselineDumps -Stage 'initial-stock'

    if ($Mode -eq 'static') {
        $stages = @(
            @('18', '15'),
            @('16', '15'),
            @('14', '20'),
            @('12', '20'),
            @('10', '30')
        )
        foreach ($stage in $stages) {
            $code = $stage[0]
            $seconds = $stage[1]
            Invoke-Probe -Arguments @('cpu', $code, $seconds)
            Invoke-Probe -Arguments @('stock')
            Assert-NoNewFailureEvidence -BaselineDumps $baselineDumps -Stage "cpu-$code"
        }
    }
    else {
        Invoke-Probe -Arguments @('cpu-step')
        Invoke-Probe -Arguments @('stock')
        Assert-NoNewFailureEvidence -BaselineDumps $baselineDumps -Stage 'cpu-step-low-v3'

        Invoke-Probe -Arguments @('plugin-cpu-step')
        Invoke-Probe -Arguments @('stock')
        Assert-NoNewFailureEvidence -BaselineDumps $baselineDumps -Stage 'plugin-cpu-step-v3'

        Invoke-Probe -Arguments @('cpu-soak')
        Invoke-Probe -Arguments @('stock')
        Assert-NoNewFailureEvidence -BaselineDumps $baselineDumps -Stage 'cpu-soak-10-120'
    }

    Write-ValidationLog 'VALIDATION_PASS'
    exit 0
}
catch {
    Write-ValidationLog "VALIDATION_FAIL $($_.Exception.Message)"
    exit 1
}
