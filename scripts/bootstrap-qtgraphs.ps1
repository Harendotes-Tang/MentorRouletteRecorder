#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$QtPrefix,
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$CMakeExecutable,
    [Parameter(Mandatory)][string]$NinjaExecutable,
    [Parameter(Mandatory)][string]$DesktopDirectory,
    [string]$TestDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'qtgraphs-runtime.ps1')
$context = Get-QtGraphsContext -QtPrefix $QtPrefix -CompilerPath $CompilerPath `
    -CMakeExecutable $CMakeExecutable -NinjaExecutable $NinjaExecutable
$destinations = @($DesktopDirectory)
if ($TestDirectory) { $destinations += $TestDirectory }
$receipt = Install-QtGraphsRuntime -Context $context -DestinationDirectory $destinations -Prepare
Write-Host ('QtGraphs {0} project runtime verified: {1}' -f $receipt.qt_version, $receipt.dll_sha256)
