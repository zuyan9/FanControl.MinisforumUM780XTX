[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $FanControlDirectory,

    [Parameter(Mandatory = $true)]
    [string] $CinebenchPath,

    [Parameter(Mandatory = $true)]
    [string] $ActiveConfigPath,

    [Parameter(Mandatory = $true)]
    [string] $DisabledConfigPath,

    [ValidateRange(1, 100)]
    [int] $Cycles = 20,

    [ValidateRange(10, 7200)]
    [int] $MinimumPhaseSeconds = 900,

    [ValidateRange(30, 10800)]
    [int] $HardPhaseSeconds = 1500,

    [ValidateRange(0, 300)]
    [int] $DrainSeconds = 15,

    [ValidateRange(0, 51)]
    [int] $CpuCode = 10,

    [ValidateRange(1, 120)]
    [double] $CpuMaximumC = 100,

    [ValidateRange(1, 120)]
    [double] $SystemMaximumC = 80,

    [ValidateRange(1, 120)]
    [double] $GpuMaximumC = 95,

    [ValidateRange(1, 120)]
    [double] $DimmMaximumC = 80,

    [ValidateRange(1, 100)]
    [double] $CpuLoadThresholdPercent = 80,

    [ValidateRange(1, 100)]
    [double] $GpuLoadThresholdPercent = 50,

    [ValidateRange(0.1, 1.0)]
    [double] $MinimumHighLoadFraction = 0.5,

    [ValidateRange(0, 300)]
    [int] $LoadWarmupSeconds = 30,

    [ValidateRange(30, 900)]
    [int] $ProgressStallSeconds = 180,

    [string] $RedshiftLogDirectory,

    [string] $UserAbortPath,

    [switch] $Resume,

    [switch] $AcknowledgeInterruptedPhase,

    [switch] $LeaveFanControlRunning,

    [switch] $DryRun
)

<#
.SYNOPSIS
Runs a resumable UM780 XTX Cinebench GPU/CPU low-fan soak campaign.

.DESCRIPTION
The default campaign performs 20 cycles. Each cycle runs the Cinebench GPU
benchmark for a minimum of 900 seconds, then the multi-threaded CPU benchmark
for a minimum of 900 seconds. Cinebench may run longer to finish its current
render. HardPhaseSeconds is an independent kill deadline, not the requested
benchmark duration.

The dedicated active Fan Control configuration must expose CPU raw-v1 at the
requested native code and leave the system control disabled/firmware-owned.
The disabled configuration must disable both UM780 controls. This script never
performs direct EC or port I/O; it drives the real Fan Control process via IPC.

Use -DryRun to validate paths, hashes, exact machine identity, configurations,
and the phase plan without requiring elevation, loading a configuration, or
starting any process. A short qualification can be requested with, for
example, -Cycles 1 -MinimumPhaseSeconds 30 -HardPhaseSeconds 900.

After an interrupted or failed phase, -Resume also requires
-AcknowledgeInterruptedPhase. The incomplete phase is rerun in a new attempt
directory. Resume is deliberately never automatic after a reboot or fault.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$invariant = [Globalization.CultureInfo]::InvariantCulture

$pluginName = 'Minisforum UM780 XTX (F7BSD)'
$cpuControlId =
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-raw-v1"
$systemControlId =
    "$pluginName/minisforum.um780xtx.f7bsd.system-raw-v2"
$cpuRpmId = "$pluginName/minisforum.um780xtx.f7bsd.fan1"
$systemRpmId = "$pluginName/minisforum.um780xtx.f7bsd.fan2"
$cpuTemperatureId =
    "$pluginName/minisforum.um780xtx.f7bsd.cpu-temperature"
$systemTemperatureId =
    "$pluginName/minisforum.um780xtx.f7bsd.system-temperature"

if ($HardPhaseSeconds -lt ($MinimumPhaseSeconds + 30)) {
    throw 'HardPhaseSeconds must be at least 30 seconds above MinimumPhaseSeconds.'
}
if (($HardPhaseSeconds + $DrainSeconds + 30) -gt 86400) {
    throw 'The phase guard duration cannot exceed 86400 seconds.'
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Get-FileRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "Expected a file, found a directory: $Path"
    }
    [ordered]@{
        Path = $item.FullName
        Length = $item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
        Sha256 = (Get-FileHash -LiteralPath $item.FullName `
            -Algorithm SHA256).Hash.ToUpperInvariant()
        FileVersion = [string]$item.VersionInfo.FileVersion
    }
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    $property.Value
}

function Get-MachineIdentity {
    $path = 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
    $properties = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
    [ordered]@{
        Product = [string]$properties.SystemProductName
        Board = [string]$properties.BaseBoardProduct
        BoardVersion = [string]$properties.BaseBoardVersion
        BiosVersion = [string]$properties.BIOSVersion
        EcMajor = [int]$properties.ECFirmwareMajorRelease
        EcMinor = [int]$properties.ECFirmwareMinorRelease
    }
}

function Assert-ExactMachine {
    param([Parameter(Mandatory = $true)] $Identity)

    $matches =
        $Identity.Product -ceq 'Venus series' -and
        $Identity.Board -ceq 'F7BSD' -and
        $Identity.BoardVersion -ceq '1.1' -and
        $Identity.BiosVersion -ceq '1.06' -and
        $Identity.EcMajor -eq 0 -and
        $Identity.EcMinor -eq 8
    if (-not $matches) {
        throw (
            'Expected Venus series/F7BSD revision 1.1, BIOS 1.06, EC 0.8; ' +
            "found $($Identity.Product)/$($Identity.Board) revision " +
            "$($Identity.BoardVersion), BIOS $($Identity.BiosVersion), EC " +
            "$($Identity.EcMajor).$($Identity.EcMinor).")
    }
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the active campaign from an elevated PowerShell session.'
    }
}

function Assert-CampaignConfig {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][bool] $Active
    )

    $config = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    $controls = @($config.FanControl.Controls)
    if ($controls.Count -ne 2) {
        throw "$Path must contain exactly the two UM780 controls."
    }
    $cpu = @($controls | Where-Object Identifier -ceq $cpuControlId)
    $system = @($controls | Where-Object Identifier -ceq $systemControlId)
    if ($cpu.Count -ne 1 -or $system.Count -ne 1) {
        throw "$Path does not contain exactly one raw-v1 CPU and raw-v2 system control."
    }

    if ($Active) {
        $expectedManual = [int][Math]::Round(
            $CpuCode * 100.0 / 51.0,
            [MidpointRounding]::AwayFromZero)
        if (-not [bool](Get-PropertyValue $cpu[0] 'Enable') -or
            -not [bool](Get-PropertyValue $cpu[0] 'ManualControl') -or
            [int](Get-PropertyValue $cpu[0] 'ManualControlValue') -ne
                $expectedManual -or
            $null -ne (Get-PropertyValue $cpu[0] 'SelectedFanCurve')) {
            throw (
                "$Path must enable CPU raw-v1 as manual $expectedManual percent " +
                "(native code $CpuCode) with no selected curve.")
        }
        if ([bool](Get-PropertyValue $system[0] 'Enable')) {
            throw "$Path must leave the UM780 system control disabled."
        }
    }
    else {
        if ([bool](Get-PropertyValue $cpu[0] 'Enable') -or
            [bool](Get-PropertyValue $system[0] 'Enable')) {
            throw "$Path must disable both UM780 controls."
        }
    }

    foreach ($control in @($cpu[0], $system[0])) {
        if ([bool](Get-PropertyValue $control 'ForceApply')) {
            throw "$Path must keep ForceApply disabled for both UM780 controls."
        }
    }
}

function Write-DurableJsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value,
        [switch] $CreateNew
    )

    $mode = if ($CreateNew) {
        [IO.FileMode]::CreateNew
    }
    else {
        [IO.FileMode]::Create
    }
    $stream = [IO.FileStream]::new(
        $Path, $mode, [IO.FileAccess]::Write, [IO.FileShare]::Read,
        4096, [IO.FileOptions]::WriteThrough)
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

function Write-DurableJsonLine {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::Append, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream, $utf8NoBom, 4096, $true)
        try {
            $writer.WriteLine(($Value | ConvertTo-Json -Depth 12 -Compress))
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

function Write-Journal {
    param(
        [Parameter(Mandatory = $true)][string] $Kind,
        [hashtable] $Data = @{}
    )

    $record = [ordered]@{
        Sequence = $script:journalSequence
        Kind = $Kind
        Utc = [DateTimeOffset]::UtcNow.ToString('o')
        SessionId = $script:sessionId
        Data = $Data
    }
    $script:journalSequence++
    Write-DurableJsonLine -Path $script:journalPath -Value $record
}

function Read-JsonLines {
    param([Parameter(Mandatory = $true)][string] $Path)

    $records = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        try {
            $records.Add(($line | ConvertFrom-Json -ErrorAction Stop))
        }
        catch {
            throw "Invalid JSON in $Path at line $lineNumber`: $($_.Exception.Message)"
        }
    }
    $records.ToArray()
}

function ConvertFrom-ResumableJsonLines {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [string[]] $Lines,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $lines = @($Lines)
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
                    "Interior JSON corruption in $Description at line $($index + 1): " +
                    $_.Exception.Message)
            }
            $bytes = $utf8NoBom.GetBytes($line)
            $torn = [ordered]@{
                LineNumber = $index + 1
                CharacterLength = $line.Length
                Utf8Length = $bytes.Length
                Sha256 = Get-BytesSha256 -Bytes $bytes -Count $bytes.Length
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

function Read-ResumableJsonLines {
    param([Parameter(Mandatory = $true)][string] $Path)

    ConvertFrom-ResumableJsonLines `
        -Lines @(Get-Content -LiteralPath $Path -ErrorAction Stop) `
        -Description $Path
}

function Repair-TornJournalTail {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object[]] $Records,
        [Parameter(Mandatory = $true)] $TornFinalLine
    )

    $archive = Join-Path $script:outputRoot `
        "campaign-journal-before-tail-recovery-$($script:sessionId).jsonl"
    $temporary = Join-Path $script:outputRoot `
        ".campaign-journal-repair-$($script:sessionId).tmp"
    if ([IO.File]::Exists($archive) -or [IO.File]::Exists($temporary)) {
        throw "Journal recovery archive already exists: $archive"
    }
    $originalHash = (Get-FileHash -LiteralPath $Path `
        -Algorithm SHA256).Hash
    $stream = [IO.FileStream]::new(
        $temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream, $utf8NoBom, 4096, $true)
        try {
            foreach ($record in $Records) {
                $writer.WriteLine(
                    ($record | ConvertTo-Json -Depth 20 -Compress))
            }
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
    $temporaryHash = (Get-FileHash -LiteralPath $temporary `
        -Algorithm SHA256).Hash
    $verified = @(Read-JsonLines $temporary)
    if ($verified.Count -ne $Records.Count) {
        Remove-Item -LiteralPath $temporary -Force
        throw (
            "Verified repair contained $($verified.Count) records; expected " +
            "$($Records.Count). The live journal was not replaced.")
    }
    try {
        # File.Replace is atomic on the same NTFS volume. The destination backup
        # is created by the same replacement transaction, preserving the exact
        # torn original rather than relying on an earlier non-atomic copy.
        [IO.File]::Replace($temporary, $Path, $archive, $true)
        foreach ($durablePath in @($Path, $archive)) {
            $durable = [IO.FileStream]::new(
                $durablePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
            try {
                $durable.Flush($true)
            }
            finally {
                $durable.Dispose()
            }
        }
    }
    finally {
        if ([IO.File]::Exists($temporary)) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
    if (-not [IO.File]::Exists($archive) -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne
            $originalHash -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne
            $temporaryHash -or
        @(Read-JsonLines $Path).Count -ne $Records.Count) {
        throw (
            'Atomic journal repair verification failed. The exact pre-repair ' +
            "journal remains at $archive.")
    }
    [ordered]@{
        ArchivePath = $archive
        ArchiveSha256 = $originalHash
        RepairedJournalSha256 = $temporaryHash
        AtomicReplacementVerified = $true
        TornFinalLine = $TornFinalLine
        PreservedRecordCount = $Records.Count
    }
}

function ConvertTo-QuotedArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Contains('"')) {
        throw 'Process arguments containing a quote are not supported.'
    }
    '"' + $Value + '"'
}

function Get-ProcessIdentity {
    param([Parameter(Mandatory = $true)][Diagnostics.Process] $Process)

    $Process.Refresh()
    $processPath = try { [string]$Process.Path } catch { $null }
    $processName = try { [string]$Process.ProcessName } catch { $null }
    [ordered]@{
        Id = $Process.Id
        StartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        StartTimeUtc = $Process.StartTime.ToUniversalTime().ToString('o')
        Path = $processPath
        Name = $processName
    }
}

function Test-ExactProcessAlive {
    param(
        [Parameter(Mandatory = $true)][int] $ProcessId,
        [Parameter(Mandatory = $true)][long] $StartTimeUtcTicks
    )

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }
    try {
        $process.StartTime.ToUniversalTime().Ticks -eq $StartTimeUtcTicks
    }
    catch {
        $false
    }
}

