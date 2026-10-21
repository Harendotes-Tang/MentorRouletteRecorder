#Requires -Version 7.0
<#
JSON stdin dispatcher for tooling callers that need the same Windows job lifecycle
as package verification. Request: executable, arguments[], working_directory,
timeout_seconds. The child inherits the caller's environment; Qt/PATH isolation is
specific to New-IsolatedPackageStartInfo and is deliberately not applied here.
#>
$ErrorActionPreference = 'Stop'
try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    . (Join-Path $PSScriptRoot 'package-runtime.ps1')
    $request = [Console]::In.ReadToEnd() | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    if ($null -eq $request -or [string]::IsNullOrWhiteSpace($request.executable)) {
        throw 'A JSON request with an executable is required.'
    }
    if ($request.arguments -isnot [System.Collections.IList]) { throw 'arguments must be a JSON array.' }
    if ([string]::IsNullOrWhiteSpace($request.working_directory)) { throw 'working_directory is required.' }
    $timeout = [int]$request.timeout_seconds
    if ($timeout -lt 1 -or $timeout -gt 7200) { throw 'timeout_seconds must be between 1 and 7200.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = [string]$request.executable
    $info.WorkingDirectory = [string]$request.working_directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $request.arguments) {
        if ($argument -isnot [string]) { throw 'Each argument must be a string.' }
        $info.ArgumentList.Add($argument)
    }
    $operation = Start-PackageVerificationProcess -StartInfo $info -TimeoutSeconds $timeout
    try { $result = $operation.Wait() }
    finally { $operation.Dispose() }
    # External output is nested as strings, never emitted before this envelope.
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress))
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
