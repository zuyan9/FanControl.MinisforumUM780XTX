[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CampaignDirectory,

    [string] $OutputDirectory,

    [ValidateRange(1048576, 107374182400)]
    [long] $MaximumCopyBytes = 2147483648,

    [switch] $CopyMemoryDump
)

<#
.SYNOPSIS
Collects post-reboot evidence for an interrupted Cinebench soak campaign.

.DESCRIPTION
This is a collector only. It does not start Fan Control, load a configuration,
resume Cinebench, access the EC, or alter dump settings. New/changed live
kernel reports, minidumps, local process dumps, relevant WER files, Windows
events, Fan Control state files, and Redshift logs are copied into a new
evidence directory and hashed.

MEMORY.DMP is inventoried but is copied only with -CopyMemoryDump and only when
it is no larger than MaximumCopyBytes. The campaign must be resumed manually
with the runner's explicit acknowledgement switch after the evidence is
reviewed.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this collector from an elevated PowerShell session.'
    }
}

function Read-JsonLines {
    param([Parameter(Mandatory = $true)][string] $Path)

    $lines = @(Get-Content -LiteralPath $Path -ErrorAction Stop)
    $lastNonempty = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if (-not [string]::IsNullOrWhiteSpace([string]$lines[$index])) {
            $lastNonempty = $index
        }
    }
    $records = [Collections.Generic.List[object]]::new()
    $torn = $null
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = [string]$lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        try {
            $records.Add(($line | ConvertFrom-Json -ErrorAction Stop))
        }
        catch {
            if ($index -ne $lastNonempty -or $null -ne $torn) {
                throw (
                    "Interior JSON corruption in $Path at line $($index + 1): " +
                    $_.Exception.Message)
            }
            $bytes = $utf8NoBom.GetBytes($line)
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $hash = ([BitConverter]::ToString(
                    $sha.ComputeHash($bytes))).Replace('-', '')
            }
            finally {
                $sha.Dispose()
            }
            $torn = [ordered]@{
                ReportedAsTornFinalLine = $true
                LineNumber = $index + 1
                CharacterLength = $line.Length
                Utf8Length = $bytes.Length
                Sha256 = $hash
                Preview = if ($line.Length -gt 256) {
                    $line.Substring(0, 256)
                }
                else { $line }
                ParseError = $_.Exception.Message
            }
        }
    }
    [ordered]@{
        Records = $records.ToArray()
        TornFinalLine = $torn
    }
}