function Stop-ExactProcess {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)][long] $StartTimeUtcTicks,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }
    if ($Process.StartTime.ToUniversalTime().Ticks -ne $StartTimeUtcTicks) {
        throw "Refusing to stop $Description because its process identity changed."
    }
    $Process.Kill()
    [void]$Process.WaitForExit(5000)
    if (-not $Process.HasExited) {
        throw "$Description did not exit after Kill()."
    }
}

function Assert-SoleCinebenchProcess {
    param(
        [Parameter(Mandatory = $true)][int] $ExpectedProcessId,
        [Parameter(Mandatory = $true)][long] $ExpectedStartTimeUtcTicks
    )

    $processes = @(Get-Process -Name Cinebench -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1) {
        throw (
            "Expected only the launched Cinebench process; found " +
            "$($processes.Count) Cinebench processes.")
    }
    if ($processes[0].Id -ne $ExpectedProcessId -or
        $processes[0].StartTime.ToUniversalTime().Ticks -ne
            $ExpectedStartTimeUtcTicks) {
        throw 'A different Cinebench process appeared during the phase.'
    }
    $actualPath = try { [string]$processes[0].Path } catch { $null }
    if ([string]::IsNullOrWhiteSpace($actualPath) -or
        [IO.Path]::GetFullPath($actualPath) -ine $script:cinebenchExe) {
        throw "The exact Cinebench executable path changed: $actualPath"
    }
}

function Get-CurrentPowerShellPath {
    $process = Get-Process -Id $PID -ErrorAction Stop
    $path = try { [string]$process.Path } catch { $null }
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = [string]$process.MainModule.FileName
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw 'Unable to resolve the current PowerShell executable.'
    }
    $path
}

function Get-BootIdentity {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    [ordered]@{
        LastBootUpTimeUtc =
            ([datetime]$os.LastBootUpTime).ToUniversalTime().ToString('o')
        LocalDateTime = ([datetime]$os.LocalDateTime).ToString('o')
    }
}

function Get-EventRecordBaselines {
    $result = [ordered]@{}
    foreach ($logName in @('System', 'Application')) {
        $latest = Get-WinEvent -LogName $logName -MaxEvents 1 `
            -ErrorAction Stop
        $result[$logName] = [long]$latest.RecordId
    }
    $result
}

function Get-NewRelevantEvents {
    $result = [Collections.Generic.List[object]]::new()
    foreach ($logName in @('System', 'Application')) {
        $cursor = [long]$script:eventCursors[$logName]
        $events = try {
            @(Get-WinEvent -LogName $logName `
                -FilterXPath "*[System[(EventRecordID > $cursor)]]" `
                -ErrorAction Stop)
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
        $events = @($events)
        if ($events.Count -ne 0) {
            $script:eventCursors[$logName] = [long](
                $events | Measure-Object -Property RecordId -Maximum).Maximum
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
                'LiveKernelEvent|0x141|WATCHDOG|hardware error|Cinebench|' +
                'FanControl|amdkmdag'
            $relevant = if ($logName -eq 'System') {
                $provider -match $systemProviderPattern -or
                $message -match 'LiveKernelEvent|0x141|WATCHDOG|hardware error'
            }
            else {
                ($provider -match $applicationProviderPattern) -and
                ($message -match $applicationMessagePattern)
            }
            if (-not $relevant) {
                continue
            }
            if ($message.Length -gt 4096) {
                $message = $message.Substring(0, 4096)
            }
            $result.Add([ordered]@{
                LogName = $logName
                RecordId = [long]$event.RecordId
                TimeCreated = $event.TimeCreated.ToString('o')
                Id = [int]$event.Id
                Level = [string]$event.LevelDisplayName
                Provider = $provider
                Message = $message
            })
        }
    }
    $result.ToArray()
}

function Get-LiveKernelInventory {
    $root = Join-Path $env:SystemRoot 'LiveKernelReports'
    if (-not [IO.Directory]::Exists($root)) {
        return @()
    }
    @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        ForEach-Object {
            [ordered]@{
                Path = $_.FullName
                Length = $_.Length
                LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o')
            }
        })
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

function Get-LiveKernelChanges {
    $current = @(Get-LiveKernelInventory)
    $currentMap = Convert-InventoryToMap $current
    $changes = [Collections.Generic.List[object]]::new()
    foreach ($item in $current) {
        $path = [string]$item.Path
        $value = "$($item.Length)|$($item.LastWriteTimeUtc)"
        if (-not $script:dumpBaselineMap.ContainsKey($path) -or
            $script:dumpBaselineMap[$path] -ne $value) {
            $changes.Add($item)
        }
    }
    $changes.ToArray()
}

function Get-RedshiftLogInventory {
    if (-not [IO.Directory]::Exists($script:redshiftLogRoot)) {
        return @()
    }
    @(Get-ChildItem -LiteralPath $script:redshiftLogRoot -Recurse -File `
        -ErrorAction Stop | ForEach-Object {
            [ordered]@{
                Path = $_.FullName
                Length = $_.Length
                CreationTimeUtc = $_.CreationTimeUtc.ToString('o')
                LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o')
                Sha256 = (Get-FileHash -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToUpperInvariant()
            }
        })
}

function Get-BytesSha256 {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][ValidateRange(0, [int]::MaxValue)]
        [int] $Count
    )

    if ($Count -gt $Bytes.Length) {
        throw 'The requested hash range exceeds the byte array.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString(
            $sha.ComputeHash($Bytes, 0, $Count))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Write-DurableBytes {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]] $Bytes
    )

    $stream = [IO.FileStream]::new(
        $Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RedshiftDeltaOffset {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [byte[]] $CurrentBytes,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]] $Before
    )

    # Redshift rotates Log.Latest.0 by renaming it. Match every current-file
    # prefix against every phase-baseline content hash so a renamed historical
    # log contributes no old completion markers. The longest matching baseline
    # is the only safe delta boundary.
    $prefixHashes = @{}
    $offset = 0
    foreach ($baseline in @($Before | Sort-Object Length -Descending)) {
        $length = [long]$baseline.Length
        if ($length -gt $CurrentBytes.LongLength -or
            $length -gt [int]::MaxValue) {
            continue
        }
        $key = [string]$length
        if (-not $prefixHashes.ContainsKey($key)) {
            $prefixHashes[$key] = Get-BytesSha256 -Bytes $CurrentBytes `
                -Count ([int]$length)
        }
        if ([string]$prefixHashes[$key] -ceq [string]$baseline.Sha256) {
            $offset = [int]$length
            break
        }
    }
    $offset
}

function Get-RedshiftSessionEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][datetime] $WorkloadStartUtc,
        [Parameter(Mandatory = $true)][datetime] $WorkloadEndUtc,
        [Parameter(Mandatory = $true)][string] $ExpectedApplicationPath,
        [Parameter(Mandatory = $true)][int] $WorkloadProcessId,
        [Parameter(Mandatory = $true)][ValidateSet('gpu', 'cpu')]
        [string] $WorkloadType
    )

    $results = [Collections.Generic.List[object]]::new()
    $sessions = [regex]::Matches(
        $Text,
        'Session:\s*(?<stamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    for ($index = 0; $index -lt $sessions.Count; $index++) {
        $session = $sessions[$index]
        $priorBoundary = if ($index -eq 0) { 0 } else {
            $sessions[$index - 1].Index
        }
        $nextBoundary = if ($index + 1 -lt $sessions.Count) {
            $sessions[$index + 1].Index
        }
        else { $Text.Length }
        $header = $Text.Substring(
            $priorBoundary, $session.Index - $priorBoundary)
        $segment = $Text.Substring(
            $session.Index, $nextBoundary - $session.Index)
        $applicationMarker = "Application Path: $ExpectedApplicationPath"
        $applicationMatched = $header.IndexOf(
            $applicationMarker,
            [StringComparison]::OrdinalIgnoreCase) -ge 0
        $parsed = [datetime]::MinValue
        $parsedOk = [datetime]::TryParseExact(
            $session.Groups['stamp'].Value,
            'yyyy-MM-dd HH:mm:ss',
            $invariant,
            [Globalization.DateTimeStyles]::AllowWhiteSpaces,
            [ref]$parsed)
        $sessionUtc = $null
        $withinWindow = $false
        if ($parsedOk) {
            $local = [datetime]::SpecifyKind(
                $parsed, [DateTimeKind]::Unspecified)
            $sessionUtc = [TimeZoneInfo]::ConvertTimeToUtc($local)
            $withinWindow =
                $sessionUtc -ge $WorkloadStartUtc.AddSeconds(-10) -and
                $sessionUtc -le $WorkloadEndUtc.AddSeconds(10)
        }
        $begins = [regex]::Matches(
            $segment, 'Renderer:\s*Render Begin',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $ends = [regex]::Matches(
            $segment, 'Renderer:\s*Render End',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $blocks = [regex]::Matches(
            $segment, 'Block\s+15/15',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $pairedEnds = 0
        foreach ($end in $ends) {
            if (@($begins | Where-Object Index -lt $end.Index).Count -ne 0) {
                $pairedEnds++
            }
        }
        $valid = $applicationMatched -and $withinWindow -and
            $pairedEnds -gt 0 -and
            ($WorkloadType -ne 'gpu' -or $blocks.Count -gt 0)
        $results.Add([ordered]@{
            WorkloadProcessId = $WorkloadProcessId
            WorkloadType = $WorkloadType
            ExpectedApplicationPath = $ExpectedApplicationPath
            ApplicationPathMatched = $applicationMatched
            SessionLocal = $session.Groups['stamp'].Value
            SessionUtc = if ($sessionUtc) { $sessionUtc.ToString('o') } else { $null }
            WithinExactProcessWindow = $withinWindow
            RenderBeginCount = $begins.Count
            PairedRenderEndCount = $pairedEnds
            Block15Count = $blocks.Count
            ValidCompletionEvidence = $valid
        })
    }
    $results.ToArray()
}

function Archive-ChangedRedshiftLogs {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]] $Before,
        [Parameter(Mandatory = $true)][string] $DestinationDirectory,
        [datetime] $WorkloadStartUtc = [datetime]::MinValue,
        [datetime] $WorkloadEndUtc = [datetime]::MinValue,
        [int] $WorkloadProcessId = 0,
        [ValidateSet('', 'gpu', 'cpu')][string] $WorkloadType = ''
    )

    [IO.Directory]::CreateDirectory($DestinationDirectory) | Out-Null
    $after = @(Get-RedshiftLogInventory)
    $copied = [Collections.Generic.List[object]]::new()
    $renderEndCount = 0
    $block15Count = 0
    $sessionEvidence = [Collections.Generic.List[object]]::new()
    foreach ($item in $after) {
        $path = [string]$item.Path
        $bytes = [IO.File]::ReadAllBytes($path)
        $deltaOffset = Get-RedshiftDeltaOffset -CurrentBytes $bytes `
            -Before $Before
        if ($deltaOffset -eq $bytes.Length) {
            continue
        }
        $deltaLength = $bytes.Length - $deltaOffset
        $delta = [byte[]]::new($deltaLength)
        [Array]::Copy($bytes, $deltaOffset, $delta, 0, $deltaLength)
        $relative = $path.Substring($script:redshiftLogRoot.Length).
            TrimStart('\', '/')
        $safeName = ($relative -replace '[\\/:*?"<>|]', '_')
        $destination = Join-Path $DestinationDirectory $safeName
        Copy-Item -LiteralPath $path -Destination $destination
        $deltaDestination = "$destination.phase-delta.bin"
        Write-DurableBytes -Path $deltaDestination -Bytes $delta
        $hash = (Get-FileHash -LiteralPath $destination `
            -Algorithm SHA256).Hash
        $deltaHash = Get-BytesSha256 -Bytes $delta -Count $delta.Length
        $fileEvidence = @()
        if ([IO.Path]::GetExtension($destination) -ieq '.html' -and
            $WorkloadProcessId -gt 0 -and
            $WorkloadStartUtc -ne [datetime]::MinValue -and
            $WorkloadEndUtc -ne [datetime]::MinValue -and
            $WorkloadType) {
            $text = [Text.Encoding]::UTF8.GetString($delta)
            $fileEvidence = @(Get-RedshiftSessionEvidence -Text $text `
                -WorkloadStartUtc $WorkloadStartUtc `
                -WorkloadEndUtc $WorkloadEndUtc `
                -ExpectedApplicationPath $script:cinebenchExe `
                -WorkloadProcessId $WorkloadProcessId `
                -WorkloadType $WorkloadType)
            foreach ($evidence in $fileEvidence) {
                $record = [ordered]@{
                    Source = $path
                    DeltaOffset = $deltaOffset
                    Evidence = $evidence
                }
                $sessionEvidence.Add($record)
                if ([bool]$evidence.ValidCompletionEvidence) {
                    $renderEndCount += [int]$evidence.PairedRenderEndCount
                    $block15Count += [int]$evidence.Block15Count
                }
            }
        }
        $copied.Add([ordered]@{
            Source = $path
            Destination = $destination
            Length = [long]$item.Length
            Sha256 = $hash
            BaselinePrefixBytes = $deltaOffset
            PhaseDeltaBytes = $deltaLength
            PhaseDeltaDestination = $deltaDestination
            PhaseDeltaSha256 = $deltaHash
            SessionEvidence = $fileEvidence
        })
    }
    [ordered]@{
        Files = $copied.ToArray()
        RenderEndCount = $renderEndCount
        Block15Count = $block15Count
        SessionEvidence = $sessionEvidence.ToArray()
        ValidCompletionEvidenceCount = @($sessionEvidence | Where-Object {
            [bool]$_.Evidence.ValidCompletionEvidence
        }).Count
    }
}

