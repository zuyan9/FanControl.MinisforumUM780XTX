[CmdletBinding()]
param(
    [string] $OutputDirectory
)

<#
.SYNOPSIS
Configures automatic memory-dump selection for the UM780 XTX reproduction run.

.DESCRIPTION
On the exact reviewed UM780 XTX, changes only CrashDumpEnabled from 3 to 7.
The script requires an already system-managed pagefile and more than 32 GiB of
physical memory. It does not change pagefile, TDR, manual-crash, dump-path, or
restart behavior. A reboot is required after the successful change.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$hardwareResultsRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'hardware-results'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $hardwareResultsRoot `
        'reproduction-dump-capture'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $resolvedOutput.StartsWith(
        $hardwareResultsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain below $hardwareResultsRoot"
}
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$runId = '{0}-{1}' -f (
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')),
    $PID
$statusPath = Join-Path $resolvedOutput "status-$runId.log"
$backupPath = Join-Path $resolvedOutput "backup-$runId.json"

function Write-DurableText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Text,

        [switch] $Append
    )

    $mode = if ($Append) {
        [IO.FileMode]::Append
    } else {
        [IO.FileMode]::CreateNew
    }
    $stream = [IO.FileStream]::new(
        $Path,
        $mode,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::WriteThrough)
    $writer = [IO.StreamWriter]::new($stream, $utf8NoBom, 4096, $true)
    try {
        $writer.Write($Text)
        $writer.Flush()
        $stream.Flush($true)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-Status {
    param([Parameter(Mandatory = $true)][string] $Message)

    Write-DurableText -Path $statusPath -Append -Text (
        '{0} {1}{2}' -f [DateTimeOffset]::UtcNow.ToString('o'),
        $Message,
        [Environment]::NewLine)
}

function Get-RequiredRegistryValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    try {
        Get-ItemPropertyValue -LiteralPath $Path -Name $Name
    }
    catch {
        throw "Required registry value is unavailable: $Path\\$Name"
    }
}

Write-DurableText -Path $statusPath -Text (
    '{0} START pid={1}{2}' -f [DateTimeOffset]::UtcNow.ToString('o'),
    $PID,
    [Environment]::NewLine)

try {
    $principal = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this configurator from an elevated PowerShell session.'
    }

    $biosPath = 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
    $identity = [ordered]@{
        Product = [string](Get-RequiredRegistryValue $biosPath `
            'SystemProductName')
        Board = [string](Get-RequiredRegistryValue $biosPath `
            'BaseBoardProduct')
        BoardVersion = [string](Get-RequiredRegistryValue $biosPath `
            'BaseBoardVersion')
        BiosVersion = [string](Get-RequiredRegistryValue $biosPath `
            'BIOSVersion')
        EcMajor = [int](Get-RequiredRegistryValue $biosPath `
            'ECFirmwareMajorRelease')
        EcMinor = [int](Get-RequiredRegistryValue $biosPath `
            'ECFirmwareMinorRelease')
    }
    $identityMatches =
        [string]::Equals(
            $identity.Product, 'Venus series', [StringComparison]::Ordinal) -and
        [string]::Equals(
            $identity.Board, 'F7BSD', [StringComparison]::Ordinal) -and
        [string]::Equals(
            $identity.BoardVersion, '1.1', [StringComparison]::Ordinal) -and
        [string]::Equals(
            $identity.BiosVersion, '1.06', [StringComparison]::Ordinal) -and
        $identity.EcMajor -eq 0 -and $identity.EcMinor -eq 8
    if (-not $identityMatches) {
        throw (
            'Expected Venus series/F7BSD revision 1.1, BIOS 1.06, EC 0.8; ' +
            "found $($identity.Product)/$($identity.Board) revision " +
            "$($identity.BoardVersion), BIOS $($identity.BiosVersion), EC " +
            "$($identity.EcMajor).$($identity.EcMinor).")
    }

    $computerSystem = Get-CimInstance Win32_ComputerSystem
    if ($computerSystem.AutomaticManagedPagefile -ne $true) {
        throw (
            'The Windows pagefile is not already system-managed. Refusing to ' +
            'change crash-dump configuration; this script never changes pagefiles.')
    }
    $physicalMemoryBytes = [uint64]$computerSystem.TotalPhysicalMemory
    $thirtyTwoGiB = [uint64](32 * 1GB)
    if ($physicalMemoryBytes -le $thirtyTwoGiB) {
        throw (
            'This narrow automatic-dump change is only intended for the reviewed ' +
            'configuration with more than 32 GiB of physical memory.')
    }

    $crashControlPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
    $crashControlKey = Get-Item -LiteralPath $crashControlPath
    $valueKind = $crashControlKey.GetValueKind('CrashDumpEnabled')
    $previousValue = [int](Get-RequiredRegistryValue $crashControlPath `
        'CrashDumpEnabled')
    if ($valueKind -ne [Microsoft.Win32.RegistryValueKind]::DWord) {
        throw "CrashDumpEnabled is $valueKind, not the required DWORD type."
    }
    if ($previousValue -ne 3) {
        throw (
            "CrashDumpEnabled is $previousValue, not 3. Refusing any change " +
            'other than the reviewed 3-to-7 transition.')
    }

    $backup = [ordered]@{
        SchemaVersion = 1
        CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Purpose = 'UM780 XTX freeze-reproduction automatic dump capture'
        MachineIdentity = $identity
        Manufacturer = [string]$computerSystem.Manufacturer
        Model = [string]$computerSystem.Model
        TotalPhysicalMemoryBytes = $physicalMemoryBytes
        AutomaticManagedPagefile =
            [bool]$computerSystem.AutomaticManagedPagefile
        Registry = [ordered]@{
            Path = $crashControlPath
            Name = 'CrashDumpEnabled'
            Kind = $valueKind.ToString()
            PreviousValue = $previousValue
            RequestedValue = 7
        }
        Scope = [ordered]@{
            ChangedValue = 'CrashControl\\CrashDumpEnabled only'
            PagefileChanged = $false
            TdrChanged = $false
            ManualCrashChanged = $false
            AutomaticRestartChanged = $false
            DumpPathChanged = $false
        }
    }
    $backupJson = $backup | ConvertTo-Json -Depth 8
    Write-DurableText -Path $backupPath -Text (
        $backupJson + [Environment]::NewLine)
    $verifiedBackup = [IO.File]::ReadAllText($backupPath) | ConvertFrom-Json
    if ([int]$verifiedBackup.Registry.PreviousValue -ne 3 -or
        [int]$verifiedBackup.Registry.RequestedValue -ne 7 -or
        $verifiedBackup.AutomaticManagedPagefile -ne $true) {
        throw 'The durable pre-change backup did not verify.'
    }
    $backupHash = (Get-FileHash -LiteralPath $backupPath `
        -Algorithm SHA256).Hash
    Write-Status "BACKUP_VERIFIED path=$backupPath sha256=$backupHash"

    Set-ItemProperty -LiteralPath $crashControlPath `
        -Name 'CrashDumpEnabled' -Value 7

    $verifiedKey = Get-Item -LiteralPath $crashControlPath
    $verifiedKind = $verifiedKey.GetValueKind('CrashDumpEnabled')
    $verifiedValue = [int](Get-RequiredRegistryValue $crashControlPath `
        'CrashDumpEnabled')
    if ($verifiedKind -ne [Microsoft.Win32.RegistryValueKind]::DWord -or
        $verifiedValue -ne 7) {
        throw (
            'CrashDumpEnabled did not verify as DWORD 7 after the requested change.')
    }

    Write-Status (
        'COMPLETE CrashDumpEnabled=7 pagefile=system-managed ' +
        'reboot_required=true')
    [pscustomobject]@{
        Status = 'Configured'
        PreviousCrashDumpEnabled = $previousValue
        CurrentCrashDumpEnabled = $verifiedValue
        AutomaticManagedPagefile = $true
        TotalPhysicalMemoryBytes = $physicalMemoryBytes
        BackupPath = $backupPath
        BackupSha256 = $backupHash
        StatusPath = $statusPath
        RebootRequired = $true
        Note =
            'No pagefile, TDR, manual-crash, dump-path, or restart setting was changed.'
    }
}
catch {
    try {
        Write-Status "ERROR $($_.Exception.Message)"
    }
    catch {
    }
    throw
}
