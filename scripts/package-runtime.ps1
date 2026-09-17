#Requires -Version 5.1

function ConvertTo-PackageProcessArgument([AllowEmptyString()][string]$Value) {
    # ProcessStartInfo.ArgumentList is unavailable on Windows PowerShell 5.1.
    # Quote for the Windows argv parser, including quotes and trailing backslashes.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function New-IsolatedPackageStartInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string[]]$Arguments = @(),
        [switch]$Offscreen
    )

    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $info.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
    $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-PackageProcessArgument $_ }) -join ' ')

    # Only the child environment changes, so build.ps1's MinGW PATH and developer
    # Qt/QML overrides cannot supply files that are absent from the package.
    try {
        $childEnvironment = $info.get_EnvironmentVariables()
    }
    catch [System.ArgumentException] {
        # A developer shell can export both Path and PATH. The first getter builds
        # the dictionary, then throws on the duplicate name; the second call returns
        # the dictionary it already created, which is then rebuilt below.
        $childEnvironment = $info.get_EnvironmentVariables()
    }
    $childEnvironment.Clear()
    $inherited = [Environment]::GetEnvironmentVariables('Process')
    foreach ($name in $inherited.Keys) { $childEnvironment[$name] = [string]$inherited[$name] }
    $childEnvironment['PATH'] = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    ) -join ';'
    foreach ($name in @($childEnvironment.Keys)) {
        if ($name -match '^(QT_|QML_|QML2_)' -or $name -in @('QTDIR', 'QT6DIR')) {
            $childEnvironment.Remove($name)
        }
    }
    if ($Offscreen) { $childEnvironment['QT_QPA_PLATFORM'] = 'offscreen' }
    return $info
}
