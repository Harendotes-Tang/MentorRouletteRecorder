#Requires -Version 7.0
<#
.SYNOPSIS
    Run the local dependency audit or read the current commit's GitHub security gates.
.DESCRIPTION
    Local is safe for ordinary PR tokens: it does not call privileged GitHub APIs.
    PrePush reads existing alerts without requiring them to close before a fixing push.
    PostPush, Merge and Release require current-commit CI. Merge/Release additionally
    require Dependabot and every configured security scan. Missing/unauthorized results
    remain visible and are never represented as PASS. This script performs no remote write.
    Each invocation gets its own output directory unless OutputDir is explicitly supplied.
.EXAMPLE
    pwsh -File scripts/security-quality.ps1 -Phase Local
    pwsh -File scripts/security-quality.ps1 -Phase PrePush
    pwsh -File scripts/security-quality.ps1 -Phase Release -SkipLocal
#>
[CmdletBinding()]
param(
    [ValidateSet('Local', 'PrePush', 'PostPush', 'Merge', 'Release')]
    [string]$Phase = 'Local',
    [string]$Commit,
    [string]$Repository,
    [string]$OutputDir,
    # Remote-only readback. The report explicitly records that local audit was NOT_RUN.
    [switch]$SkipLocal,
    # Add required capabilities; this cannot remove the repository's mandatory scans.
    [ValidateSet('dependabot', 'code-scanning', 'secret-scanning')]
    [string[]]$RequiredRemoteScan = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pythonName = if ($env:MR_PYTHON_EXE) { $env:MR_PYTHON_EXE } else { 'python' }
$python = Get-Command $pythonName -ErrorAction SilentlyContinue
if (-not $python -and -not $env:MR_PYTHON_EXE) {
    $python = Get-Command python3 -ErrorAction SilentlyContinue
}
if (-not $python) { throw 'Python 3 is required for the security-quality gate.' }
$phaseArgument = switch ($Phase) {
    'PrePush' { 'pre-push' }
    'PostPush' { 'post-push' }
    default { $Phase.ToLowerInvariant() }
}
$arguments = @('-X', 'utf8', '-B', (Join-Path $repoRoot 'tools/security-quality/check.py'),
    '--root', $repoRoot, '--phase', $phaseArgument)
if ($Commit) { $arguments += @('--commit', $Commit) }
if ($Repository) { $arguments += @('--repository', $Repository) }
if ($OutputDir) { $arguments += @('--output-dir', $OutputDir) }
if ($SkipLocal) { $arguments += '--skip-local' }
foreach ($scan in $RequiredRemoteScan) { $arguments += @('--required-scan', $scan) }
& $python.Source @arguments
exit $LASTEXITCODE
