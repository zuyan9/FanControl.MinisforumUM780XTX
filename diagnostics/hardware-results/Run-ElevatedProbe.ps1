[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Executable,

    [Parameter(Mandatory = $true)]
    [string[]] $ProbeArguments,

    [string] $AdditionalProbeArgument,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [string] $ExitCodePath
)

$ErrorActionPreference = 'Stop'
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$allProbeArguments = @($ProbeArguments)
if ($AdditionalProbeArgument) {
    $allProbeArguments += $AdditionalProbeArgument
}

& $Executable @allProbeArguments *>&1 |
    Tee-Object -FilePath $OutputPath
$probeExitCode = $LASTEXITCODE
Set-Content -LiteralPath $ExitCodePath -Value $probeExitCode -Encoding ASCII
exit $probeExitCode