function Write-DurableJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream, $utf8NoBom, 4096, $true)
        try {
            $writer.Write(($Value | ConvertTo-Json -Depth 20))
            $writer.Write([Environment]::NewLine)
            $writer.Flush()
            $stream.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RelevantEvents {
    param([Parameter(Mandatory = $true)][datetime] $StartTime)

    $result = [Collections.Generic.List[object]]::new()
    foreach ($logName in @('System', 'Application')) {
        $events = try {
            @(Get-WinEvent -FilterHashtable @{
                LogName = $logName
                StartTime = $StartTime
            } -ErrorAction Stop)
        }
        catch [Exception] {
            if ($_.FullyQualifiedErrorId -match
                'NoMatchingEventsFound|NoMatchingEvents') {
                @()
            }
            else {
                throw
            }
        }
        foreach ($event in $events) {
            $provider = [string]$event.ProviderName
            $message = [string]$event.Message
            $systemProviderPattern =
                'WHEA-Logger|(^Display$)|amdwddmg|amdkmdag|DxgKrnl|' +
                'BugCheck|Kernel-Power|volmgr'
            $applicationProviderPattern =
                'Windows Error Reporting|Application Error|Application Hang|' +
                '\.NET Runtime'
            $applicationMessagePattern =
                'LiveKernelEvent|0x141|WATCHDOG|hardware error|BlueScreen|' +
                'Cinebench|FanControl|amdkmdag'
            $relevant = if ($logName -eq 'System') {
                $provider -match $systemProviderPattern -or
                $message -match
                    'LiveKernelEvent|0x141|WATCHDOG|hardware error|BlueScreen'
            }
            else {
                ($provider -match $applicationProviderPattern) -and
                ($message -match $applicationMessagePattern)
            }
            if (-not $relevant) {
                continue
            }
            if ($message.Length -gt 8192) {
                $message = $message.Substring(0, 8192)
            }
            $result.Add([ordered]@{
                TimeCreated = $event.TimeCreated.ToString('o')
                LogName = $logName
                RecordId = [long]$event.RecordId
                Id = [int]$event.Id
                Level = [string]$event.LevelDisplayName
                Provider = $provider
                Message = $message
            })
        }
    }
    @($result.ToArray() | Sort-Object TimeCreated)
}

function Convert-InventoryToMap {
    param([AllowEmptyCollection()][object[]] $Inventory)

    $map = @{}
    foreach ($item in @($Inventory)) {
        $map[[string]$item.Path] =
            "$($item.Length)|$($item.LastWriteTimeUtc)"
    }
    $map
}

function Add-Candidate {
    param(
        [Parameter(Mandatory = $true)][string] $Category,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][bool] $CopyAllowed,
        [string] $Reason
    )

    if (-not [IO.File]::Exists($Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    $script:candidates.Add([ordered]@{
        Category = $Category
        Path = $item.FullName
        Length = $item.Length
        CreationTimeUtc = $item.CreationTimeUtc.ToString('o')
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
        CopyAllowed = $CopyAllowed
        Reason = $Reason
    })
}

function Copy-Candidate {
    param(
        [Parameter(Mandatory = $true)] $Candidate,
        [Parameter(Mandatory = $true)][int] $Sequence
    )

    $categoryDirectory = Join-Path $script:outputRoot `
        ([string]$Candidate.Category -replace '[^A-Za-z0-9._-]', '_')
    [IO.Directory]::CreateDirectory($categoryDirectory) | Out-Null
    $safeName = [IO.Path]::GetFileName([string]$Candidate.Path) -replace
        '[^A-Za-z0-9._-]', '_'
    $destination = Join-Path $categoryDirectory `
        ('{0:D4}-{1}' -f $Sequence, $safeName)
    $copied = $false
    $copyError = $null
    $hash = $null
    if (-not [bool]$Candidate.CopyAllowed) {
        $copyError = [string]$Candidate.Reason
    }
    elseif ([long]$Candidate.Length -gt $MaximumCopyBytes) {
        $copyError =
            "File exceeded MaximumCopyBytes ($MaximumCopyBytes)."
    }
    else {
        try {
            Copy-Item -LiteralPath ([string]$Candidate.Path) `
                -Destination $destination
            $hash = (Get-FileHash -LiteralPath $destination `
                -Algorithm SHA256).Hash
            $copied = $true
        }
        catch {
            $copyError = $_.Exception.Message
        }
    }
    [ordered]@{
        Category = $Candidate.Category
        Source = $Candidate.Path
        SourceLength = [long]$Candidate.Length
        SourceLastWriteTimeUtc = $Candidate.LastWriteTimeUtc
        Copied = $copied
        Destination = if ($copied) { $destination } else { $null }
        Sha256 = $hash
        Note = $copyError
    }
}

Assert-Elevated
$campaignRoot = Get-FullPath $CampaignDirectory
if (-not [IO.Directory]::Exists($campaignRoot)) {
    throw "Campaign directory does not exist: $campaignRoot"
}
$planPath = Join-Path $campaignRoot 'campaign-plan.json'
$journalPath = Join-Path $campaignRoot 'campaign-journal.jsonl'
if (-not [IO.File]::Exists($planPath) -or
    -not [IO.File]::Exists($journalPath)) {
    throw 'The campaign plan or journal is missing.'
}
$plan = [IO.File]::ReadAllText($planPath) | ConvertFrom-Json
if ([int]$plan.SchemaVersion -ne 1 -or
    [string]$plan.Kind -ne
        'FanControl.MinisforumUM780XTX.CinebenchSoakCampaign') {
    throw 'The campaign plan kind or schema is unsupported.'
}
$journalRead = Read-JsonLines $journalPath
$records = @($journalRead.Records)
$campaignStart = [DateTimeOffset]::Parse(
    [string]$plan.CreatedUtc).UtcDateTime
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
$script:outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $campaignRoot "post-reboot-$stamp"
}
else {
    Get-FullPath $OutputDirectory
}
$campaignPrefix = $campaignRoot + [IO.Path]::DirectorySeparatorChar
if (-not $script:outputRoot.StartsWith(
        $campaignPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain below the campaign directory: $campaignRoot"
}
if ([IO.File]::Exists($script:outputRoot) -or
    [IO.Directory]::Exists($script:outputRoot)) {
    throw "Collector output path already exists: $script:outputRoot"
}
[IO.Directory]::CreateDirectory($script:outputRoot) | Out-Null

$lastSession = @($records | Where-Object Kind -eq 'session-start' |
    Select-Object -Last 1)
$baselineInventory = if ($lastSession.Count -eq 1) {
    @($lastSession[0].Data.LiveKernelInventory)
}
else {
    @()
}
$baselineMap = Convert-InventoryToMap $baselineInventory
$completed = @{}
foreach ($record in $records | Where-Object Kind -eq 'phase-complete') {
    $completed[[string]$record.Data.PhaseId] = $true
}
$incompletePhase = @($plan.Phases | Where-Object {
    -not $completed.ContainsKey([string]$_.Id)
} | Select-Object -First 1)
$startedIncomplete = if ($incompletePhase.Count -eq 1) {
    @($records | Where-Object {
        $_.Kind -eq 'phase-start' -and
        [string]$_.Data.PhaseId -eq [string]$incompletePhase[0].Id
    }).Count -ne 0
}
else {
    $false
}

$script:candidates = [Collections.Generic.List[object]]::new()
$inventoryErrors = [Collections.Generic.List[object]]::new()

$liveKernelRoot = Join-Path $env:SystemRoot 'LiveKernelReports'
try {
    if ([IO.Directory]::Exists($liveKernelRoot)) {
        foreach ($item in Get-ChildItem -LiteralPath $liveKernelRoot `
            -Recurse -File -ErrorAction Stop) {
            $value = "$($item.Length)|$($item.LastWriteTimeUtc.ToString('o'))"
            if (-not $baselineMap.ContainsKey($item.FullName) -or
                $baselineMap[$item.FullName] -ne $value) {
                Add-Candidate -Category 'live-kernel-reports' `
                    -Path $item.FullName -CopyAllowed $true `
                    -Reason 'New or changed since the latest session baseline.'
            }
        }
    }
}
catch {
    $inventoryErrors.Add([ordered]@{
        Category = 'live-kernel-reports'
        Error = $_.Exception.Message
    })
}

foreach ($root in @(
        (Join-Path $env:SystemRoot 'Minidump'),
        (Join-Path $env:LOCALAPPDATA 'CrashDumps'))) {
    try {
        if (-not [IO.Directory]::Exists($root)) {
            continue
        }
        foreach ($item in Get-ChildItem -LiteralPath $root -File `
            -ErrorAction Stop | Where-Object {
                $_.LastWriteTimeUtc -ge $campaignStart
            }) {
            $category = if ($root -like '*Minidump') {
                'windows-minidumps'
            }
            else {
                'local-crash-dumps'
            }
            Add-Candidate -Category $category -Path $item.FullName `
                -CopyAllowed $true -Reason 'Modified during the campaign.'
        }
    }
    catch {
        $inventoryErrors.Add([ordered]@{
            Category = $root
            Error = $_.Exception.Message
        })
    }
}

$memoryDump = Join-Path $env:SystemRoot 'MEMORY.DMP'
if ([IO.File]::Exists($memoryDump)) {
    $memoryItem = Get-Item -LiteralPath $memoryDump
    if ($memoryItem.LastWriteTimeUtc -ge $campaignStart) {
        Add-Candidate -Category 'memory-dump' -Path $memoryDump `
            -CopyAllowed ([bool]$CopyMemoryDump) `
            -Reason 'Use -CopyMemoryDump to copy this potentially large file.'
    }
}

$werRoots = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue')
)
foreach ($root in $werRoots) {
    try {
        if (-not [IO.Directory]::Exists($root)) {
            continue
        }
        foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -File `
            -ErrorAction Stop | Where-Object {
                $_.LastWriteTimeUtc -ge $campaignStart -and
                $_.FullName -match
                    'Kernel_141|LiveKernel|WATCHDOG|BlueScreen|Cinebench|FanControl'
            }) {
            Add-Candidate -Category 'windows-error-reporting' `
                -Path $item.FullName -CopyAllowed $true `
                -Reason 'Relevant WER evidence modified during the campaign.'
        }
    }
    catch {
        $inventoryErrors.Add([ordered]@{
            Category = $root
            Error = $_.Exception.Message
        })
    }
}

$fanControlRoot = [string]$plan.Paths.FanControlDirectory
$fanControlCandidates = @(
    (Join-Path $fanControlRoot 'log.txt'),
    (Join-Path $fanControlRoot 'Configurations\CACHE'),
    (Join-Path $fanControlRoot 'Configurations\userConfig.json'),
    (Join-Path $fanControlRoot `
        'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll')
)
foreach ($path in $fanControlCandidates) {
    if ([IO.File]::Exists($path)) {
        Add-Candidate -Category 'fancontrol' -Path $path `
            -CopyAllowed $true -Reason 'Current post-reboot Fan Control state.'
    }
}

$redshiftRoot = [string]$plan.Paths.RedshiftLogDirectory
try {
    if ([IO.Directory]::Exists($redshiftRoot)) {
        foreach ($item in Get-ChildItem -LiteralPath $redshiftRoot -Recurse `
            -File -ErrorAction Stop | Where-Object {
                $_.LastWriteTimeUtc -ge $campaignStart
            }) {
            Add-Candidate -Category 'redshift-logs' -Path $item.FullName `
                -CopyAllowed $true -Reason 'Modified during the campaign.'
        }
    }
}
catch {
    $inventoryErrors.Add([ordered]@{
        Category = 'redshift-logs'
        Error = $_.Exception.Message
    })
}

$copyRecords = [Collections.Generic.List[object]]::new()
$sequence = 0
foreach ($candidate in $script:candidates) {
    $sequence++
    $copyRecords.Add((Copy-Candidate -Candidate $candidate `
        -Sequence $sequence))
}

$events = try {
    # Get-WinEvent interprets FilterHashtable StartTime as local wall-clock
    # time even when the DateTime carries Utc kind.
    @(Get-RelevantEvents -StartTime $campaignStart.ToLocalTime())
}
catch {
    $inventoryErrors.Add([ordered]@{
        Category = 'windows-events'
        Error = $_.Exception.Message
    })
    @()
}
$events = @($events)
Write-DurableJson -Path (Join-Path $script:outputRoot 'events.json') `
    -Value $events

$os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
$currentBootUtc = ([datetime]$os.LastBootUpTime).ToUniversalTime()
$previousBootUtc = if ($lastSession.Count -eq 1) {
    [DateTimeOffset]::Parse(
        [string]$lastSession[0].Data.BootIdentity.LastBootUpTimeUtc).UtcDateTime
}
else {
    $null
}
$manifest = [ordered]@{
    SchemaVersion = 1
    Kind = 'FanControl.MinisforumUM780XTX.CinebenchPostRebootEvidence'
    CapturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    CampaignDirectory = $campaignRoot
    CampaignStartUtc = $campaignStart.ToString('o')
    OutputDirectory = $script:outputRoot
    CurrentBootUtc = $currentBootUtc.ToString('o')
    PreviousSessionBootUtc = if ($previousBootUtc) {
        $previousBootUtc.ToString('o')
    }
    else { $null }
    BootChanged = $null -ne $previousBootUtc -and
        $currentBootUtc -ne $previousBootUtc
    CompletedPhaseCount = $completed.Count
    ExpectedPhaseCount = @($plan.Phases).Count
    IncompletePhaseId = if ($incompletePhase.Count -eq 1) {
        [string]$incompletePhase[0].Id
    }
    else { $null }
    IncompletePhaseHadStarted = $startedIncomplete
    AutomaticResumePerformed = $false
    ResumeInstruction =
        'Review this evidence, start Fan Control, then explicitly use -Resume -AcknowledgeInterruptedPhase.'
    CopyPolicy = [ordered]@{
        MaximumCopyBytes = $MaximumCopyBytes
        CopyMemoryDump = [bool]$CopyMemoryDump
    }
    EvidenceFiles = $copyRecords.ToArray()
    RelevantEventCount = $events.Count
    InventoryErrors = $inventoryErrors.ToArray()
    JournalRead = [ordered]@{
        ValidRecordCount = $records.Count
        TornFinalLine = $journalRead.TornFinalLine
        InteriorCorruptionAccepted = $false
    }
}
$manifestPath = Join-Path $script:outputRoot 'manifest.json'
Write-DurableJson -Path $manifestPath -Value $manifest

[pscustomobject]@{
    Status = 'CapturedNoResumePerformed'
    OutputDirectory = $script:outputRoot
    ManifestPath = $manifestPath
    CopiedFileCount = @($copyRecords | Where-Object Copied).Count
    IncompletePhaseId = $manifest.IncompletePhaseId
    BootChanged = $manifest.BootChanged
    RelevantEventCount = $events.Count
    JournalTornFinalLineReported =
        $null -ne $journalRead.TornFinalLine
}