function Get-FanControlIdentity {
    $processes = @(Get-Process -Name FanControl -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1) {
        throw "Expected exactly one FanControl process; found $($processes.Count)."
    }
    if (-not $processes[0].Responding) {
        throw 'Fan Control is not responding.'
    }
    Get-ProcessIdentity $processes[0]
}

function Assert-FanControlIdentity {
    if (-not (Test-ExactProcessAlive `
            -ProcessId ([int]$script:fanControlIdentity.Id) `
            -StartTimeUtcTicks ([long]$script:fanControlIdentity.StartTimeUtcTicks))) {
        throw 'Fan Control exited or changed process identity.'
    }
    $process = Get-Process -Id ([int]$script:fanControlIdentity.Id) `
        -ErrorAction Stop
    if (-not $process.Responding) {
        throw 'Fan Control stopped responding.'
    }
}

function Invoke-Ipc {
    param(
        [Parameter(Mandatory = $true)][string] $Command,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $OutputPath
    )

    & $script:ipcExecutable $script:ipcAssembly $Command @Arguments `
        --output $OutputPath
    $exitCode = $LASTEXITCODE
    $text = if ([IO.File]::Exists($OutputPath)) {
        [IO.File]::ReadAllText($OutputPath)
    }
    else {
        ''
    }
    if ($exitCode -ne 0) {
        throw "Fan Control IPC '$Command' failed with exit $exitCode`: $text"
    }
    $text
}

function Load-FanControlConfig {
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $output = Join-Path $script:sessionDirectory "$Label-ipc-load.json"
    $reply = Invoke-Ipc -Command 'load' -Arguments @($ConfigPath) `
        -OutputPath $output
    if ($reply -notmatch '"status"\s*:\s*"OK"') {
        throw "Fan Control did not acknowledge $Label configuration: $reply"
    }
}

function Get-SensorValue {
    param(
        [Parameter(Mandatory = $true)] $Snapshot,
        [Parameter(Mandatory = $true)][string] $Identifier
    )

    $matches = @($Snapshot.Sensors | Where-Object Identifier -ceq $Identifier)
    if ($matches.Count -ne 1) {
        return $null
    }
    $matches[0].Value
}

function Assert-ActiveControlSettled {
    $expectedPercent = $CpuCode * 100.0 / 51.0
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Seconds 1
        $output = Join-Path $script:sessionDirectory `
            ("active-preflight-{0:D2}.json" -f $attempt)
        $snapshot = Invoke-Ipc -Command 'plugin-sensors' `
            -OutputPath $output | ConvertFrom-Json
        $cpuControl = Get-SensorValue $snapshot $cpuControlId
        $systemControl = Get-SensorValue $snapshot $systemControlId
        $cpuRpm = Get-SensorValue $snapshot $cpuRpmId
        $systemRpm = Get-SensorValue $snapshot $systemRpmId
        $cpuTemperature = Get-SensorValue $snapshot $cpuTemperatureId
        $systemTemperature = Get-SensorValue $snapshot $systemTemperatureId
        if ($null -ne $cpuControl -and
            [Math]::Abs([double]$cpuControl - $expectedPercent) -le 0.1 -and
            $null -eq $systemControl -and
            $null -ne $cpuRpm -and $null -ne $systemRpm -and
            $null -ne $cpuTemperature -and $null -ne $systemTemperature) {
            return [ordered]@{
                CpuControlPercent = [double]$cpuControl
                SystemControlPercent = $systemControl
                CpuRpm = [double]$cpuRpm
                SystemRpm = [double]$systemRpm
                CpuTemperatureC = [double]$cpuTemperature
                SystemTemperatureC = [double]$systemTemperature
            }
        }
    }
    throw 'CPU raw control/system firmware-owned telemetry did not settle in ten seconds.'
}

function Assert-DisabledControlSettled {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Seconds 1
        $output = Join-Path $script:sessionDirectory `
            ("disabled-final-{0:D2}.json" -f $attempt)
        $snapshot = Invoke-Ipc -Command 'plugin-sensors' `
            -OutputPath $output | ConvertFrom-Json
        $cpuControl = Get-SensorValue $snapshot $cpuControlId
        $systemControl = Get-SensorValue $snapshot $systemControlId
        $cpuRpm = Get-SensorValue $snapshot $cpuRpmId
        $systemRpm = Get-SensorValue $snapshot $systemRpmId
        if ($null -eq $cpuControl -and $null -eq $systemControl -and
            $null -ne $cpuRpm -and $null -ne $systemRpm) {
            return [ordered]@{
                CpuRpm = [double]$cpuRpm
                SystemRpm = [double]$systemRpm
            }
        }
    }
    throw 'Both controls did not verify disabled in ten seconds.'
}

function Start-Heartbeat {
    $parent = Get-Process -Id $PID -ErrorAction Stop
    $parentTicks = $parent.StartTime.ToUniversalTime().Ticks
    $ledger = Join-Path $script:sessionDirectory 'heartbeat.jsonl'
    $stop = Join-Path $script:sessionDirectory 'heartbeat.stop'
    $stdout = Join-Path $script:sessionDirectory 'heartbeat.stdout.log'
    $stderr = Join-Path $script:sessionDirectory 'heartbeat.stderr.log'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        $script:heartbeatScript,
        '-LedgerPath', $ledger,
        '-StopPath', $stop,
        '-ParentProcessId', [string]$PID,
        '-ParentStartTimeUtcTicks', [string]$parentTicks,
        '-SessionId', $script:sessionId
    ) | ForEach-Object { ConvertTo-QuotedArgument ([string]$_) }
    $process = Start-Process -FilePath $script:powerShellPath `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.EnableRaisingEvents = $true
    $null = $process.Handle
    $identity = Get-ProcessIdentity $process
    [ordered]@{
        Process = $process
        Identity = $identity
        StopPath = $stop
        LedgerPath = $ledger
    }
}

function Read-StableJsonLines {
    param([Parameter(Mandatory = $true)][string] $Path)

    $lastError = $null
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            return @(Read-JsonLines $Path)
        }
        catch {
            $lastError = $_.Exception
            if ($attempt -lt 4) {
                Start-Sleep -Milliseconds 50
            }
        }
    }
    throw $lastError
}

function Assert-ContiguousRecordSequence {
    param(
        [Parameter(Mandatory = $true)][object[]] $Records,
        [Parameter(Mandatory = $true)][string] $Description
    )

    for ($index = 0; $index -lt $Records.Count; $index++) {
        if ([long]$Records[$index].Sequence -ne $index) {
            throw "$Description ledger sequence is not contiguous at index $index."
        }
    }
}

function Assert-HeartbeatRecords {
    param(
        [Parameter(Mandatory = $true)][object[]] $Records,
        [Parameter(Mandatory = $true)][ValidateSet('active', 'stopped')]
        [string] $State
    )

    if ($Records.Count -lt 1) {
        throw 'The heartbeat ledger is empty.'
    }
    Assert-ContiguousRecordSequence -Records $Records `
        -Description 'Heartbeat'
    $first = $Records[0]
    if ([string]$first.Kind -cne 'heartbeat-start' -or
        [string]$first.SessionId -cne $script:sessionId -or
        [int]$first.ParentProcessId -ne $PID -or
        [long]$first.Data.ParentStartTimeUtcTicks -ne
            [long]$script:runnerStartTimeUtcTicks) {
        throw 'The durable heartbeat-start record does not match this runner.'
    }
    $allowed = if ($State -eq 'active') {
        @('heartbeat-start', 'heartbeat')
    }
    else {
        @('heartbeat-start', 'heartbeat', 'heartbeat-stopped')
    }
    foreach ($record in $Records) {
        if ([string]$record.Kind -cnotin $allowed) {
            throw "Heartbeat ledger contains failure/unknown kind '$($record.Kind)'."
        }
    }
    $last = $Records[-1]
    if ($State -eq 'active') {
        if ($Records.Count -lt 2 -or [string]$last.Kind -cne 'heartbeat') {
            throw 'The heartbeat has not produced a live sample.'
        }
        $lastUtc = [DateTimeOffset]::Parse([string]$last.Utc)
        $age = ([DateTimeOffset]::UtcNow - $lastUtc).TotalSeconds
        if ($age -lt -2 -or $age -gt 5) {
            throw "The durable heartbeat is stale or future-dated ($age seconds)."
        }
    }
    elseif ([string]$last.Kind -cne 'heartbeat-stopped') {
        throw "Heartbeat terminal record was '$($last.Kind)', not heartbeat-stopped."
    }
    $last
}

function Assert-HeartbeatHealthy {
    if ($null -eq $script:heartbeat) {
        throw 'The campaign heartbeat was not started.'
    }
    if (-not (Test-ExactProcessAlive `
            -ProcessId ([int]$script:heartbeat.Identity.Id) `
            -StartTimeUtcTicks (
                [long]$script:heartbeat.Identity.StartTimeUtcTicks))) {
        throw 'The exact campaign heartbeat process exited or changed identity.'
    }
    if (-not [IO.File]::Exists([string]$script:heartbeat.LedgerPath)) {
        throw 'The durable heartbeat ledger is missing.'
    }
    $records = @(Read-StableJsonLines $script:heartbeat.LedgerPath)
    $null = Assert-HeartbeatRecords -Records $records -State 'active'
}

function Wait-HeartbeatReady {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $lastError = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            Assert-HeartbeatHealthy
            return
        }
        catch {
            $lastError = $_.Exception.Message
            $process = $script:heartbeat.Process
            $process.Refresh()
            if ($process.HasExited) {
                [void]$process.WaitForExit()
                throw "Heartbeat exited before readiness with code $($process.ExitCode): $lastError"
            }
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Heartbeat did not become durably ready: $lastError"
}

function Stop-Heartbeat {
    if ($null -eq $script:heartbeat) {
        return
    }
    if (-not [IO.File]::Exists([string]$script:heartbeat.StopPath)) {
        [IO.File]::WriteAllText(
            [string]$script:heartbeat.StopPath, 'stop', $utf8NoBom)
    }
    $process = $script:heartbeat.Process
    [void]$process.WaitForExit(3000)
    if (-not $process.HasExited) {
        Stop-ExactProcess -Process $process `
            -StartTimeUtcTicks (
                [long]$script:heartbeat.Identity.StartTimeUtcTicks) `
            -Description 'campaign heartbeat'
        throw 'The heartbeat ignored its stop file and required forced termination.'
    }
    [void]$process.WaitForExit()
    $records = @(Read-StableJsonLines $script:heartbeat.LedgerPath)
    $null = Assert-HeartbeatRecords -Records $records -State 'stopped'
    if ($process.ExitCode -ne 0) {
        throw "Heartbeat exited with code $($process.ExitCode)."
    }
}

function Start-TelemetryGuard {
    param(
        [Parameter(Mandatory = $true)][string] $PhaseDirectory,
        [Parameter(Mandatory = $true)][string] $PhaseId
    )

    $ledger = Join-Path $PhaseDirectory 'guard.jsonl'
    $abort = Join-Path $PhaseDirectory 'guard.abort.json'
    $summary = Join-Path $PhaseDirectory 'guard-summary.json'
    $stdout = Join-Path $PhaseDirectory 'guard.stdout.log'
    $stderr = Join-Path $PhaseDirectory 'guard.stderr.log'
    $duration = $HardPhaseSeconds + $DrainSeconds + 30
    $expectedCpu = ($CpuCode * 100.0 / 51.0).ToString('R', $invariant)
    $arguments = @(
        $script:ipcAssembly,
        'guard',
        [string]$duration,
        $ledger,
        $abort,
        $CpuMaximumC.ToString('R', $invariant),
        $SystemMaximumC.ToString('R', $invariant),
        $expectedCpu,
        'null',
        '0',
        $GpuMaximumC.ToString('R', $invariant),
        $DimmMaximumC.ToString('R', $invariant),
        '--output',
        $summary
    ) | ForEach-Object { ConvertTo-QuotedArgument ([string]$_) }
    $process = Start-Process -FilePath $script:ipcExecutable `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.EnableRaisingEvents = $true
    $null = $process.Handle
    $identity = Get-ProcessIdentity $process
    [ordered]@{
        Process = $process
        Identity = $identity
        LedgerPath = $ledger
        AbortPath = $abort
        SummaryPath = $summary
        PhaseId = $PhaseId
    }
}

