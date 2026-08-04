[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Label,

    [Parameter(Mandatory = $true)]
    [datetime] $CampaignStart,

    [Parameter(Mandatory = $true)]
    [string] $FanControlDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$capturedAt = Get-Date
$safeLabel = $Label -replace '[^A-Za-z0-9._-]', '_'
$prefix = "$(Get-Date -Date $capturedAt -Format 'yyyyMMdd-HHmmss')-$safeLabel"
$progressPath = Join-Path $OutputDirectory "$prefix-progress.log"

function Write-CaptureMarker {
    param([string] $Message)

    $line = "$(Get-Date -Format 'o') $Message"
    Add-Content -LiteralPath $progressPath -Value $line -Encoding UTF8
    Write-Verbose $Message
}

function Get-FilteredEvents {
    param([string] $LogName)

    $warningOrWorse = @(Get-WinEvent -FilterHashtable @{
        LogName = $LogName
        StartTime = $CampaignStart
        Level = 1, 2, 3
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.ProviderName -match
            'WHEA-Logger|Display|amdwddmg|amdkmdag|BugCheck|Kernel-Power|' +
            'Application Error|Application Hang|\.NET Runtime'
    })
    $wer = if ($LogName -eq 'Application') {
        @(Get-WinEvent -FilterHashtable @{
            LogName = $LogName
            ProviderName = 'Windows Error Reporting'
            StartTime = $CampaignStart
        } -ErrorAction SilentlyContinue | Where-Object {
            $_.Message -match
                'LiveKernelEvent|BlueScreen|hardware error|FanControl|' +
                'CpuBurn|PawnIo'
        })
    } else {
        @()
    }

    @($warningOrWorse + $wer | Sort-Object RecordId -Unique |
        Select-Object TimeCreated, LogName, Id, LevelDisplayName,
            ProviderName, RecordId, Message)
}

function Get-LiveKernelReports {
    param([string] $Root)

    try {
        @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction Stop |
            Select-Object FullName, Length, CreationTimeUtc, LastWriteTimeUtc)
    }
    catch {
        @([ordered]@{
            AccessError = $_.Exception.Message
            Root = $Root
        })
    }
}

$pluginPath = Join-Path $FanControlDirectory `
    'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll'
$cachePath = Join-Path $FanControlDirectory 'Configurations\CACHE'
$cache = if (Test-Path -LiteralPath $cachePath) {
    Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json
} else {
    $null
}
$configFileName = if ($cache) {
    $cache.CurrentConfigFileName
} else {
    'userConfig.json'
}
$configPath = Join-Path $FanControlDirectory `
    "Configurations\$configFileName"
$logPath = Join-Path $FanControlDirectory 'log.txt'
$dumpRoot = 'C:\Windows\LiveKernelReports'
$localDumpRoot = Join-Path $env:LOCALAPPDATA 'CrashDumps'
$pawnIoDriver = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
    Where-Object Name -eq 'PawnIO' |
    Select-Object -First 1
$componentPaths = @(
    (Join-Path $FanControlDirectory 'FanControl.exe'),
    (Join-Path $FanControlDirectory 'FanControl.IPC.dll'),
    (Join-Path $FanControlDirectory 'FanControl.Plugins.dll'),
    (Join-Path $FanControlDirectory 'LibreHardwareMonitorLib.dll'),
    'C:\Program Files\PawnIO\PawnIOLib.dll',
    ($pawnIoDriver.PathName -replace '^\\SystemRoot', $env:SystemRoot)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$snapshot = [ordered]@{
    Label = $Label
    CampaignStart = $CampaignStart.ToString('o')
    CapturedAt = $capturedAt.ToString('o')
    OperatingSystem = $(Write-CaptureMarker 'Capturing operating system';
        Get-CimInstance Win32_OperatingSystem |
        Select-Object LastBootUpTime, LocalDateTime, FreePhysicalMemory,
            TotalVisibleMemorySize)
    ComputerSystem = $(Write-CaptureMarker 'Capturing computer system';
        Get-CimInstance Win32_ComputerSystem |
        Select-Object Manufacturer, Model, TotalPhysicalMemory)
    Processes = @(Write-CaptureMarker 'Capturing processes';
        Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match
            'FanControl|CpuBurn|HWiNFO|AIDA|HardwareMonitor|OCCT|Prime|CoreTemp'
    } | Select-Object ProcessName, Id, StartTime, CPU, Path, Responding,
        WorkingSet64, PrivateMemorySize64, HandleCount,
        @{ Name = 'ThreadCount'; Expression = { $_.Threads.Count } })
    Components = @(Write-CaptureMarker 'Hashing components';
        $componentPaths | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{
            Path = $item.FullName
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
            Version = $item.VersionInfo.FileVersion
        }
    })
    Plugin = $(Write-CaptureMarker 'Hashing plugin';
        if (Test-Path -LiteralPath $pluginPath) {
        $item = Get-Item -LiteralPath $pluginPath
        [ordered]@{
            Path = $item.FullName
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            Sha256 = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
            Version = $item.VersionInfo.FileVersion
        }
    }
    else {
        $null
    })
    Config = $(Write-CaptureMarker 'Capturing config';
        if (Test-Path -LiteralPath $configPath) {
        $item = Get-Item -LiteralPath $configPath
        [ordered]@{
            Path = $item.FullName
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            Sha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        }
    }
    else {
        $null
    })
    FanControlLog = $(Write-CaptureMarker 'Hashing Fan Control log';
        if (Test-Path -LiteralPath $logPath) {
        $item = Get-Item -LiteralPath $logPath
        [ordered]@{
            Path = $item.FullName
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
            Sha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash
        }
    }
    else {
        $null
    })
    LiveKernelReports = $(Write-CaptureMarker 'Listing LiveKernelReports';
        Get-LiveKernelReports -Root $dumpRoot)
    LocalCrashDumps = $(Write-CaptureMarker 'Listing local crash dumps';
        Get-LiveKernelReports -Root $localDumpRoot)
    PawnIoDriver = $(Write-CaptureMarker 'Capturing PawnIO driver';
        $pawnIoDriver | Select-Object Name, State, StartMode, PathName)
    SystemEvents = $(Write-CaptureMarker 'Capturing System events';
        Get-FilteredEvents -LogName 'System')
    ApplicationEvents = $(Write-CaptureMarker 'Capturing Application events';
        Get-FilteredEvents -LogName 'Application')
}

Write-CaptureMarker 'Serializing evidence JSON'
$snapshot | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "$prefix.json") `
        -Encoding UTF8

if (Test-Path -LiteralPath $logPath) {
    Write-CaptureMarker 'Copying Fan Control log tail'
    Get-Content -LiteralPath $logPath -Tail 1000 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory "$prefix-fancontrol.log") `
            -Encoding UTF8
}

if (Test-Path -LiteralPath $configPath) {
    Write-CaptureMarker 'Copying active config'
    Copy-Item -LiteralPath $configPath -Destination (
        Join-Path $OutputDirectory "$prefix-config.json") -Force
}

Write-Output (Join-Path $OutputDirectory "$prefix.json")