function Wait-GuardReady {
    param([Parameter(Mandatory = $true)] $Guard)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ([IO.File]::Exists([string]$Guard.AbortPath)) {
            throw "Telemetry guard aborted during readiness: $([IO.File]::ReadAllText($Guard.AbortPath))"
        }
        $Guard.Process.Refresh()
        if ($Guard.Process.HasExited) {
            [void]$Guard.Process.WaitForExit()
            throw "Telemetry guard exited during readiness with code $($Guard.Process.ExitCode)."
        }
        $count = if ([IO.File]::Exists([string]$Guard.LedgerPath)) {
            @(Get-Content -LiteralPath ([string]$Guard.LedgerPath)).Count
        }
        else {
            0
        }
        if ($count -ge 6) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Telemetry guard did not produce six readiness samples in 15 seconds.'
}

function Stop-TelemetryGuard {
    param([Parameter(Mandatory = $true)] $Guard)

    if ($null -eq $Guard) {
        return
    }
    $process = $Guard.Process
    $process.Refresh()
    if (-not $process.HasExited) {
        Stop-ExactProcess -Process $process `
            -StartTimeUtcTicks ([long]$Guard.Identity.StartTimeUtcTicks) `
            -Description "telemetry guard for $($Guard.PhaseId)"
        Write-Journal -Kind 'guard-supervisor-stop' -Data @{
            PhaseId = $Guard.PhaseId
            ProcessId = $process.Id
            Reason = 'workload-and-drain-proven-complete'
        }
    }
}

function Start-PhaseWatchdog {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Workload,
        [Parameter(Mandatory = $true)][long] $WorkloadStartTicks,
        [Parameter(Mandatory = $true)][datetime] $Deadline,
        [Parameter(Mandatory = $true)][string] $PhaseDirectory,
        [Parameter(Mandatory = $true)][string] $PhaseId
    )

    $ledger = Join-Path $PhaseDirectory 'workload-watchdog.jsonl'
    $stop = Join-Path $PhaseDirectory 'workload-watchdog.stop'
    $stdout = Join-Path $PhaseDirectory 'workload-watchdog.stdout.log'
    $stderr = Join-Path $PhaseDirectory 'workload-watchdog.stderr.log'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        $script:watchdogScript,
        '-WorkloadProcessId', [string]$Workload.Id,
        '-WorkloadStartTimeUtcTicks', [string]$WorkloadStartTicks,
        '-DeadlineUtc', $Deadline.ToUniversalTime().ToString('o'),
        '-LedgerPath', $ledger,
        '-StopPath', $stop,
        '-PhaseId', $PhaseId
    ) | ForEach-Object { ConvertTo-QuotedArgument ([string]$_) }
    $process = Start-Process -FilePath $script:powerShellPath `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.EnableRaisingEvents = $true
    $null = $process.Handle
    $identity = Get-ProcessIdentity $process
    [ordered]@{
        Process = $process
        Identity = $identity
        LedgerPath = $ledger
        StopPath = $stop
        WorkloadProcessId = $Workload.Id
        WorkloadStartTimeUtcTicks = $WorkloadStartTicks
        DeadlineUtc = $Deadline.ToUniversalTime()
        PhaseId = $PhaseId
    }
}

function Assert-WatchdogStartRecord {
    param(
        [Parameter(Mandatory = $true)] $Watchdog,
        [Parameter(Mandatory = $true)][object[]] $Records
    )

    if ($Records.Count -lt 1) {
        throw 'The workload-watchdog ledger is empty.'
    }
    Assert-ContiguousRecordSequence -Records $Records `
        -Description 'Workload watchdog'
    $first = $Records[0]
    $recordDeadline = [DateTimeOffset]::Parse(
        [string]$first.Data.DeadlineUtc).UtcDateTime
    if ([string]$first.Kind -cne 'watchdog-start' -or
        [string]$first.PhaseId -cne [string]$Watchdog.PhaseId -or
        [int]$first.WorkloadProcessId -ne
            [int]$Watchdog.WorkloadProcessId -or
        [long]$first.Data.WorkloadStartTimeUtcTicks -ne
            [long]$Watchdog.WorkloadStartTimeUtcTicks -or
        $recordDeadline.Ticks -ne
            ([datetime]$Watchdog.DeadlineUtc).Ticks) {
        throw 'The durable watchdog-start record does not match this phase.'
    }
}

function Assert-PhaseWatchdogHealthy {
    param([Parameter(Mandatory = $true)] $Watchdog)

    if (-not (Test-ExactProcessAlive `
            -ProcessId ([int]$Watchdog.Identity.Id) `
            -StartTimeUtcTicks (
                [long]$Watchdog.Identity.StartTimeUtcTicks))) {
        throw 'The exact workload-watchdog process exited or changed identity.'
    }
    if (-not [IO.File]::Exists([string]$Watchdog.LedgerPath)) {
        throw 'The durable workload-watchdog ledger is missing.'
    }
    $records = @(Read-StableJsonLines $Watchdog.LedgerPath)
    Assert-WatchdogStartRecord -Watchdog $Watchdog -Records $records
    if ($records.Count -ne 1) {
        throw "The active watchdog emitted unexpected record '$($records[-1].Kind)'."
    }
}

function Wait-PhaseWatchdogReady {
    param([Parameter(Mandatory = $true)] $Watchdog)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $lastError = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            Assert-PhaseWatchdogHealthy $Watchdog
            return
        }
        catch {
            $lastError = $_.Exception.Message
            $Watchdog.Process.Refresh()
            if ($Watchdog.Process.HasExited) {
                [void]$Watchdog.Process.WaitForExit()
                throw (
                    'Workload watchdog exited before durable readiness with ' +
                    "code $($Watchdog.Process.ExitCode): $lastError")
            }
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Workload watchdog did not become durably ready: $lastError"
}

function Complete-PhaseWatchdog {
    param(
        [Parameter(Mandatory = $true)] $Watchdog,
        [switch] $RequireWorkloadExited
    )

    if ($null -eq $Watchdog) {
        throw 'The workload watchdog was never started.'
    }
    $process = $Watchdog.Process
    [void]$process.WaitForExit(5000)
    $supervisorDisarmed = $false
    if (-not $process.HasExited) {
        $supervisorDisarmed = $true
        if (-not [IO.File]::Exists([string]$Watchdog.StopPath)) {
            [IO.File]::WriteAllText(
                [string]$Watchdog.StopPath, 'stop', $utf8NoBom)
        }
        [void]$process.WaitForExit(3000)
    }
    if (-not $process.HasExited) {
        Stop-ExactProcess -Process $process `
            -StartTimeUtcTicks (
                [long]$Watchdog.Identity.StartTimeUtcTicks) `
            -Description 'Cinebench workload watchdog'
        throw 'The workload watchdog ignored its stop file and was force-terminated.'
    }
    [void]$process.WaitForExit()
    if (-not [IO.File]::Exists([string]$Watchdog.LedgerPath)) {
        throw 'The workload-watchdog ledger is missing at completion.'
    }
    $records = @(Read-StableJsonLines $Watchdog.LedgerPath)
    Assert-WatchdogStartRecord -Watchdog $Watchdog -Records $records
    if ($records.Count -lt 2) {
        throw 'The workload watchdog has no durable terminal record.'
    }
    $failureKinds = @(
        'watchdog-inspection-error',
        'workload-identity-mismatch',
        'deadline-kill-attempt-failed',
        'deadline-kill-failed'
    )
    $failures = @($records | Where-Object {
        [string]$_.Kind -cin $failureKinds
    })
    if ($failures.Count) {
        throw "The watchdog recorded a hard failure '$($failures[0].Kind)'."
    }
    $terminal = $records[-1]
    $expectedExit = switch ([string]$terminal.Kind) {
        'workload-exited' { 0 }
        'watchdog-stopped' { 0 }
        'deadline-enforced' { 3 }
        default {
            throw "Unknown watchdog terminal record '$($terminal.Kind)'."
        }
    }
    if ($process.ExitCode -ne $expectedExit) {
        throw (
            "Watchdog terminal '$($terminal.Kind)' required exit $expectedExit; " +
            "observed $($process.ExitCode).")
    }
    if ($supervisorDisarmed -or
        ($RequireWorkloadExited -and
            [string]$terminal.Kind -cne 'workload-exited')) {
        throw (
            "Watchdog did not naturally prove workload exit; terminal was " +
            "'$($terminal.Kind)'.")
    }
    [ordered]@{
        Records = $records
        ExitCode = $process.ExitCode
        TerminalKind = [string]$terminal.Kind
        SupervisorDisarmed = $supervisorDisarmed
    }
}

function Get-PerformanceSnapshot {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    $cpu = Get-CimInstance `
        Win32_PerfFormattedData_Counters_ProcessorInformation `
        -Filter "Name='_Total'" -ErrorAction Stop
    $counter = Get-Counter -Counter `
        '\GPU Engine(*)\Utilization Percentage',
        '\GPU Adapter Memory(*)\Dedicated Usage',
        '\GPU Adapter Memory(*)\Shared Usage' -ErrorAction Stop
    $pidPattern = "pid_$ProcessId" + '_'
    $engines = @($counter.CounterSamples | Where-Object {
        $_.Path -like "*$pidPattern*GPU Engine*" -or
        $_.Path -match "gpu engine\(pid_$ProcessId`_"
    })
    # The first condition is retained for provider/localization variants; the
    # second matches the standard Windows path observed on this machine.
    if ($engines.Count -eq 0) {
        $engines = @($counter.CounterSamples | Where-Object {
            $_.Path -match "(?i)gpu engine\(pid_$ProcessId`_"
        })
    }
    $engineValues = @($engines | ForEach-Object { [double]$_.CookedValue })
    $dedicated = @($counter.CounterSamples | Where-Object {
        $_.Path -match '(?i)GPU Adapter Memory.*Dedicated Usage$'
    } | ForEach-Object { [double]$_.CookedValue })
    $shared = @($counter.CounterSamples | Where-Object {
        $_.Path -match '(?i)GPU Adapter Memory.*Shared Usage$'
    } | ForEach-Object { [double]$_.CookedValue })
    $activeEngines = @($engines | Where-Object CookedValue -gt 0.1 |
        Sort-Object CookedValue -Descending | Select-Object -First 12 |
        ForEach-Object {
            [ordered]@{
                Path = $_.Path
                UtilizationPercent = [double]$_.CookedValue
            }
        })
    [ordered]@{
        CpuUtilityPercent = [double]$cpu.PercentProcessorUtility
        CpuPerformancePercent = [double]$cpu.PercentProcessorPerformance
        CpuMaximumFrequencyPercent = [double]$cpu.PercentofMaximumFrequency
        CpuFrequencyMHz = [double]$cpu.ProcessorFrequency
        GpuEngineCount = $engines.Count
        GpuEngineMaximumPercent = if ($engineValues.Count) {
            ($engineValues | Measure-Object -Maximum).Maximum
        }
        else { $null }
        GpuEngineSumPercent = if ($engineValues.Count) {
            ($engineValues | Measure-Object -Sum).Sum
        }
        else { $null }
        GpuDedicatedMemoryBytes = if ($dedicated.Count) {
            ($dedicated | Measure-Object -Sum).Sum
        }
        else { $null }
        GpuSharedMemoryBytes = if ($shared.Count) {
            ($shared | Measure-Object -Sum).Sum
        }
        else { $null }
        ActiveGpuEngines = $activeEngines
    }
}

function Get-UserAbort {
    if (-not $script:userAbortPath -or
        -not [IO.File]::Exists($script:userAbortPath)) {
        return $null
    }
    $text = try {
        [IO.File]::ReadAllText($script:userAbortPath).Trim()
    }
    catch {
        "unreadable abort file: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($text)) {
        $text = 'requested'
    }
    if ($text.Length -gt 512) {
        $text = $text.Substring(0, 512)
    }
    $text
}

function Invoke-DrainMonitoring {
    param(
        [Parameter(Mandatory = $true)] $Guard,
        [Parameter(Mandatory = $true)][string] $PhaseId,
        [Parameter(Mandatory = $true)][string] $MonitorPath
    )

    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $DrainSeconds) {
        $userAbort = Get-UserAbort
        if ($null -ne $userAbort) {
            throw "User abort during drain: $userAbort"
        }
        if ([IO.File]::Exists([string]$Guard.AbortPath)) {
            throw "Telemetry guard abort during drain: $([IO.File]::ReadAllText($Guard.AbortPath))"
        }
        Assert-HeartbeatHealthy
        Assert-FanControlIdentity
        $events = @(Get-NewRelevantEvents)
        if ($events.Count) {
            Write-DurableJsonLine -Path $MonitorPath -Value ([ordered]@{
                Kind = 'relevant-events'
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                PhaseId = $PhaseId
                Phase = 'drain'
                Events = $events
            })
            throw 'A relevant Windows event appeared during drain.'
        }
        $dumpChanges = @(Get-LiveKernelChanges)
        if ($dumpChanges.Count) {
            throw 'The LiveKernelReports inventory changed during drain.'
        }
        Write-DurableJsonLine -Path $MonitorPath -Value ([ordered]@{
            Kind = 'drain-sample'
            Utc = [DateTimeOffset]::UtcNow.ToString('o')
            PhaseId = $PhaseId
            ElapsedSeconds = $clock.Elapsed.TotalSeconds
        })
        Start-Sleep -Seconds 1
    }
}

function Invoke-CinebenchPhase {
    param(
        [Parameter(Mandatory = $true)] $Phase,
        [Parameter(Mandatory = $true)][int] $Attempt
    )

    $phaseId = [string]$Phase.Id
    $phaseRoot = Join-Path (Join-Path $script:outputRoot 'phases') $phaseId
    [IO.Directory]::CreateDirectory($phaseRoot) | Out-Null
    $phaseDirectory = Join-Path $phaseRoot ("attempt-{0:D2}" -f $Attempt)
    if ([IO.Directory]::Exists($phaseDirectory) -or
        [IO.File]::Exists($phaseDirectory)) {
        throw "Phase attempt path already exists: $phaseDirectory"
    }
    [IO.Directory]::CreateDirectory($phaseDirectory) | Out-Null
    $monitorPath = Join-Path $phaseDirectory 'monitor.jsonl'
    $summaryPath = Join-Path $phaseDirectory 'summary.json'
    $redshiftBefore = @(Get-RedshiftLogInventory)
    Write-DurableJsonFile -Path (Join-Path $phaseDirectory `
        'redshift-baseline.json') -Value $redshiftBefore -CreateNew
    $guard = $null
    $workload = $null
    $workloadStartTicks = $null
    $workloadStartUtc = $null
    $workloadEndUtc = $null
    $watchdog = $null
    $watchdogOutcome = $null
    $watchdogRecords = @()
    $phaseFailure = $null
    $phaseAbortReason = $null
    $phaseSummary = $null
    $archive = $null

    Write-Journal -Kind 'phase-start' -Data @{
        PhaseId = $phaseId
        Cycle = [int]$Phase.Cycle
        Workload = [string]$Phase.Workload
        Attempt = $Attempt
        Directory = $phaseDirectory
    }

    try {
        Assert-HeartbeatHealthy
        Assert-FanControlIdentity
        $guard = Start-TelemetryGuard -PhaseDirectory $phaseDirectory `
            -PhaseId $phaseId
        Write-Journal -Kind 'guard-start' -Data @{
            PhaseId = $phaseId
            ProcessId = $guard.Identity.Id
            LedgerPath = $guard.LedgerPath
            AbortPath = $guard.AbortPath
        }
        Wait-GuardReady $guard
        Write-Journal -Kind 'guard-ready' -Data @{ PhaseId = $phaseId }

        $stdout = Join-Path $phaseDirectory 'cinebench.stdout.log'
        $stderr = Join-Path $phaseDirectory 'cinebench.stderr.log'
        $testArgument = if ($Phase.Workload -eq 'gpu') {
            'g_cinebenchGPUTest=true'
        }
        else {
            'g_cinebenchCpuXTest=true'
        }
        $rawArguments = @(
            $testArgument,
            "g_cinebenchMinimumTestDuration=$MinimumPhaseSeconds",
            'g_acceptDisclaimer=true',
            'g_console=true',
            'g_consoleWaitOnEnd=false'
        )
        $arguments = @($rawArguments | ForEach-Object {
            ConvertTo-QuotedArgument ([string]$_)
        })
        $workload = Start-Process -FilePath $script:cinebenchExe `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($script:cinebenchExe)) `
            -ArgumentList $arguments -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $workload.EnableRaisingEvents = $true
        $null = $workload.Handle
        $workloadIdentity = Get-ProcessIdentity $workload
        $workloadStartTicks = [long]$workloadIdentity.StartTimeUtcTicks
        $workloadStartUtc = $workload.StartTime.ToUniversalTime()
        $deadline = [datetime]::UtcNow.AddSeconds($HardPhaseSeconds)
        $watchdog = Start-PhaseWatchdog -Workload $workload `
            -WorkloadStartTicks $workloadStartTicks -Deadline $deadline `
            -PhaseDirectory $phaseDirectory -PhaseId $phaseId
        Write-Journal -Kind 'workload-start' -Data @{
            PhaseId = $phaseId
            Process = $workloadIdentity
            Arguments = $rawArguments
            DeadlineUtc = $deadline.ToString('o')
            WatchdogProcessId = $watchdog.Process.Id
        }
        Wait-PhaseWatchdogReady $watchdog
        Write-Journal -Kind 'workload-watchdog-ready' -Data @{
            PhaseId = $phaseId
            Process = $watchdog.Identity
            LedgerPath = $watchdog.LedgerPath
        }

        $clock = [Diagnostics.Stopwatch]::StartNew()
        $sequence = 0
        $activeSamples = 0
        $highSamples = 0
        $consecutivePerformanceErrors = 0
        $lastCpuSeconds = 0.0
        $lastProgressSeconds = 0.0
        $nextEventCheck = 0.0
        $nextDumpCheck = 0.0
        while ($true) {
            $sampleStart = $clock.Elapsed.TotalSeconds
            $workload.Refresh()
            if ($workload.HasExited) {
                break
            }
            if ($sampleStart -ge $HardPhaseSeconds) {
                $phaseAbortReason = 'hard-phase-deadline'
                throw "Cinebench exceeded the $HardPhaseSeconds-second hard deadline."
            }
            $userAbort = Get-UserAbort
            if ($null -ne $userAbort) {
                $phaseAbortReason = 'user-abort'
                throw "User abort: $userAbort"
            }
            if ([IO.File]::Exists([string]$guard.AbortPath)) {
                $phaseAbortReason = 'telemetry-guard-abort'
                throw "Telemetry guard abort: $([IO.File]::ReadAllText($guard.AbortPath))"
            }
            $guard.Process.Refresh()
            if ($guard.Process.HasExited) {
                [void]$guard.Process.WaitForExit()
                $phaseAbortReason = 'telemetry-guard-exited'
                throw "Telemetry guard exited unexpectedly with code $($guard.Process.ExitCode)."
            }
            Assert-HeartbeatHealthy
            Assert-PhaseWatchdogHealthy $watchdog
            Assert-SoleCinebenchProcess `
                -ExpectedProcessId $workload.Id `
                -ExpectedStartTimeUtcTicks $workloadStartTicks
            Assert-FanControlIdentity

            $newEvents = @()
            if ($sampleStart -ge $nextEventCheck) {
                $newEvents = @(Get-NewRelevantEvents)
                $nextEventCheck = $sampleStart + 5
                if ($newEvents.Count) {
                    $phaseAbortReason = 'relevant-windows-event'
                }
            }
            $dumpChanges = @()
            if ($sampleStart -ge $nextDumpCheck) {
                $dumpChanges = @(Get-LiveKernelChanges)
                $nextDumpCheck = $sampleStart + 10
                if ($dumpChanges.Count) {
                    $phaseAbortReason = 'livekernel-report-change'
                }
            }

            $performance = $null
            $performanceError = $null
            try {
                $performance = Get-PerformanceSnapshot -ProcessId $workload.Id
                $consecutivePerformanceErrors = 0
            }
            catch {
                $consecutivePerformanceErrors++
                $performanceError = $_.Exception.Message
            }
            if ($consecutivePerformanceErrors -ge 3) {
                $phaseAbortReason = 'performance-telemetry-failed'
            }

            $workload.Refresh()
            $cpuSeconds = $workload.TotalProcessorTime.TotalSeconds
            $workingSet = $workload.WorkingSet64
            $responding = try { [bool]$workload.Responding } catch { $null }
            $progress = $cpuSeconds -gt ($lastCpuSeconds + 0.01)
            if ($performance -and
                $null -ne $performance.GpuEngineMaximumPercent -and
                [double]$performance.GpuEngineMaximumPercent -gt 1) {
                $progress = $true
            }
            if ($progress) {
                $lastProgressSeconds = $sampleStart
                $lastCpuSeconds = $cpuSeconds
            }

            $high = $false
            if ($performance -and $sampleStart -ge $LoadWarmupSeconds) {
                $activeSamples++
                if ($Phase.Workload -eq 'cpu') {
                    $high = [double]$performance.CpuUtilityPercent -ge
                        $CpuLoadThresholdPercent
                }
                elseif ($null -ne $performance.GpuEngineMaximumPercent) {
                    $high = [double]$performance.GpuEngineMaximumPercent -ge
                        $GpuLoadThresholdPercent
                }
                if ($high) {
                    $highSamples++
                }
            }

            Write-DurableJsonLine -Path $monitorPath -Value ([ordered]@{
                Kind = 'active-sample'
                Sequence = $sequence
                Utc = [DateTimeOffset]::UtcNow.ToString('o')
                PhaseId = $phaseId
                ElapsedSeconds = $sampleStart
                Process = [ordered]@{
                    Id = $workload.Id
                    CpuSeconds = $cpuSeconds
                    WorkingSetBytes = $workingSet
                    Responding = $responding
                }
                Performance = $performance
                PerformanceError = $performanceError
                HighLoad = $high
                RelevantEvents = $newEvents
                LiveKernelChanges = $dumpChanges
            })

            if ($newEvents.Count) {
                throw 'A relevant Windows event appeared during the phase.'
            }
            if ($dumpChanges.Count) {
                throw 'The LiveKernelReports inventory changed during the phase.'
            }
            if ($consecutivePerformanceErrors -ge 3) {
                throw 'Performance telemetry failed three consecutive times.'
            }
            if ($sampleStart -ge $LoadWarmupSeconds -and
                ($sampleStart - $lastProgressSeconds) -ge
                    $ProgressStallSeconds) {
                $phaseAbortReason = 'workload-progress-stall'
                throw "Cinebench made no observed progress for $ProgressStallSeconds seconds."
            }

            $sequence++
            $remaining = 1000 -
                [int][Math]::Floor(($clock.Elapsed.TotalSeconds - $sampleStart) * 1000)
            if ($remaining -gt 0) {
                Start-Sleep -Milliseconds $remaining
            }
        }

        [void]$workload.WaitForExit()
        $workload.Refresh()
        $workloadEndUtc = $workload.ExitTime.ToUniversalTime()
        if (@(Get-Process -Name Cinebench -ErrorAction SilentlyContinue).Count `
                -ne 0) {
            $phaseAbortReason = 'unexpected-cinebench-process-after-exit'
            throw 'A Cinebench process remained or appeared after workload exit.'
        }
        $runtimeSeconds = $clock.Elapsed.TotalSeconds
        $exitCode = $workload.ExitCode
        $watchdogOutcome = Complete-PhaseWatchdog $watchdog
        $watchdog = $null
        $watchdogRecords = @($watchdogOutcome.Records)
        $deadlineRecord = @($watchdogRecords | Where-Object Kind -eq
            'deadline-enforced')
        if ($deadlineRecord.Count) {
            $phaseAbortReason = 'independent-watchdog-deadline'
            throw 'The independent watchdog enforced the workload deadline.'
        }
        if ([string]$watchdogOutcome.TerminalKind -cne 'workload-exited') {
            $phaseAbortReason = 'invalid-watchdog-terminal'
            throw (
                "The independent watchdog terminal was " +
                "'$($watchdogOutcome.TerminalKind)'.")
        }
        if ($exitCode -ne 0) {
            $phaseAbortReason = 'cinebench-nonzero-exit'
            throw "Cinebench exited with code $exitCode."
        }
        if ($runtimeSeconds -lt $MinimumPhaseSeconds) {
            $phaseAbortReason = 'cinebench-exited-before-minimum'
            throw (
                "Cinebench exited after $runtimeSeconds seconds, before the " +
                "$MinimumPhaseSeconds-second minimum.")
        }
        if ($activeSamples -lt 10) {
            $phaseAbortReason = 'insufficient-load-samples'
            throw "Only $activeSamples post-warmup load samples were captured."
        }
        $highFraction = $highSamples / [double]$activeSamples
        if ($highFraction -lt $MinimumHighLoadFraction) {
            $phaseAbortReason = 'high-load-fraction-too-low'
            throw (
                "Only $highSamples/$activeSamples load samples met the " +
                "$MinimumHighLoadFraction required fraction.")
        }

        Invoke-DrainMonitoring -Guard $guard -PhaseId $phaseId `
            -MonitorPath $monitorPath
        $archive = Archive-ChangedRedshiftLogs -Before $redshiftBefore `
            -DestinationDirectory (Join-Path $phaseDirectory 'redshift-logs') `
            -WorkloadStartUtc $workloadStartUtc `
            -WorkloadEndUtc $workloadEndUtc `
            -WorkloadProcessId $workloadIdentity.Id `
            -WorkloadType ([string]$Phase.Workload)
        if (@($archive.Files).Count -eq 0 -or
            $archive.ValidCompletionEvidenceCount -lt 1 -or
            $archive.RenderEndCount -lt 1) {
            $phaseAbortReason = 'redshift-completion-evidence-missing'
            throw (
                'No phase-delta Redshift session from the exact Cinebench ' +
                'process/time window had paired Render Begin/End markers.')
        }
        if ($Phase.Workload -eq 'gpu' -and $archive.Block15Count -lt 1) {
            $phaseAbortReason = 'gpu-block-completion-evidence-missing'
            throw 'The GPU Redshift log did not contain a Block 15/15 marker.'
        }

        Stop-TelemetryGuard $guard
        $guard = $null

        $phaseSummary = [ordered]@{
            Status = 'completed'
            PhaseId = $phaseId
            Cycle = [int]$Phase.Cycle
            Workload = [string]$Phase.Workload
            Attempt = $Attempt
            RuntimeSeconds = $runtimeSeconds
            ExitCode = $exitCode
            ActiveSamples = $activeSamples
            HighSamples = $highSamples
            HighLoadFraction = $highFraction
            Redshift = $archive
            WatchdogRecords = $watchdogRecords
            WatchdogOutcome = $watchdogOutcome
        }
        Write-DurableJsonFile -Path $summaryPath -Value $phaseSummary `
            -CreateNew
        Write-Journal -Kind 'phase-complete' -Data @{
            PhaseId = $phaseId
            Attempt = $Attempt
            SummaryPath = $summaryPath
            RuntimeSeconds = $runtimeSeconds
            HighLoadFraction = $highFraction
        }
        $phaseSummary
    }
    catch {
        $phaseFailure = $_.Exception.ToString()
        $workloadCleanupProven = $null -eq $workload
        if (-not $phaseAbortReason) {
            $phaseAbortReason = 'phase-error'
        }
        if ($workload -and $null -ne $workloadStartTicks) {
            try {
                Stop-ExactProcess -Process $workload `
                    -StartTimeUtcTicks ([long]$workloadStartTicks) `
                    -Description "Cinebench $phaseId"
                $workloadCleanupProven = -not (Test-ExactProcessAlive `
                    -ProcessId $workload.Id `
                    -StartTimeUtcTicks ([long]$workloadStartTicks))
            }
            catch {
                $phaseFailure +=
                    "`nWorkload cleanup failed: $($_.Exception.Message)"
            }
        }
        if ($watchdog) {
            if (-not $workloadCleanupProven) {
                $watchdogOutcome = [ordered]@{
                    TerminalKind = 'left-armed-after-workload-cleanup-failure'
                    Process = $watchdog.Identity
                    DeadlineUtc = $watchdog.DeadlineUtc
                    LedgerPath = $watchdog.LedgerPath
                }
                $phaseFailure +=
                    "`nExact workload cleanup was not proven; the independent " +
                    "watchdog remains armed through $($watchdog.DeadlineUtc)."
                $watchdog = $null
            }
            else {
                try {
                    $watchdogOutcome = Complete-PhaseWatchdog $watchdog
                    $watchdogRecords = @($watchdogOutcome.Records)
                    $watchdog = $null
                }
                catch {
                    $phaseFailure +=
                        "`nWatchdog cleanup failed: $($_.Exception.Message)"
                }
            }
        }
        if ($null -eq $archive) {
            try {
                $archiveArguments = @{
                    Before = $redshiftBefore
                    DestinationDirectory =
                        (Join-Path $phaseDirectory 'redshift-logs')
                }
                if ($workload -and $workloadStartUtc) {
                    $workload.Refresh()
                    if ($workload.HasExited) {
                        $workloadEndUtc = $workload.ExitTime.ToUniversalTime()
                    }
                    else {
                        $workloadEndUtc = [datetime]::UtcNow
                    }
                    $archiveArguments.WorkloadStartUtc = $workloadStartUtc
                    $archiveArguments.WorkloadEndUtc = $workloadEndUtc
                    $archiveArguments.WorkloadProcessId = $workload.Id
                    $archiveArguments.WorkloadType = [string]$Phase.Workload
                }
                $archive = Archive-ChangedRedshiftLogs @archiveArguments
            }
            catch {
                $archive = [ordered]@{
                    ArchiveError = $_.Exception.Message
                }
            }
        }
        $phaseSummary = [ordered]@{
            Status = 'failed'
            PhaseId = $phaseId
            Cycle = [int]$Phase.Cycle
            Workload = [string]$Phase.Workload
            Attempt = $Attempt
            AbortReason = $phaseAbortReason
            Error = $phaseFailure
            Redshift = $archive
            WatchdogRecords = $watchdogRecords
            WatchdogOutcome = $watchdogOutcome
            GuardAbort = if ($guard -and
                [IO.File]::Exists([string]$guard.AbortPath)) {
                [IO.File]::ReadAllText([string]$guard.AbortPath)
            }
            else { $null }
        }
        Write-DurableJsonFile -Path $summaryPath -Value $phaseSummary `
            -CreateNew
        Write-Journal -Kind 'phase-failed' -Data @{
            PhaseId = $phaseId
            Attempt = $Attempt
            AbortReason = $phaseAbortReason
            Error = $phaseFailure
            SummaryPath = $summaryPath
        }
        throw $phaseFailure
    }
    finally {
        if ($guard) {
            try { Stop-TelemetryGuard $guard }
            catch {
                Write-Journal -Kind 'guard-cleanup-error' -Data @{
                    PhaseId = $phaseId
                    Error = $_.Exception.Message
                }
            }
        }
    }
}

function Get-FanControlLogBaseline {
    if (-not [IO.File]::Exists($script:fanControlLogPath)) {
        throw "Fan Control log is missing: $script:fanControlLogPath"
    }
    $bytes = [IO.File]::ReadAllBytes($script:fanControlLogPath)
    [ordered]@{
        Path = $script:fanControlLogPath
        Length = $bytes.Length
        Sha256 = Get-BytesSha256 -Bytes $bytes -Count $bytes.Length
        CapturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
}

function Capture-NewFanControlLog {
    if (-not [IO.File]::Exists($script:fanControlLogPath)) {
        throw "Fan Control log disappeared: $script:fanControlLogPath"
    }
    $bytes = [IO.File]::ReadAllBytes($script:fanControlLogPath)
    $offset = 0
    $continuity = $false
    if ($bytes.Length -ge [int]$script:fanControlLogBaseline.Length) {
        $prefixHash = Get-BytesSha256 -Bytes $bytes `
            -Count ([int]$script:fanControlLogBaseline.Length)
        if ($prefixHash -ceq [string]$script:fanControlLogBaseline.Sha256) {
            $offset = [int]$script:fanControlLogBaseline.Length
            $continuity = $true
        }
    }
    $delta = [byte[]]::new($bytes.Length - $offset)
    [Array]::Copy($bytes, $offset, $delta, 0, $delta.Length)
    $destination = Join-Path $script:sessionDirectory `
        'fancontrol-log-phase-delta.txt'
    Write-DurableBytes -Path $destination -Bytes $delta
    $text = [Text.Encoding]::UTF8.GetString($delta)
    $failureLines = @($text -split '\r?\n' | Where-Object {
        $_ -match '(?i)Minisforum UM780 XTX' -and
        $_ -match (
            '(?i)fail|fault|disabled|exception|timed out|ambiguous|' +
            'incomplete|restoration remains pending')
    })
    [ordered]@{
        Source = $script:fanControlLogPath
        BaselineLength = [long]$script:fanControlLogBaseline.Length
        BaselineContinuityVerified = $continuity
        DeltaOffset = $offset
        DeltaLength = $delta.Length
        DeltaPath = $destination
        DeltaSha256 = Get-BytesSha256 -Bytes $delta -Count $delta.Length
        PluginFailureLines = $failureLines
    }
}

function Invoke-IndependentStockAudit {
    $stdout = Join-Path $script:sessionDirectory 'stock-audit.stdout.log'
    $stderr = Join-Path $script:sessionDirectory 'stock-audit.stderr.log'
    $process = Start-Process -FilePath $script:diagnosticsExecutable `
        -ArgumentList @('stock') `
        -WorkingDirectory ([IO.Path]::GetDirectoryName(
            $script:diagnosticsExecutable)) `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.EnableRaisingEvents = $true
    $null = $process.Handle
    $identity = Get-ProcessIdentity $process
    if (-not $process.WaitForExit(30000)) {
        Stop-ExactProcess -Process $process `
            -StartTimeUtcTicks ([long]$identity.StartTimeUtcTicks) `
            -Description 'independent stock-state diagnostic'
        throw 'The independent stock-state diagnostic exceeded 30 seconds.'
    }
    [void]$process.WaitForExit()
    $outText = if ([IO.File]::Exists($stdout)) {
        [IO.File]::ReadAllText($stdout)
    }
    else { '' }
    $errText = if ([IO.File]::Exists($stderr)) {
        [IO.File]::ReadAllText($stderr)
    }
    else { '' }
    if ($process.ExitCode -ne 0 -or
        $outText -notmatch '(?m)^Stock state validated;' -or
        $outText -notmatch '(?m)^PASS stock \(') {
        throw (
            "Independent stock audit failed with exit $($process.ExitCode). " +
            "stdout: $outText stderr: $errText")
    }
    [ordered]@{
        Process = $identity
        ExitCode = $process.ExitCode
        StdoutPath = $stdout
        StderrPath = $stderr
        StdoutSha256 = (Get-FileHash -LiteralPath $stdout `
            -Algorithm SHA256).Hash
        StderrSha256 = (Get-FileHash -LiteralPath $stderr `
            -Algorithm SHA256).Hash
        ExactB1AndFirmwareOwnedSystemVerified = $true
    }
}

function Invoke-CampaignCleanup {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    try {
        Assert-FanControlIdentity
        Load-FanControlConfig -ConfigPath $script:disabledConfig `
            -Label 'campaign-cleanup-disabled'
        $settled = Assert-DisabledControlSettled
        Write-Journal -Kind 'controls-disabled-via-fancontrol' -Data @{
            State = $settled
            IndependentStockVerification = $false
        }
    }
    catch {
        $cleanupErrors.Add(
            "Control restoration failed: $($_.Exception.Message)")
    }

    if ($LeaveFanControlRunning) {
        try {
            $logEvidence = Capture-NewFanControlLog
            Write-Journal -Kind 'fancontrol-log-evidence' -Data @{
                Evidence = $logEvidence
            }
            if (-not [bool]$logEvidence.BaselineContinuityVerified) {
                throw 'Fan Control log continuity from the session baseline was lost.'
            }
            if (@($logEvidence.PluginFailureLines).Count) {
                throw (
                    'New UM780 plugin failure lines appeared in Fan Control log: ' +
                    (@($logEvidence.PluginFailureLines) -join ' | '))
            }
        }
        catch {
            $cleanupErrors.Add(
                "Fan Control log verification failed: $($_.Exception.Message)")
        }
        Write-Journal -Kind 'stock-verification-skipped' -Data @{
            Reason = 'LeaveFanControlRunning was requested.'
            IndependentlyVerified = $false
        }
    }
    else {
        $fanControlExited = $false
        try {
            $wasAlive = Test-ExactProcessAlive `
                    -ProcessId ([int]$script:fanControlIdentity.Id) `
                    -StartTimeUtcTicks (
                        [long]$script:fanControlIdentity.StartTimeUtcTicks)
            if ($wasAlive) {
                $output = Join-Path $script:sessionDirectory 'ipc-exit.json'
                $null = Invoke-Ipc -Command 'exit' -OutputPath $output
                $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
                while ([DateTimeOffset]::UtcNow -lt $deadline -and
                    (Test-ExactProcessAlive `
                        -ProcessId ([int]$script:fanControlIdentity.Id) `
                        -StartTimeUtcTicks (
                            [long]$script:fanControlIdentity.StartTimeUtcTicks))) {
                    Start-Sleep -Milliseconds 250
                }
            }
            else {
                $cleanupErrors.Add(
                    'Fan Control exited unexpectedly before orderly cleanup.')
            }
            if (Test-ExactProcessAlive `
                    -ProcessId ([int]$script:fanControlIdentity.Id) `
                    -StartTimeUtcTicks (
                        [long]$script:fanControlIdentity.StartTimeUtcTicks)) {
                throw 'Fan Control did not exit normally within 15 seconds.'
            }
            $fanControlExited = $true
            Write-Journal -Kind 'fancontrol-exited' -Data @{
                WasAliveAtCleanup = $wasAlive
                OrderlyExitRequested = $wasAlive
            }
        }
        catch {
            $cleanupErrors.Add(
                "Fan Control orderly exit failed: $($_.Exception.Message)")
        }
        if (-not $fanControlExited -and
            -not (Test-ExactProcessAlive `
                -ProcessId ([int]$script:fanControlIdentity.Id) `
                -StartTimeUtcTicks (
                    [long]$script:fanControlIdentity.StartTimeUtcTicks))) {
            $fanControlExited = $true
            Write-Journal -Kind 'fancontrol-exit-observed-after-error'
        }
        if ($fanControlExited -and
            @(Get-Process -Name FanControl -ErrorAction SilentlyContinue).Count `
                -ne 0) {
            $fanControlExited = $false
            $cleanupErrors.Add(
                'A Fan Control process is still/rather running; stock audit refused.')
        }
        if ($fanControlExited) {
            try {
                $logEvidence = Capture-NewFanControlLog
                Write-Journal -Kind 'fancontrol-log-evidence' -Data @{
                    Evidence = $logEvidence
                }
                if (-not [bool]$logEvidence.BaselineContinuityVerified) {
                    throw 'Fan Control log continuity from the session baseline was lost.'
                }
                if (@($logEvidence.PluginFailureLines).Count) {
                    throw (
                        'New UM780 plugin failure lines appeared in Fan Control log: ' +
                        (@($logEvidence.PluginFailureLines) -join ' | '))
                }
            }
            catch {
                $cleanupErrors.Add(
                    "Fan Control log verification failed: $($_.Exception.Message)")
            }
            try {
                $stockAudit = Invoke-IndependentStockAudit
                $script:stockRestorationVerified = $true
                Write-Journal -Kind 'stock-restoration-independently-verified' `
                    -Data @{ Audit = $stockAudit }
            }
            catch {
                Write-Journal -Kind 'stock-restoration-verification-failed' `
                    -Data @{
                        Error = $_.Exception.Message
                        StdoutPath = Join-Path $script:sessionDirectory `
                            'stock-audit.stdout.log'
                        StderrPath = Join-Path $script:sessionDirectory `
                            'stock-audit.stderr.log'
                    }
                $cleanupErrors.Add(
                    "Independent stock audit failed: $($_.Exception.Message)")
            }
        }
    }
    $cleanupErrors.ToArray()
}

function Get-CampaignProgress {
    param(
        [Parameter(Mandatory = $true)][object[]] $Phases,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]] $JournalRecords
    )

    $known = @{}
    foreach ($phase in $Phases) {
        $known[[string]$phase.Id] = $true
    }
    $completed = @{}
    foreach ($record in @($JournalRecords | Where-Object {
            [string]$_.Kind -eq 'phase-complete'
        })) {
        $phaseId = [string]$record.Data.PhaseId
        if (-not $known.ContainsKey($phaseId)) {
            throw "Journal completed unknown phase '$phaseId'."
        }
        $completed[$phaseId] = $true
    }
    $next = @($Phases | Where-Object {
        -not $completed.ContainsKey([string]$_.Id)
    } | Select-Object -First 1)
    [ordered]@{
        CompletedPhaseIds = $completed
        NextPhase = if ($next.Count -eq 1) { $next[0] } else { $null }
        AllPhasesComplete = $next.Count -eq 0
    }
}

function Test-EquivalentPlanParameter {
    param(
        [AllowNull()][object] $Saved,
        [AllowNull()][object] $Requested
    )

    if ($null -eq $Saved -or $null -eq $Requested) {
        return $null -eq $Saved -and $null -eq $Requested
    }
    if ($Saved -is [bool] -or $Requested -is [bool]) {
        return $Saved -is [bool] -and
            $Requested -is [bool] -and
            [bool]$Saved -eq [bool]$Requested
    }

    $numericTypeCodes = @(
        [TypeCode]::Byte,
        [TypeCode]::SByte,
        [TypeCode]::UInt16,
        [TypeCode]::UInt32,
        [TypeCode]::UInt64,
        [TypeCode]::Int16,
        [TypeCode]::Int32,
        [TypeCode]::Int64,
        [TypeCode]::Decimal,
        [TypeCode]::Double,
        [TypeCode]::Single)
    $savedTypeCode = [Type]::GetTypeCode($Saved.GetType())
    $requestedTypeCode = [Type]::GetTypeCode($Requested.GetType())
    if ($savedTypeCode -in $numericTypeCodes -and
        $requestedTypeCode -in $numericTypeCodes) {
        $savedDouble = [Convert]::ToDouble($Saved, $invariant)
        $requestedDouble = [Convert]::ToDouble($Requested, $invariant)
        $scale = [Math]::Max(
            1.0,
            [Math]::Max([Math]::Abs($savedDouble),
                [Math]::Abs($requestedDouble)))
        return [Math]::Abs($savedDouble - $requestedDouble) -le
            (1.0e-12 * $scale)
    }

    return [string]::Equals(
        [string]$Saved, [string]$Requested,
        [StringComparison]::Ordinal)
}

function Invoke-StaticSelfValidation {
    for ($code = 0; $code -le 51; $code++) {
        $rawPercent = $code * 100.0 / 51.0
        $roundTrippedPercent = ([pscustomobject]@{
                Value = $rawPercent
            } | ConvertTo-Json | ConvertFrom-Json).Value
        if (-not (Test-EquivalentPlanParameter -Saved $roundTrippedPercent `
                -Requested $rawPercent)) {
            throw (
                'Static validation rejected JSON-round-tripped raw code ' +
                "$code.")
        }
    }
    if (Test-EquivalentPlanParameter -Saved (10 * 100.0 / 51.0) `
            -Requested (11 * 100.0 / 51.0)) {
        throw 'Static validation accepted a different raw CPU fan code.'
    }

    $historical = $utf8NoBom.GetBytes(
        "historical Renderer: Render Begin Block 15/15 Renderer: Render End`n")
    $baseline = [ordered]@{
        Path = 'Log.Latest.0\log.html'
        Length = $historical.Length
        Sha256 = Get-BytesSha256 -Bytes $historical `
            -Count $historical.Length
    }
    $windowStart = [datetime]::UtcNow.AddMinutes(-1)
    $windowEnd = [datetime]::UtcNow.AddMinutes(1)
    $sessionLocal = [TimeZoneInfo]::ConvertTimeFromUtc(
        [datetime]::UtcNow, [TimeZoneInfo]::Local).ToString(
            'yyyy-MM-dd HH:mm:ss', $invariant)
    $phaseText =
        "Application Path: $script:cinebenchExe`n" +
        "Session: $sessionLocal`n" +
        'Renderer: Render Begin Block 15/15 Renderer: Render End'
    $phaseBytes = $utf8NoBom.GetBytes($phaseText)
    $combined = [byte[]]::new($historical.Length + $phaseBytes.Length)
    [Array]::Copy($historical, 0, $combined, 0, $historical.Length)
    [Array]::Copy(
        $phaseBytes, 0, $combined, $historical.Length, $phaseBytes.Length)
    $offset = Get-RedshiftDeltaOffset -CurrentBytes $combined `
        -Before @($baseline)
    if ($offset -ne $historical.Length) {
        throw 'Static validation failed to strip a rotated Redshift baseline.'
    }
    $evidence = @(Get-RedshiftSessionEvidence -Text $phaseText `
        -WorkloadStartUtc $windowStart -WorkloadEndUtc $windowEnd `
        -ExpectedApplicationPath $script:cinebenchExe `
        -WorkloadProcessId 1234 -WorkloadType 'gpu')
    if ($evidence.Count -ne 1 -or
        -not [bool]$evidence[0].ValidCompletionEvidence) {
        throw 'Static validation rejected phase/process/time-bounded Redshift evidence.'
    }
    $stale = @(Get-RedshiftSessionEvidence -Text $phaseText `
        -WorkloadStartUtc $windowStart.AddDays(2) `
        -WorkloadEndUtc $windowEnd.AddDays(2) `
        -ExpectedApplicationPath $script:cinebenchExe `
        -WorkloadProcessId 1234 -WorkloadType 'gpu')
    if ($stale.Count -ne 1 -or
        [bool]$stale[0].ValidCompletionEvidence) {
        throw 'Static validation accepted out-of-window Redshift evidence.'
    }
    $torn = ConvertFrom-ResumableJsonLines -Lines @(
        '{"Sequence":0}',
        '{"Sequence":') -Description 'static torn-tail sample'
    if (@($torn.Records).Count -ne 1 -or
        $null -eq $torn.TornFinalLine -or
        [int]$torn.TornFinalLine.LineNumber -ne 2) {
        throw 'Static validation did not report exactly one torn final JSONL line.'
    }
    $interiorRejected = $false
    try {
        $null = ConvertFrom-ResumableJsonLines -Lines @(
            '{"Sequence":',
            '{"Sequence":1}') -Description 'static interior-corruption sample'
    }
    catch {
        $interiorRejected = $true
    }
    if (-not $interiorRejected) {
        throw 'Static validation accepted interior JSONL corruption.'
    }
    $samplePhases = @(
        [pscustomobject]@{ Id = 'phase-a' },
        [pscustomobject]@{ Id = 'phase-b' })
    $sampleRecords = @(
        [pscustomobject]@{
            Kind = 'phase-complete'
            Data = [pscustomobject]@{ PhaseId = 'phase-a' }
        },
        [pscustomobject]@{
            Kind = 'phase-complete'
            Data = [pscustomobject]@{ PhaseId = 'phase-b' }
        })
    $progress = Get-CampaignProgress -Phases $samplePhases `
        -JournalRecords $sampleRecords
    if (-not [bool]$progress.AllPhasesComplete -or
        $null -ne $progress.NextPhase -or
        $progress.CompletedPhaseIds.Count -ne 2) {
        throw 'Static validation did not select all-phases cleanup-only recovery.'
    }
}

$script:repoRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
$script:outputRoot = Get-FullPath $OutputDirectory
$script:fanControlRoot = Get-FullPath $FanControlDirectory
$script:cinebenchExe = Get-FullPath $CinebenchPath
$script:activeConfig = Get-FullPath $ActiveConfigPath
$script:disabledConfig = Get-FullPath $DisabledConfigPath
$script:ipcExecutable = Get-FullPath (Join-Path $script:repoRoot `
    'diagnostics\FanControlIpc\bin\Release\net10.0-windows\FanControlIpc.exe')
$script:diagnosticsExecutable = Get-FullPath (Join-Path $script:repoRoot `
    'diagnostics\bin\Release\net10.0-windows\FanControl.MinisforumUM780XTX.Diagnostics.exe')
$script:ipcAssembly = Get-FullPath (Join-Path $script:fanControlRoot `
    'FanControl.IPC.dll')
$script:fanControlLogPath = Get-FullPath (Join-Path $script:fanControlRoot `
    'log.txt')
$script:heartbeatScript = Get-FullPath (Join-Path $PSScriptRoot `
    'CinebenchCampaignHeartbeat.ps1')
$script:watchdogScript = Get-FullPath (Join-Path $PSScriptRoot `
    'CinebenchPhaseWatchdog.ps1')
$script:redshiftLogRoot = if ([string]::IsNullOrWhiteSpace(
        $RedshiftLogDirectory)) {
    Get-FullPath (Join-Path $env:APPDATA `
        'MAXON\Cinebench2026_win_x86_64_EA999F11\Redshift\Log')
}
else {
    Get-FullPath $RedshiftLogDirectory
}
$script:userAbortPath = if ([string]::IsNullOrWhiteSpace($UserAbortPath)) {
    Join-Path $script:outputRoot 'user.abort'
}
else {
    Get-FullPath $UserAbortPath
}
$script:journalPath = Join-Path $script:outputRoot 'campaign-journal.jsonl'
$planPath = Join-Path $script:outputRoot 'campaign-plan.json'
$pluginPath = Get-FullPath (Join-Path $script:fanControlRoot `
    'Plugins\MinisforumUM780XTX\FanControl.MinisforumUM780XTX.dll')
$cinebenchModule = Get-FullPath (Join-Path `
    ([IO.Path]::GetDirectoryName($script:cinebenchExe)) `
    'corelibs\cinebench.xdl64')

foreach ($requiredFile in @(
        $script:cinebenchExe,
        $cinebenchModule,
        $script:activeConfig,
        $script:disabledConfig,
        $script:ipcExecutable,
        $script:diagnosticsExecutable,
        $script:ipcAssembly,
        $script:heartbeatScript,
        $script:watchdogScript,
        $pluginPath)) {
    if (-not [IO.File]::Exists($requiredFile)) {
        throw "Required file is missing: $requiredFile"
    }
}
if (-not [IO.File]::Exists($script:fanControlLogPath)) {
    throw "Required Fan Control log is missing: $script:fanControlLogPath"
}
Assert-CampaignConfig -Path $script:activeConfig -Active $true
Assert-CampaignConfig -Path $script:disabledConfig -Active $false
$machineIdentity = Get-MachineIdentity
Assert-ExactMachine $machineIdentity
Invoke-StaticSelfValidation

$phases = [Collections.Generic.List[object]]::new()
for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    foreach ($workload in @('gpu', 'cpu')) {
        $phases.Add([ordered]@{
            Id = ('cycle-{0:D2}-{1}' -f $cycle, $workload)
            Cycle = $cycle
            Workload = $workload
        })
    }
}

$records = [ordered]@{
    Cinebench = Get-FileRecord $script:cinebenchExe
    CinebenchModule = Get-FileRecord $cinebenchModule
    Plugin = Get-FileRecord $pluginPath
    FanControlIpc = Get-FileRecord $script:ipcAssembly
    IpcHelper = Get-FileRecord $script:ipcExecutable
    StockDiagnostics = Get-FileRecord $script:diagnosticsExecutable
    ActiveConfig = Get-FileRecord $script:activeConfig
    DisabledConfig = Get-FileRecord $script:disabledConfig
    HeartbeatScript = Get-FileRecord $script:heartbeatScript
    WatchdogScript = Get-FileRecord $script:watchdogScript
}
$requestedPlan = [ordered]@{
    SchemaVersion = 1
    Kind = 'FanControl.MinisforumUM780XTX.CinebenchSoakCampaign'
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    MachineIdentity = $machineIdentity
    Parameters = [ordered]@{
        Cycles = $Cycles
        MinimumPhaseSeconds = $MinimumPhaseSeconds
        HardPhaseSeconds = $HardPhaseSeconds
        DrainSeconds = $DrainSeconds
        CpuCode = $CpuCode
        CpuControlPercent = $CpuCode * 100.0 / 51.0
        CpuMaximumC = $CpuMaximumC
        SystemMaximumC = $SystemMaximumC
        GpuMaximumC = $GpuMaximumC
        DimmMaximumC = $DimmMaximumC
        CpuLoadThresholdPercent = $CpuLoadThresholdPercent
        GpuLoadThresholdPercent = $GpuLoadThresholdPercent
        MinimumHighLoadFraction = $MinimumHighLoadFraction
        LoadWarmupSeconds = $LoadWarmupSeconds
        ProgressStallSeconds = $ProgressStallSeconds
        LeaveFanControlRunning = [bool]$LeaveFanControlRunning
    }
    Paths = [ordered]@{
        OutputDirectory = $script:outputRoot
        FanControlDirectory = $script:fanControlRoot
        CinebenchPath = $script:cinebenchExe
        ActiveConfigPath = $script:activeConfig
        DisabledConfigPath = $script:disabledConfig
        RedshiftLogDirectory = $script:redshiftLogRoot
        UserAbortPath = $script:userAbortPath
    }
    Files = $records
    ExpectedControls = [ordered]@{
        CpuIdentifier = $cpuControlId
        CpuNativeCode = $CpuCode
        SystemIdentifier = $systemControlId
        SystemMode = 'disabled-firmware-owned'
        CleanupVerification = if ($LeaveFanControlRunning) {
            'Fan Control disabled/null only; independent stock audit intentionally skipped.'
        }
        else {
            'Orderly Fan Control exit followed by diagnostics stock PASS is required.'
        }
        CriticalRowNote =
            'The untouched firmware critical row may command maximum fan at 94 C.'
    }
    Phases = $phases.ToArray()
}

if ($DryRun) {
    [pscustomobject]@{
        Status = 'DryRunValidatedNoProcessesStarted'
        MachineIdentity = $machineIdentity
        PhaseCount = $phases.Count
        MinimumWorkloadSeconds = $phases.Count * $MinimumPhaseSeconds
        Plan = $requestedPlan
        StaticSelfValidation = 'Passed'
        Note =
            'No directory, configuration, Fan Control RPC, helper, or Cinebench process was created or started.'
    }
    return
}

Assert-Elevated
if ([IO.File]::Exists($script:userAbortPath) -or
    [IO.Directory]::Exists($script:userAbortPath)) {
    throw "UserAbortPath must not exist before the session: $script:userAbortPath"
}
if (@(Get-Process -Name Cinebench -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close every existing Cinebench process before starting or resuming.'
}

$script:sessionId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
$script:journalSequence = 0
$priorRecords = @()
$plan = $null
if ($Resume) {
    if (-not [IO.Directory]::Exists($script:outputRoot) -or
        -not [IO.File]::Exists($planPath) -or
        -not [IO.File]::Exists($script:journalPath)) {
        throw 'Resume requires an existing campaign plan and journal.'
    }
    $plan = [IO.File]::ReadAllText($planPath) | ConvertFrom-Json
    if ([int]$plan.SchemaVersion -ne 1 -or
        [string]$plan.Kind -ne
            'FanControl.MinisforumUM780XTX.CinebenchSoakCampaign') {
        throw 'The existing campaign plan kind or schema is unsupported.'
    }
    foreach ($name in $requestedPlan.Parameters.Keys) {
        $savedProperty = $plan.Parameters.PSObject.Properties[$name]
        if ($null -eq $savedProperty) {
            throw "Resume plan is missing parameter '$name'."
        }
        if (-not (Test-EquivalentPlanParameter `
                -Saved $savedProperty.Value `
                -Requested $requestedPlan.Parameters[$name])) {
            throw "Resume parameter '$name' differs from the saved plan."
        }
    }
    foreach ($name in $records.Keys) {
        if ([string]$plan.Files.$name.Sha256 -ne
            [string]$records[$name].Sha256) {
            throw "Resume file hash '$name' differs from the saved plan."
        }
    }
    foreach ($name in $requestedPlan.Paths.Keys) {
        if ([string]$plan.Paths.$name -ne
            [string]$requestedPlan.Paths[$name]) {
            throw "Resume path '$name' differs from the saved plan."
        }
    }
    $journalRead = Read-ResumableJsonLines $script:journalPath
    $priorRecords = @($journalRead.Records)
    $tailRecovery = $null
    if ($null -ne $journalRead.TornFinalLine) {
        $tailRecovery = Repair-TornJournalTail `
            -Path $script:journalPath -Records $priorRecords `
            -TornFinalLine $journalRead.TornFinalLine
    }
    if ($priorRecords.Count) {
        $script:journalSequence =
            [int](($priorRecords | Measure-Object -Property Sequence `
                -Maximum).Maximum) + 1
    }
    if ($tailRecovery) {
        Write-Journal -Kind 'journal-torn-final-line-recovered' -Data @{
            Recovery = $tailRecovery
        }
    }
    if (@($priorRecords | Where-Object Kind -eq 'campaign-complete').Count) {
        [pscustomobject]@{
            Status = 'AlreadyComplete'
            OutputDirectory = $script:outputRoot
        }
        return
    }
}
else {
    if ([IO.File]::Exists($script:outputRoot) -or
        [IO.Directory]::Exists($script:outputRoot)) {
        throw "New campaign output path must not exist: $script:outputRoot"
    }
    [IO.Directory]::CreateDirectory($script:outputRoot) | Out-Null
    [IO.Directory]::CreateDirectory(
        (Join-Path $script:outputRoot 'phases')) | Out-Null
    [IO.Directory]::CreateDirectory(
        (Join-Path $script:outputRoot 'sessions')) | Out-Null
    Write-DurableJsonFile -Path $planPath -Value $requestedPlan -CreateNew
    $plan = [IO.File]::ReadAllText($planPath) | ConvertFrom-Json
    Write-Journal -Kind 'campaign-created' -Data @{
        PlanPath = $planPath
        PlanSha256 = (Get-FileHash -LiteralPath $planPath `
            -Algorithm SHA256).Hash
    }
}

$progress = Get-CampaignProgress -Phases @($plan.Phases) `
    -JournalRecords $priorRecords
$completedPhaseIds = $progress.CompletedPhaseIds
$cleanupOnlyRecovery = $Resume -and [bool]$progress.AllPhasesComplete
$incompleteId = if ($cleanupOnlyRecovery) {
    $null
}
else {
    [string]$progress.NextPhase.Id
}
$previousStarts = if ($cleanupOnlyRecovery) {
    @()
}
else {
    @($priorRecords | Where-Object {
        $_.Kind -eq 'phase-start' -and
        [string]$_.Data.PhaseId -eq $incompleteId
    })
}
$previousFailures = @($priorRecords | Where-Object {
    $_.Kind -in @('phase-failed', 'campaign-failed')
})
$previousStarts = @($previousStarts)
$previousFailures = @($previousFailures)
$lastSession = @($priorRecords | Where-Object Kind -eq 'session-start' |
    Select-Object -Last 1)
$currentBoot = Get-BootIdentity
$bootChanged = $lastSession.Count -eq 1 -and
    [string]$lastSession[0].Data.BootIdentity.LastBootUpTimeUtc -ne
        [string]$currentBoot.LastBootUpTimeUtc
$needsAcknowledgement =
    $previousStarts.Count -ne 0 -or $previousFailures.Count -ne 0 -or
    $bootChanged -or $cleanupOnlyRecovery
if ($Resume -and $needsAcknowledgement -and
    -not $AcknowledgeInterruptedPhase) {
    if ($cleanupOnlyRecovery) {
        throw (
            'All workload phases are durable, but campaign completion is ' +
            'missing or torn. Inspect the journal, then rerun with -Resume ' +
            '-AcknowledgeInterruptedPhase to repeat disabled cleanup and the ' +
            'independent stock audit before recreating campaign-complete.')
    }
    else {
        throw (
            "Campaign has an interrupted/failed phase or boot change. Inspect it, " +
            'then rerun with -Resume -AcknowledgeInterruptedPhase to retry the ' +
            "incomplete phase '$incompleteId'.")
    }
}
if ($Resume -and $AcknowledgeInterruptedPhase) {
    Write-Journal -Kind 'interruption-acknowledged' -Data @{
        IncompletePhaseId = $incompleteId
        PreviousStartCount = $previousStarts.Count
        PreviousFailureCount = $previousFailures.Count
        BootChanged = $bootChanged
        CurrentBootIdentity = $currentBoot
        CleanupOnlyRecovery = $cleanupOnlyRecovery
    }
}

$script:sessionDirectory = Join-Path (Join-Path $script:outputRoot 'sessions') `
    $script:sessionId
[IO.Directory]::CreateDirectory($script:sessionDirectory) | Out-Null
$script:powerShellPath = Get-CurrentPowerShellPath
$script:runnerStartTimeUtcTicks = (Get-Process -Id $PID `
    -ErrorAction Stop).StartTime.ToUniversalTime().Ticks
$script:fanControlIdentity = Get-FanControlIdentity
$script:fanControlLogBaseline = Get-FanControlLogBaseline
$eventBaselines = Get-EventRecordBaselines
$script:eventCursors = @{
    System = [long]$eventBaselines.System
    Application = [long]$eventBaselines.Application
}
$dumpBaseline = @(Get-LiveKernelInventory)
$script:dumpBaselineMap = Convert-InventoryToMap $dumpBaseline
Write-Journal -Kind 'session-start' -Data @{
    BootIdentity = $currentBoot
    FanControlIdentity = $script:fanControlIdentity
    FanControlLogBaseline = $script:fanControlLogBaseline
    EventRecordBaselines = $eventBaselines
    LiveKernelInventory = $dumpBaseline
    SessionDirectory = $script:sessionDirectory
    Resume = [bool]$Resume
}
$script:heartbeat = Start-Heartbeat
Write-Journal -Kind 'heartbeat-start' -Data @{
    ProcessId = $script:heartbeat.Process.Id
    LedgerPath = $script:heartbeat.LedgerPath
}
Wait-HeartbeatReady
Write-Journal -Kind 'heartbeat-ready' -Data @{
    Process = $script:heartbeat.Identity
    LedgerPath = $script:heartbeat.LedgerPath
}

$campaignFailure = $null
$cleanupErrors = @()
$campaignCompleted = $false
$script:stockRestorationVerified = $false
try {
    Assert-HeartbeatHealthy
    if ($cleanupOnlyRecovery) {
        Write-Journal -Kind 'completion-recovery-cleanup-start' -Data @{
            CompletedPhases = $completedPhaseIds.Count
            ExpectedPhases = @($plan.Phases).Count
            ActiveConfigurationReloaded = $false
        }
    }
    else {
        Load-FanControlConfig -ConfigPath $script:activeConfig `
            -Label 'campaign-active'
        $settled = Assert-ActiveControlSettled
        Write-Journal -Kind 'active-control-settled' -Data @{
            State = $settled
            CpuNativeCode = $CpuCode
            SystemMode = 'disabled-firmware-owned'
        }

        foreach ($phase in $plan.Phases) {
            $phaseId = [string]$phase.Id
            if ($completedPhaseIds.ContainsKey($phaseId)) {
                continue
            }
            $phaseRoot = Join-Path (Join-Path $script:outputRoot 'phases') $phaseId
            $attempt = if ([IO.Directory]::Exists($phaseRoot)) {
                @(Get-ChildItem -LiteralPath $phaseRoot -Directory `
                    -Filter 'attempt-*').Count + 1
            }
            else {
                1
            }
            $null = Invoke-CinebenchPhase -Phase $phase -Attempt $attempt
            $completedPhaseIds[$phaseId] = $true
        }
    }
    $campaignCompleted = $true
}
catch {
    $campaignFailure = $_.Exception.ToString()
    Write-Journal -Kind 'campaign-failed' -Data @{
        Error = $campaignFailure
    }
}
finally {
    $cleanupErrors = @(Invoke-CampaignCleanup)
    try { Stop-Heartbeat }
    catch {
        $cleanupErrors += "Heartbeat cleanup failed: $($_.Exception.Message)"
    }
    if ($cleanupErrors.Count) {
        Write-Journal -Kind 'campaign-cleanup-failed' -Data @{
            Errors = $cleanupErrors
        }
    }
}

if ($campaignCompleted -and -not $campaignFailure -and
    $cleanupErrors.Count -eq 0) {
    Write-Journal -Kind 'campaign-complete' -Data @{
        CompletedPhases = $completedPhaseIds.Count
        ExpectedPhases = @($plan.Phases).Count
        CleanupOnlyRecovery = $cleanupOnlyRecovery
        StockRestorationIndependentlyVerified =
            [bool]$script:stockRestorationVerified
    }
    [pscustomobject]@{
        Status = if ($cleanupOnlyRecovery) {
            'RecoveredCompletionAfterCleanupAndStockAudit'
        }
        else { 'Completed' }
        OutputDirectory = $script:outputRoot
        CompletedPhases = $completedPhaseIds.Count
        FanControlLeftRunning = [bool]$LeaveFanControlRunning
        StockRestorationIndependentlyVerified =
            [bool]$script:stockRestorationVerified
    }
    return
}

$parts = [Collections.Generic.List[string]]::new()
if ($campaignFailure) {
    $parts.Add($campaignFailure)
}
foreach ($cleanupError in $cleanupErrors) {
    $parts.Add([string]$cleanupError)
}
throw ($parts -join [Environment]::NewLine)
