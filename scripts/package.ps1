#Requires -Version 5.1
<#
.SYNOPSIS
    打包 MentorRecorder / Package MentorRecorder.

.DESCRIPTION
    生成自带运行时的 Windows 发布目录、zip 和 Inno Setup 安装器：
      1. 先运行 scripts/verify.ps1
      2. dotnet publish Collector（self-contained, win-x64）
      3. 复制 Desktop 可执行文件并用 windeployqt 收集 Qt 运行时
      4. 补齐 MinGW 运行时、许可证、README、docs 与校验和
      5. 断言输出中不含 Npcap、deucalion 注入载荷、合成协议档案、数据库、日志，
         也不含 FFmpeg（在线语音只播 WAV，只随包 Windows 多媒体后端）
      6. -Verify：把 zip 解到临时目录，在解包后的目录中运行
         Collector --version / --capture-doctor --json、Desktop --screenshot 与
         Desktop --speech-selftest，以此证明运行时依赖完整

    产物：artifacts/<name>/、<name>.zip、<name>.zip.sha256，可用时另出 Inno Setup 安装器。
    退出码：0 表示打包完成；非 0 表示某一步骤或断言失败。

.EXAMPLE
    pwsh -File scripts/package.ps1
    pwsh -File scripts/package.ps1 -Force -Verify
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDir = 'artifacts',

    [switch]$SkipVerify,

    # Replace only this script's exact, versioned staging directory and archive.
    [switch]$Force,

    # Unzip the artifact into a temporary directory and run both executables from it.
    [switch]$Verify,

    # Skip the Inno Setup installer even when ISCC.exe is available.
    [switch]$NoInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'package-runtime.ps1')

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$VerifyScript = Join-Path $PSScriptRoot 'verify.ps1'
$BuildScript = Join-Path $PSScriptRoot 'build.ps1'
$CollectorProject = Join-Path $RepoRoot 'src\Collector\MentorRecorder.Collector.csproj'
$DesktopExe = Join-Path $RepoRoot 'build\src\Desktop\MentorRecorder.Desktop.exe'
$DesktopManifest = Join-Path $RepoRoot 'build\src\Desktop\MentorRecorder.Desktop.exe.manifest'
# Example defaults for the documented machine; MR_QT_PREFIX / MR_MINGW_BIN override them
# (same variables as scripts/build.ps1).
$QtPrefixConfigured = [Environment]::GetEnvironmentVariable('MR_QT_PREFIX')
$QtBin = if ([string]::IsNullOrWhiteSpace($QtPrefixConfigured)) { 'D:\APPS\Qt\6.11.2\mingw_64\bin' } else { Join-Path $QtPrefixConfigured 'bin' }
$Windeployqt = Join-Path $QtBin 'windeployqt.exe'
$QmlSourceDir = Join-Path $RepoRoot 'src\Desktop\qml'
$MinGwBinConfigured = [Environment]::GetEnvironmentVariable('MR_MINGW_BIN')
$MinGwBin = if ([string]::IsNullOrWhiteSpace($MinGwBinConfigured)) { 'D:\APPS\Qt\Tools\mingw1310_64\bin' } else { $MinGwBinConfigured }
$OutputRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $RepoRoot $OutputDir }

# Directory.Build.props is the single source of truth for the version: MSBuild stamps the
# Collector assembly from it, the root CMakeLists.txt reads it for the Desktop VERSIONINFO
# resource and the --version compile definition, and the staging directory, the zip,
# BUILD-METADATA.json and ISCC's /DAppVersion are all derived from it.
$PropsPath = Join-Path $RepoRoot 'Directory.Build.props'
$Version = ([xml](Get-Content -LiteralPath $PropsPath -Raw)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) { throw '无法从 Directory.Build.props 读取 <Version>' }
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw ("Directory.Build.props 的 <Version> 不是 x.y.z: {0}" -f $Version)
}

# The artifact name carries the version so that a stale tree from an older version cannot
# survive a repackage run without -Force and be shipped as the new release.
$ArtifactName = if ($Configuration -eq 'Release') {
    "MentorRecorder-{0}-win-x64" -f $Version
} else {
    "MentorRecorder-{0}-{1}-win-x64" -f $Version, $Configuration.ToLowerInvariant()
}
$StageDir = Join-Path $OutputRoot $ArtifactName
$CollectorStage = Join-Path $OutputRoot '_collector_publish'
$ZipPath = Join-Path $OutputRoot ($ArtifactName + '.zip')
$ZipHashPath = $ZipPath + '.sha256'
$SourceNotePath = Join-Path $StageDir 'SOURCE_CODE.md'
$BuildMetadataPath = Join-Path $StageDir 'BUILD-METADATA.json'
$HashPath = Join-Path $StageDir 'SHA256SUMS.txt'
$ChangelogPath = Join-Path $RepoRoot 'CHANGELOG.md'
$InstallerScript = Join-Path $RepoRoot 'installer\MentorRecorder.iss'
$InstallerPath = Join-Path $OutputRoot ("MentorRecorder-{0}-setup.exe" -f $Version)
$IsccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$Iscc = $IsccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("{0}: {1}" -f $Label, $Path)
    }
}

function Remove-IfExists([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $fullOutput = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\'
        $fullTarget = [System.IO.Path]::GetFullPath($Path)
        if (-not $fullTarget.StartsWith($fullOutput, [StringComparison]::OrdinalIgnoreCase)) {
            throw ("拒绝删除输出目录之外的路径: {0}" -f $fullTarget)
        }

        $item = Get-Item -LiteralPath $fullTarget -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ("拒绝递归删除重解析点: {0}" -f $fullTarget)
        }

        Remove-Item -LiteralPath $fullTarget -Recurse -Force
    }
}

function Assert-ReplaceAllowed([string[]]$Paths) {
    $existing = @($Paths | Where-Object { Test-Path -LiteralPath $_ })
    if ($existing.Count -gt 0 -and -not $Force) {
        throw ("输出已存在；请换一个 -OutputDir，或显式使用 -Force 只替换以下目标:`n{0}" -f
            ($existing -join [Environment]::NewLine))
    }
}

function Add-MinGwRuntime([string]$TargetDir) {
    foreach ($name in 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll') {
        $source = Join-Path $MinGwBin $name
        $target = Join-Path $TargetDir $name
        Assert-File $source '缺少 MinGW 运行时'
        if (-not (Test-Path -LiteralPath $target)) {
            Copy-Item -LiteralPath $source -Destination $target
        }
    }
}

function Add-OffscreenPlatformPlugin([string]$TargetDir) {
    # windeployqt follows the interactive import graph and installs qwindows only. The
    # documented --screenshot mode selects qoffscreen before QApplication is constructed,
    # so that plugin is a runtime dependency as well.
    $source = Join-Path (Split-Path -Parent $QtBin) 'plugins\platforms\qoffscreen.dll'
    $platformDir = Join-Path $TargetDir 'platforms'
    Assert-File $source '缺少 Qt offscreen 平台插件'
    New-Item -ItemType Directory -Path $platformDir -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $platformDir 'qoffscreen.dll') -Force
}

# Online speech plays the Collector's 16-bit PCM WAV files with QSoundEffect, which the
# Windows Media Foundation backend handles alone (main.cpp sets QT_MEDIA_BACKEND=windows).
# The FFmpeg backend is therefore not shipped: windeployqt runs with --no-ffmpeg
# --exclude-plugins ffmpegmediaplugin, and these patterns fail the package if a Qt update
# starts deploying them another way.
$ForbiddenMediaPatterns = @(
    'ffmpegmediaplugin*'
    'avcodec-*.dll'
    'avformat-*.dll'
    'avutil-*.dll'
    'avdevice-*.dll'
    'avfilter-*.dll'
    'swresample-*.dll'
    'swscale-*.dll'
    'postproc-*.dll'
)

function Test-ForbiddenMediaFile([System.IO.FileInfo]$File) {
    foreach ($pattern in $ForbiddenMediaPatterns) {
        if ($File.Name -like $pattern) { return $true }
    }
    return $false
}

function Assert-MultimediaLayout([string]$TargetDir) {
    # Exactly one multimedia backend, the Windows one, and nothing FFmpeg anywhere.
    $pluginDir = Join-Path $TargetDir 'multimedia'
    $plugins = @(Get-ChildItem -LiteralPath $pluginDir -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
    $expected = @('windowsmediaplugin.dll')
    $unexpected = @($plugins | Where-Object { $expected -notcontains $_ })
    $missing = @($expected | Where-Object { $plugins -notcontains $_ })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw ("multimedia 插件目录应当只有 {0}；实际: {1}" -f ($expected -join ', '), ($plugins -join ', '))
    }
    $ffmpeg = @(Get-ChildItem -LiteralPath $TargetDir -Recurse -File | Where-Object { Test-ForbiddenMediaFile $_ })
    if ($ffmpeg.Count -gt 0) {
        throw ("打包输出包含 FFmpeg 文件:`n{0}" -f (($ffmpeg | ForEach-Object { $_.FullName }) -join [Environment]::NewLine))
    }
    Write-Host ("  多媒体后端: multimedia\{0}；无 FFmpeg。" -f ($plugins -join ', ')) -ForegroundColor Green
}

function New-SilentWave([string]$Path) {
    # Canonical 44-byte RIFF/WAVE header plus 0.3 s of 24 kHz mono 16-bit silence: the
    # format the Collector writes into tts-cache\.
    $sampleRate = 24000
    $dataBytes = [int]($sampleRate * 2 * 3 / 10)
    $stream = [System.IO.File]::Create($Path)
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        $ascii = [System.Text.Encoding]::ASCII
        $writer.Write($ascii.GetBytes('RIFF'))
        $writer.Write([uint32](36 + $dataBytes))
        $writer.Write($ascii.GetBytes('WAVEfmt '))
        $writer.Write([uint32]16)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write([uint32]$sampleRate)
        $writer.Write([uint32]($sampleRate * 2))
        $writer.Write([uint16]2)
        $writer.Write([uint16]16)
        $writer.Write($ascii.GetBytes('data'))
        $writer.Write([uint32]$dataBytes)
        $writer.Write((New-Object byte[] $dataBytes))
        $writer.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-NoForbiddenPayload([string]$TargetDir) {
    $forbidden = Get-ChildItem -LiteralPath $TargetDir -Recurse -File | Where-Object {
        # Injection payload, proprietary Oodle library, pcap runtime, game binaries and
        # raw capture files: docs/privacy-boundary.md sections 3, 4 and 7.
        $_.Name -like 'deucalion*' -or
        $_.Name -like 'oo2net*.dll' -or
        $_.Name -ieq 'wpcap.dll' -or
        $_.Name -ieq 'Packet.dll' -or
        $_.Name -like '*npcap*' -or
        $_.Name -ieq 'ffxiv.exe' -or
        $_.Name -ieq 'ffxiv_dx11.exe' -or
        $_.Extension -ieq '.pcap' -or
        $_.Extension -ieq '.pcapng' -or

        # A database or log would carry a user's recorded play history and machine
        # diagnostics; neither belongs in a package.
        $_.Extension -ieq '.db' -or
        $_.Extension -ieq '.db-wal' -or
        $_.Extension -ieq '.db-shm' -or
        $_.Extension -ieq '.sqlite' -or
        $_.Extension -ieq '.sqlite3' -or
        $_.Extension -ieq '.log' -or

        # Symbols ship embedded (Directory.Build.props sets DebugType=embedded plus PathMap
        # for Release). A loose .pdb must never be staged: portable PDBs store absolute
        # source paths segment by segment and would leak the maintainer's drive layout.
        $_.Extension -ieq '.pdb' -or

        # A .trx or coverage file means test output leaked into the staging directory.
        $_.Extension -ieq '.trx' -or

        # FFmpeg: not needed to play WAV files (see $ForbiddenMediaPatterns).
        (Test-ForbiddenMediaFile $_)
    }

    if ($forbidden) {
        $names = $forbidden | ForEach-Object { $_.FullName }
        throw ("打包输出包含被禁止的文件:`n{0}" -f ($names -join [Environment]::NewLine))
    }

    # Synthetic profiles describe no real client and must never be offered to the live
    # profile selector in an installed build.
    $synthetic = Get-ChildItem -LiteralPath $TargetDir -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '[\\/]protocol-profiles[\\/]synthetic([\\/]|$)' }
    if ($synthetic) {
        $names = $synthetic | ForEach-Object { $_.FullName }
        throw ("打包输出包含合成协议档案:`n{0}" -f ($names -join [Environment]::NewLine))
    }
}

function Invoke-StagedCollectorJson {
    # Query the packaged Collector instead of asserting a literal in this script. What each
    # field is worth:
    #
    #   packaged_verified_profile_status is derived from the artifact: --list-profiles walks
    #   the profile files that were staged, so it changes when the package changes.
    #
    #   live_capture_status and the doctor's own public_distribution_ready are not
    #   measurements but compile-time constants in the staged binary
    #   (src/Collector/Capture/CaptureDiagnostics.cs: CaptureDiagnosticsSnapshot
    #   .LiveCaptureStatus, and a literal `true`). Reading them proves only that the metadata
    #   and the shipped binary came from one build, so they must not be described as
    #   measured. The release blockers are the three that follow: no VERIFIED profile in the
    #   package, a dirty source worktree, -SkipVerify.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TargetDir,
        [Parameter(Mandatory = $true)][string[]]$CollectorArgs,
        [Parameter(Mandatory = $true)][int[]]$AllowedExitCodes,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$ProbeTimeoutSeconds = 60
    )

    $exe = Join-Path $TargetDir 'MentorRecorder.Collector.exe'
    Assert-File $exe '缺少 Collector 可执行文件'

    # Scratch data directory: the probe must not write a database or log into the artifact
    # it is describing, which Assert-NoForbiddenPayload would then reject.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("mr-pkg-probe-{0}" -f ([guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        $startInfo = New-IsolatedPackageStartInfo -Executable $exe -Arguments $CollectorArgs `
            -WorkingDirectory $TargetDir
        $childEnvironment = $startInfo.get_EnvironmentVariables()
        $childEnvironment['MR_DATA_DIR'] = $scratch
        # Shared-calibration kill switch (docs/privacy-boundary.md section 8.2): a packaging probe never downloads.
        $childEnvironment['MR_DISABLE_SHARED_FETCH'] = '1'
        # Online-speech kill switch (docs/privacy-boundary.md section 8.3): a packaging probe never speaks online.
        $childEnvironment['MR_DISABLE_ONLINE_SPEECH'] = '1'
        # Update-check kill switch (docs/privacy-boundary.md section 8.4): a packaging probe never checks for updates.
        $childEnvironment['MR_DISABLE_UPDATE_CHECK'] = '1'

        $process = [System.Diagnostics.Process]::Start($startInfo)
        try {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            # Bounded: --capture-doctor probes Npcap and looks for the game process, so a
            # wedged driver or hung enumeration would otherwise stall the packaging run
            # with no output.
            if (-not $process.WaitForExit($ProbeTimeoutSeconds * 1000)) {
                try { $process.Kill($true) } catch { }
                throw ("{0} 在 {1} 秒内没有退出，已强制结束。" -f $Label, $ProbeTimeoutSeconds)
            }
            $processExit = $process.ExitCode
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
        }
        finally {
            $process.Dispose()
        }

        if ($AllowedExitCodes -notcontains $processExit) {
            throw ("{0} 退出码 {1}（允许 {2}）:`n{3}`n{4}" -f
                $Label, $processExit, ($AllowedExitCodes -join ','), $stdout.Trim(), $stderr.Trim())
        }

        try {
            return ($stdout | ConvertFrom-Json)
        }
        catch {
            throw ("{0} 的输出不是 JSON:`n{1}" -f $Label, $stdout.Trim())
        }
    }
    finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-PackagedVerifiedProfileIds($ProfileReport) {
    # Synthetic profiles have already been removed from the stage, so this lists what an
    # installed build would offer the selector.
    if (-not $ProfileReport -or -not $ProfileReport.profiles) { return @() }
    return @($ProfileReport.profiles |
        Where-Object { $_.status -eq 'VERIFIED' -and $_.usable } |
        ForEach-Object { [string]$_.profile_id } |
        Sort-Object)
}

function Format-PackagedProfileStatus([string[]]$VerifiedIds) {
    if ($VerifiedIds.Count -eq 0) { return 'NONE' }
    return ('VERIFIED ({0})' -f ($VerifiedIds -join ', '))
}

function Assert-NoLocalPathLeak([string]$TargetDir) {
    # Assert-NoForbiddenPayload rejects a file that must not be here; this rejects a string
    # that must not be here: the maintainer's repository path embedded in a shipped binary.
    # Directory.Build.props sets PathMap so the compiler never records it, and this check
    # proves it against the artifact rather than against the setting.
    #
    # Both encodings are searched: .NET metadata and PE debug directories store UTF-8, while
    # a Qt/Win32 resource or wide-char literal stores UTF-16LE.
    #
    # Both separators are searched: MSVC and .NET record the backslash form, while CMake,
    # qmlcachegen and the rest of the Qt build write __FILE__ and generated-source paths
    # with forward slashes.
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $needleRoot = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\')
    $needleForms = [ordered]@{
        'backslash'   = $needleRoot
        'forwardslash' = $needleRoot.Replace('\', '/')
    }
    $needles = [ordered]@{}
    foreach ($form in $needleForms.Keys) {
        $value = $needleForms[$form]
        if ($form -ne 'backslash' -and $value -eq $needleForms['backslash']) { continue }
        $needles["UTF-8/$form"] = $latin1.GetString([System.Text.Encoding]::UTF8.GetBytes($value))
        $needles["UTF-16LE/$form"] =
            $latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($value))
    }

    $offenders = @()
    foreach ($file in Get-ChildItem -LiteralPath $TargetDir -Recurse -File) {
        # Documentation and checksums legitimately name the source tree, so only binaries
        # and generated metadata are scanned.
        if ($file.Extension -imatch '^\.(md|txt|json)$') { continue }

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $text = $latin1.GetString($bytes)
        foreach ($encoding in $needles.Keys) {
            if ($text.IndexOf($needles[$encoding], [StringComparison]::Ordinal) -ge 0) {
                $offenders += ('{0} ({1})' -f $file.FullName, $encoding)
            }
        }
    }

    if ($offenders.Count -gt 0) {
        throw ("发布产物内嵌了本机源码路径（{0}）；请确认 Directory.Build.props 的 PathMap/DebugType 生效:`n{1}" -f
            $needleRoot, ($offenders -join [Environment]::NewLine))
    }

    Write-Host ("  产物中不含本机源码路径（{0}）。" -f $needleRoot) -ForegroundColor Green
}

function Get-NormalizedVersion([string]$Value) {
    # FileVersion resources are four-part ("0.2.2.0"); Directory.Build.props is three-part.
    # Compare on major.minor.patch so the two shapes are commensurable.
    if (-not $Value) { return $null }
    $match = [regex]::Match($Value.Trim(), '^\s*(\d+)\.(\d+)\.(\d+)')
    if (-not $match.Success) { return $null }
    return ('{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value)
}

function Get-TopChangelogVersion([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $match = [regex]::Match($line, '^##\s*\[(\d+\.\d+\.\d+)\]')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

function Assert-StagedVersion([string]$TargetDir) {
    # Every place a version can be read off this release - both VERSIONINFO resources,
    # BUILD-METADATA.json and the top CHANGELOG section - must agree with
    # Directory.Build.props.
    $expected = Get-NormalizedVersion $Version
    $observed = [ordered]@{}

    foreach ($exe in 'MentorRecorder.Collector.exe', 'MentorRecorder.Desktop.exe') {
        $path = Join-Path $TargetDir $exe
        Assert-File $path '缺少可执行文件'
        $observed[$exe] = Get-NormalizedVersion (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    }

    $metadataPath = Join-Path $TargetDir 'BUILD-METADATA.json'
    Assert-File $metadataPath '缺少 BUILD-METADATA.json'
    $observed['BUILD-METADATA.json'] =
        Get-NormalizedVersion ((Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8) | ConvertFrom-Json).version

    $observed['CHANGELOG.md'] = Get-NormalizedVersion (Get-TopChangelogVersion $ChangelogPath)

    $mismatched = @()
    foreach ($key in $observed.Keys) {
        $value = $observed[$key]
        Write-Host ("  {0,-28} {1}" -f $key, $(if ($value) { $value } else { '(读不到 / unreadable)' }))
        if ($value -ne $expected) { $mismatched += $key }
    }

    if ($mismatched.Count -gt 0) {
        throw ("版本不一致：Directory.Build.props 是 {0}，但以下不是:`n{1}" -f
            $expected, ($mismatched -join [Environment]::NewLine))
    }

    Write-Host ("  全部与 Directory.Build.props 一致（{0}）。" -f $expected) -ForegroundColor Green
}

function Get-ChangelogSection([string]$Text, [string]$Version) {
    # The body of "## [X.Y.Z] - date" up to the next "## " heading, with line endings
    # normalised so a CRLF/LF difference between git and the working copy is not a change.
    $lines = $Text -replace "`r`n", "`n" -split "`n"
    $inside = $false
    $body = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match '^##\s*\[([^\]]+)\]') {
            if ($inside) { break }
            $inside = ($Matches[1] -eq $Version)
            continue
        }
        if ($inside) { $body.Add($line.TrimEnd()) }
    }
    if (-not $inside -and $body.Count -eq 0) { return $null }
    return ($body -join "`n").Trim()
}

function Assert-ReleasedChangelogSectionsUnchanged {
    # A published section records what a tag contains, so every "## [X.Y.Z]" section with a
    # matching vX.Y.Z tag must read exactly as it did at that tag. Newer entries belong
    # under [Unreleased] or the next version.
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) {
        Write-Host '  跳过：找不到 git，无法核对已发布的 CHANGELOG 段落。' -ForegroundColor Yellow
        return
    }
    $tags = @(& git -C $RepoRoot tag --list 'v*' 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  跳过：当前目录不是 git 仓库，无法核对已发布的 CHANGELOG 段落。' -ForegroundColor Yellow
        return
    }
    $working = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
    $changed = @()
    $checked = 0
    # git writes UTF-8; PowerShell decodes native output with the console code page, which on
    # a Chinese Windows is GBK. Read the tagged file through UTF-8 or every CJK line "differs".
    $previousEncoding = [Console]::OutputEncoding
    try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
    foreach ($tag in $tags) {
        if ($tag -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
        $version = $Matches[1]
        $current = Get-ChangelogSection $working $version
        if ($null -eq $current) { continue }   # never released with a section: nothing to protect
        $tagged = & git -C $RepoRoot show ("{0}:CHANGELOG.md" -f $tag) 2>$null
        if ($LASTEXITCODE -ne 0) { continue }  # tag predates the changelog
        $released = Get-ChangelogSection ($tagged -join "`n") $version
        if ($null -eq $released) { continue }
        $checked++
        if ($released -ne $current) { $changed += $tag }
    }
    } finally { [Console]::OutputEncoding = $previousEncoding }
    if ($changed.Count -gt 0) {
        throw (("CHANGELOG.md 里已发布的段落在打 tag 之后被修改了：{0}。" +
                "已发布段落记录的是那个 tag 的内容；新的变更请写到 [Unreleased] 或下一个版本。") -f
               ($changed -join ', '))
    }
    Write-Host ("  已核对 {0} 个已发布段落与其 tag 一致。" -f $checked) -ForegroundColor Green
}

function Assert-InstallDeleteCoverage([string]$TargetDir) {
    # [InstallDelete] in installer/MentorRecorder.iss clears the loose-file directories before
    # an upgrade copies the new ones over them, so a protocol profile, Qt plugin or QML module
    # withdrawn in this release cannot survive in {app}; a stale protocol profile would still
    # be offered by the selector.
    #
    # That list is hand-written while the layout it must cover comes from windeployqt, which
    # adds and renames plugin directories between Qt releases. An uncovered directory would
    # fail silently, on upgrade, on a user's machine, so the list is checked against the
    # layout actually being shipped.
    if (-not (Test-Path -LiteralPath $InstallerScript)) {
        throw ("找不到安装器脚本: {0}" -f $InstallerScript)
    }

    $declared = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $InstallerScript -Encoding UTF8) {
        # Matches the directory name in:  Type: filesandordirs; Name: "{app}\qml"
        $match = [regex]::Match($line, 'Name:\s*"\{app\}\\([^"\\]+)"')
        if ($match.Success) { [void]$declared.Add($match.Groups[1].Value) }
    }

    $staged = @(Get-ChildItem -LiteralPath $TargetDir -Directory | Select-Object -ExpandProperty Name)
    $uncovered = @($staged | Where-Object { -not $declared.Contains($_) })

    if ($uncovered.Count -gt 0) {
        $names = $uncovered -join [Environment]::NewLine
        throw ("以下目录会被安装到 {app}，但 installer/MentorRecorder.iss 的 [InstallDelete] " +
            "没有覆盖它们；升级时上一版本的内容会残留：" + [Environment]::NewLine + $names)
    }

    Write-Host ("  [InstallDelete] 覆盖了全部 {0} 个子目录。" -f $staged.Count) -ForegroundColor Green
}

function Assert-RequiredContent([string]$TargetDir) {
    # What a GPL-3.0 distribution and a reproducible validation build must carry; a missing
    # entry is a licensing or traceability failure.
    $required = @(
        'MentorRecorder.Collector.exe'
        'MentorRecorder.Desktop.exe'
        'Qt6Multimedia.dll'
        'multimedia\windowsmediaplugin.dll'
        'LICENSE'
        'THIRD_PARTY_NOTICES.md'
        'README.md'
        'SOURCE_CODE.md'
        'BUILD-METADATA.json'
        'SHA256SUMS.txt'
        'docs\privacy-boundary.md'
        'docs\release-checklist.md'
        'docs\third-party-licenses.md'
    )

    $missing = @()
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $TargetDir $relative))) {
            $missing += $relative
        }
    }

    if ($missing.Count -gt 0) {
        throw ("打包输出缺少必需文件:`n{0}" -f ($missing -join [Environment]::NewLine))
    }

    Write-Host ("  必需文件齐全（{0} 项）。" -f $required.Count) -ForegroundColor Green
}

Write-Head '打包 / Packaging'
Write-Host ("配置: {0}" -f $Configuration)
Write-Host ("输出目录: {0}" -f $OutputRoot)

Assert-File $CollectorProject '缺少 Collector 项目文件'
Assert-File $Windeployqt '缺少 windeployqt'
Assert-File $BuildScript '缺少构建脚本'
Assert-File $VerifyScript '缺少验证脚本'

if (-not $SkipVerify) {
    Write-Head '预验证 / Preflight verify'
    & $VerifyScript -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} else {
    Write-Head '预验证 / Preflight verify'
    Write-Host '按 -SkipVerify 跳过；该产物只适合本地快速联调。' -ForegroundColor Yellow
}

Write-Head '补构建 / Ensure build outputs'
& $BuildScript -Configuration $Configuration -NoRestore
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Assert-File $DesktopExe '缺少 Desktop 可执行文件'

Write-Head '准备目录 / Prepare staging directories'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Assert-ReplaceAllowed @($StageDir, $CollectorStage, $ZipPath, $ZipHashPath)
Remove-IfExists $StageDir
Remove-IfExists $CollectorStage
if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
if (Test-Path -LiteralPath $ZipHashPath) {
    Remove-Item -LiteralPath $ZipHashPath -Force
}
New-Item -ItemType Directory -Path $StageDir | Out-Null
New-Item -ItemType Directory -Path $CollectorStage | Out-Null

Write-Head '发布 Collector / Publish Collector'
# Self-contained: the installer promises no dependencies to install by hand, and the .NET
# runtime is the only dependency this project may redistribute. Npcap is not (see the .iss).
& dotnet publish $CollectorProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:NuGetAudit=false `
    -o $CollectorStage
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Head '组装发布目录 / Assemble staged layout'
Copy-Item -Path (Join-Path $CollectorStage '*') -Destination $StageDir -Recurse -Force
Copy-Item -LiteralPath $DesktopExe -Destination (Join-Path $StageDir 'MentorRecorder.Desktop.exe')
if (Test-Path -LiteralPath $DesktopManifest) {
    Copy-Item -LiteralPath $DesktopManifest -Destination (Join-Path $StageDir 'MentorRecorder.Desktop.exe.manifest')
}
Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE') -Destination $StageDir
Copy-Item -LiteralPath (Join-Path $RepoRoot 'README.md') -Destination $StageDir
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD_PARTY_NOTICES.md') -Destination $StageDir
Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs') -Destination (Join-Path $StageDir 'docs') -Recurse -Force

# The development output carries synthetic profiles for replay tests. They must not reach the
# live selector in an installed build, even though their fake game-build marker keeps them
# fail-closed on real clients.
$SyntheticProfiles = Join-Path $StageDir 'protocol-profiles\synthetic'
Remove-IfExists $SyntheticProfiles

# docs/ is copied whole so new user-facing documents ship without editing this list. The
# directories below are developer working material kept out of the repository by .gitignore;
# a local checkout may still have them and a package must not.
foreach ($localOnlyDocs in @('docs\diagrams', 'docs\plans', 'docs\reviews')) {
    Remove-IfExists (Join-Path $StageDir $localOnlyDocs)
}

@(
    '# Source Code Availability'
    ''
    'This package was produced from the MentorRecorder source tree.'
    'Complete corresponding source code for the packaged revision (see source_commit in'
    'BUILD-METADATA.json): https://github.com/Harendotes-Tang/MentorRouletteRecorder'
    'Licence: GPL-3.0-or-later (LICENSE); third-party notices: THIRD_PARTY_NOTICES.md.'
) | Set-Content -LiteralPath $SourceNotePath -Encoding utf8

$sourceCommit = (& git -C $RepoRoot rev-parse HEAD 2>$null)
$sourceCommitAvailable = $LASTEXITCODE -eq 0
$sourceDirty = @(& git -C $RepoRoot status --porcelain --untracked-files=all 2>$null).Count -gt 0

Write-Head '读取构建自述 / Read build self-report'
# Exit code 1 from --capture-doctor means this machine could not capture right now (no Npcap,
# no game). That says nothing about the artifact and is accepted; only a crash or a non-JSON
# answer is a failure.
$stagedDoctor = Invoke-StagedCollectorJson -TargetDir $StageDir `
    -CollectorArgs @('--capture-doctor', '--json') -AllowedExitCodes @(0, 1) -Label 'staged-capture-doctor'
$stagedProfiles = Invoke-StagedCollectorJson -TargetDir $StageDir `
    -CollectorArgs @('--list-profiles', '--json') -AllowedExitCodes @(0, 1) -Label 'staged-list-profiles'

# @() so a single VERIFIED profile is still an array: Set-StrictMode Latest refuses
# .Count on the bare string PowerShell would otherwise unwrap it to.
$verifiedProfileIds = @(Get-PackagedVerifiedProfileIds $stagedProfiles)
$packagedProfileStatus = Format-PackagedProfileStatus $verifiedProfileIds
$liveCaptureStatus = [string]$stagedDoctor.live_capture_status

# The status of the profile set in the source tree is declared in one place,
# protocol-profiles/README.md, and read from there rather than repeated here.
$ProfilesReadme = Join-Path $RepoRoot 'protocol-profiles\README.md'
Assert-File $ProfilesReadme '缺少协议档案 README'
$sourceProfileMatch = [regex]::Match(
    (Get-Content -LiteralPath $ProfilesReadme -Raw -Encoding UTF8),
    'PROTOCOL_PROFILE_STATUS\s*=\s*([A-Z0-9_]+)')
if (-not $sourceProfileMatch.Success) {
    throw ("protocol-profiles/README.md 里找不到 PROTOCOL_PROFILE_STATUS = <状态>: {0}" -f $ProfilesReadme)
}
$sourceProfileStatus = $sourceProfileMatch.Groups[1].Value

# public_distribution_ready is a claim about this artifact, so every blocker below must hold.
# The first two are declarations, not findings: compile-time constants read back out of the
# staged binary (see Invoke-StagedCollectorJson), recorded with a *_source field saying so and
# kept only so a build whose declared status regressed cannot ship. The last three carry the
# gate: whether the package carries a VERIFIED protocol profile, whether its source is
# committed, and whether verification was skipped.
$distributionBlockers = @()
if (-not $stagedDoctor.public_distribution_ready) {
    $distributionBlockers += '打包的 Collector 自述 public_distribution_ready=false（编译期常量）'
}
if ($liveCaptureStatus -ne 'VERIFIED_POP_TO_EXIT') {
    $distributionBlockers += ("live_capture_status={0}（编译期常量，应为 VERIFIED_POP_TO_EXIT）" -f $liveCaptureStatus)
}
if ($verifiedProfileIds.Count -eq 0) {
    $distributionBlockers += '包内没有任何 VERIFIED 协议档案'
}
if ($sourceDirty) {
    $distributionBlockers += '源码工作区有未提交改动（source_worktree_dirty）'
}
if ($SkipVerify) {
    $distributionBlockers += '按 -SkipVerify 跳过了验证关卡'
}
$publicDistributionReady = $distributionBlockers.Count -eq 0

Write-Host ("  live_capture_status              = {0}  (打包二进制中的编译期常量)" -f $liveCaptureStatus)
Write-Host ("  packaged_verified_profile_status = {0}  (由包内档案实际得出)" -f $packagedProfileStatus)
Write-Host ("  public_distribution_ready        = {0}" -f $publicDistributionReady)
foreach ($blocker in $distributionBlockers) {
    Write-Host ("    - {0}" -f $blocker) -ForegroundColor Yellow
}

[ordered]@{
    product = 'MentorRecorder'
    version = $Version
    configuration = $Configuration
    runtime = 'win-x64'
    framework_dependent = $false
    source_commit = if ($sourceCommitAvailable) { [string]$sourceCommit } else { $null }
    source_worktree_dirty = $sourceDirty
    live_capture_status = $liveCaptureStatus
    # Where each status came from, so a reader never has to guess whether a field is a
    # finding or a declaration: live_capture_status is a constant, not evidence that this
    # build was run against live game traffic.
    live_capture_status_source = 'CaptureDiagnostics constant in the staged binary'
    source_protocol_profile_status = $sourceProfileStatus
    packaged_verified_profile_status = $packagedProfileStatus
    packaged_verified_profile_status_source = 'staged --list-profiles over the packaged profile files'
    public_distribution_ready = $publicDistributionReady
    public_distribution_ready_source = 'staged CaptureDiagnostics constant, packaged profiles, git worktree state and -SkipVerify'
    public_distribution_blockers = @($distributionBlockers)
    produced_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath $BuildMetadataPath -Encoding utf8

Write-Head '部署 Qt 运行时 / Deploy Qt runtime'
$deployMode = if ($Configuration -eq 'Debug') { '--debug' } else { '--release' }
# --no-ffmpeg / --exclude-plugins: online speech only plays WAV files, which the Windows
# multimedia backend handles; see $ForbiddenMediaPatterns.
& $Windeployqt `
    $deployMode `
    --compiler-runtime `
    --no-translations `
    --no-ffmpeg `
    --exclude-plugins ffmpegmediaplugin `
    --qmldir $QmlSourceDir `
    (Join-Path $StageDir 'MentorRecorder.Desktop.exe')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Add-MinGwRuntime $StageDir
Add-OffscreenPlatformPlugin $StageDir

Write-Head '禁止内容核对 / Forbidden content assertions'
Assert-NoForbiddenPayload $StageDir
Assert-NoLocalPathLeak $StageDir
Assert-MultimediaLayout $StageDir

Write-Head '生成校验和 / Generate checksums'
$hashLines = foreach ($file in Get-ChildItem -LiteralPath $StageDir -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($StageDir.Length).TrimStart('\')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1}' -f $hash, ($relative -replace '\\', '/')
}
$hashLines | Set-Content -LiteralPath $HashPath -Encoding utf8

# Runs here, not with the forbidden-content check: SHA256SUMS.txt is one of the files it
# requires and the line above is what creates it.
Write-Head '必需内容核对 / Required content assertions'
Assert-RequiredContent $StageDir

Write-Head '版本一致性核对 / Version consistency assertions'
Assert-StagedVersion $StageDir
Assert-ReleasedChangelogSectionsUnchanged

Write-Head '升级残留核对 / Upgrade leftover assertions'
Assert-InstallDeleteCoverage $StageDir

Write-Head '压缩 / Zip artifact'
Compress-Archive -LiteralPath $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
("{0}  {1}" -f $zipHash, (Split-Path -Leaf $ZipPath)) |
    Set-Content -LiteralPath $ZipHashPath -Encoding ascii

if ($Verify) {
    Write-Head '解包验证 / Unpack and run'

    # This step uses nothing from the workspace: the archive is expanded elsewhere and both
    # executables are run from there, so a runtime dependency that is only satisfied by a
    # stray PATH entry or a file left in the build tree fails here, not on a user's machine.
    $verifyRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mr-pkg-verify-{0}" -f ([guid]::NewGuid().ToString('N')))
    try {
        New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $verifyRoot -Force

        $unpacked = Join-Path $verifyRoot (Split-Path -Leaf $StageDir)
        Assert-File $unpacked '解包后找不到发布目录'
        Write-Host ("  解包目录: {0}" -f $unpacked)

        # The unpacked tree must satisfy the same content rules as the staging tree; a zip
        # that silently dropped or added a file is a broken artifact.
        Assert-NoForbiddenPayload $unpacked
        Assert-NoLocalPathLeak $unpacked
        Assert-MultimediaLayout $unpacked
        Assert-RequiredContent $unpacked
        Assert-StagedVersion $unpacked
        Assert-ReleasedChangelogSectionsUnchanged

        $collector = Join-Path $unpacked 'MentorRecorder.Collector.exe'
        $desktop = Join-Path $unpacked 'MentorRecorder.Desktop.exe'
        Assert-File $collector '解包目录缺少 Collector'
        Assert-File $desktop '解包目录缺少 Desktop'

        function Invoke-Unpacked([string]$Exe, [string[]]$PackageArgs, [int[]]$AllowedExitCodes, [string]$Label,
                                 [switch]$Offscreen) {
            $out = Join-Path $verifyRoot ("{0}.out" -f $Label)
            $err = Join-Path $verifyRoot ("{0}.err" -f $Label)
            $startInfo = New-IsolatedPackageStartInfo -Executable $Exe -Arguments $PackageArgs `
                -WorkingDirectory $unpacked -Offscreen:$Offscreen
            # Kill switches for the unpacked runs (docs/privacy-boundary.md sections 8.2, 8.3, 8.4).
            $startInfo.get_EnvironmentVariables()['MR_DISABLE_SHARED_FETCH'] = '1'
            $startInfo.get_EnvironmentVariables()['MR_DISABLE_ONLINE_SPEECH'] = '1'
            $startInfo.get_EnvironmentVariables()['MR_DISABLE_UPDATE_CHECK'] = '1'
            $process = [System.Diagnostics.Process]::Start($startInfo)
            try {
                # Drain both pipes while the process runs so Qt diagnostics cannot fill
                # stderr and block a screenshot before it reaches its exit path.
                $stdoutTask = $process.StandardOutput.ReadToEndAsync()
                $stderrTask = $process.StandardError.ReadToEndAsync()
                $process.WaitForExit()
                $processExit = $process.ExitCode
                $stdout = $stdoutTask.GetAwaiter().GetResult()
                $stderr = $stderrTask.GetAwaiter().GetResult()
            }
            finally {
                $process.Dispose()
            }
            Set-Content -LiteralPath $out -Value $stdout -Encoding UTF8
            Set-Content -LiteralPath $err -Value $stderr -Encoding UTF8

            if ($AllowedExitCodes -notcontains $processExit) {
                throw ("{0} 退出码 {1}（允许 {2}）:`n{3}`n{4}" -f
                    $Label, $processExit, ($AllowedExitCodes -join ','), $stdout.Trim(), $stderr.Trim())
            }

            Write-Host ("  [ok] {0} (exit {1})" -f $Label, $processExit) -ForegroundColor Green
            return $stdout
        }

        # Not $version: PowerShell variables are case-insensitive and $Version (from
        # Directory.Build.props) is still needed by the installer step below.
        $versionBanner = Invoke-Unpacked $collector @('--version') @(0) 'collector-version'
        $bannerMatch = [regex]::Match($versionBanner.Trim(),
            '^MentorRecorder\.Collector (\d+\.\d+\.\d+) \(ipc v\d+\)$')
        if (-not $bannerMatch.Success -or $bannerMatch.Groups[1].Value -ne $Version) {
            throw ("--version 横幅与 Directory.Build.props 的 {0} 不一致: {1}" -f $Version, $versionBanner.Trim())
        }
        Write-Host ("       {0}" -f $versionBanner.Trim())

        # The assertion is independent of the machine it runs on.
        #
        # --capture-doctor exits 0 when this machine could capture right now and 1 when it
        # could not (no Npcap, no game client, no suitable adapter). Both are accepted; only
        # a crash or a non-JSON answer is a failure. The three boundary tokens asserted below
        # are compile-time constants in src/Collector/Capture/CaptureDiagnostics.cs
        # (LiveCaptureStatus, MonitorType, InjectedHookEnabled), describing what the build is
        # allowed to do rather than what the machine has installed, so a CI runner without
        # Npcap must produce the same three values as a developer machine.
        $doctor = Invoke-Unpacked $collector @('--capture-doctor', '--json') @(0, 1) 'collector-doctor'
        $report = $doctor | ConvertFrom-Json
        if ($report.live_capture_status -ne 'VERIFIED_POP_TO_EXIT') {
            throw ("--capture-doctor 报告 live_capture_status = {0}，应为 VERIFIED_POP_TO_EXIT。" -f $report.live_capture_status)
        }
        if ($report.boundary.injected_hook_enabled) {
            throw '--capture-doctor 报告注入式钩子已启用；这是硬边界违规。'
        }
        if ($report.boundary.monitor_type -ne 'WinPCap') {
            throw ("--capture-doctor 报告 monitor_type = {0}，应为 WinPCap。" -f $report.boundary.monitor_type)
        }
        Write-Host ("       live_capture_status={0} monitor_type={1} injected_hook={2}" -f
            $report.live_capture_status, $report.boundary.monitor_type, $report.boundary.injected_hook_enabled)

        # BUILD-METADATA.json is what a downloader reads to tell a release from a local
        # experiment. Its status fields are checked against the unpacked build's own answers
        # and the distribution claim against its preconditions.
        #
        # The live_capture_status comparison proves only that the metadata and the packaged
        # binary came from the same build: both sides are the same compile-time constant. The
        # comparisons that can actually disagree are the profile status (read from the
        # packaged files) and the worktree state.
        $metadata = (Get-Content -LiteralPath (Join-Path $unpacked 'BUILD-METADATA.json') -Raw -Encoding UTF8) |
            ConvertFrom-Json
        $unpackedProfiles = Invoke-StagedCollectorJson -TargetDir $unpacked `
            -CollectorArgs @('--list-profiles', '--json') -AllowedExitCodes @(0, 1) -Label 'unpacked-list-profiles'
        $unpackedVerifiedIds = @(Get-PackagedVerifiedProfileIds $unpackedProfiles)
        $unpackedProfileStatus = Format-PackagedProfileStatus $unpackedVerifiedIds

        if ($metadata.live_capture_status -ne $report.live_capture_status) {
            throw ("BUILD-METADATA.json 的 live_capture_status = {0}，但解包后的 Collector 报告 {1}。" -f
                $metadata.live_capture_status, $report.live_capture_status)
        }
        if ($metadata.packaged_verified_profile_status -ne $unpackedProfileStatus) {
            throw ("BUILD-METADATA.json 的 packaged_verified_profile_status = {0}，但包内实际是 {1}。" -f
                $metadata.packaged_verified_profile_status, $unpackedProfileStatus)
        }

        if ($metadata.public_distribution_ready) {
            $verifyBlockers = @()
            if ($metadata.source_worktree_dirty) {
                $verifyBlockers += '源码工作区有未提交改动：产物无法与任何提交对应'
            }
            if (@(& git -C $RepoRoot status --porcelain --untracked-files=all 2>$null).Count -gt 0) {
                $verifyBlockers += '打包后源码工作区又出现了未提交改动'
            }
            if ($unpackedVerifiedIds.Count -eq 0) {
                $verifyBlockers += '包内没有任何 VERIFIED 协议档案'
            }
            if ($report.live_capture_status -ne 'VERIFIED_POP_TO_EXIT') {
                $verifyBlockers += ("live_capture_status = {0}" -f $report.live_capture_status)
            }
            if ($verifyBlockers.Count -gt 0) {
                throw ("BUILD-METADATA.json 声称 public_distribution_ready = true，但:`n{0}" -f
                    (($verifyBlockers | ForEach-Object { '  - ' + $_ }) -join [Environment]::NewLine))
            }
        }
        Write-Host ("  [ok] BUILD-METADATA.json 与包内实际一致（{0}，public_distribution_ready={1}）。" -f
            $unpackedProfileStatus, $metadata.public_distribution_ready) -ForegroundColor Green

        # The Desktop needs the whole Qt runtime, the QML modules and the offscreen platform
        # plugin; rendering a screenshot exercises all three.
        $shot = Join-Path $verifyRoot 'unpacked-smoke.png'
        Invoke-Unpacked $desktop @('--screenshot', $shot, '--page', '1', '--theme', 'dark',
            '--screenshot-delay', '400') @(0) 'desktop-screenshot' -Offscreen | Out-Null

        Assert-File $shot '解包后的 Desktop 没有产出截图'
        $shotBytes = (Get-Item -LiteralPath $shot).Length
        if ($shotBytes -lt 4096) {
            throw ("截图只有 {0} 字节，看起来不是一张真的界面。" -f $shotBytes)
        }
        Write-Host ("  [ok] 截图 {0} 字节" -f $shotBytes) -ForegroundColor Green

        # Online speech plays through QSoundEffect and the only multimedia backend in the
        # package. Exit 6 means this machine has no audio output device, which says nothing
        # about the artifact; anything else but 0 is a missing or broken runtime.
        $silentWave = Join-Path $verifyRoot 'speech-selftest.wav'
        New-SilentWave $silentWave
        $selftest = Invoke-Unpacked $desktop @('--speech-selftest', $silentWave) @(0, 6) 'desktop-speech-selftest'
        Write-Host ("       {0}" -f (($selftest -split "`r?`n" | Where-Object { $_ }) -join ' | '))

        # Nothing the run just did may have written a database or a log into the artifact.
        Assert-NoForbiddenPayload $unpacked
        Write-Host '  解包验证通过：运行时依赖完整。' -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $verifyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $NoInstaller) {
    Write-Head '安装器 / Installer'
    if (-not $Iscc) {
        Write-Host '  未找到 Inno Setup 6（ISCC.exe），跳过安装器。安装: winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
    } else {
        Assert-File $InstallerScript '缺少安装器脚本'
        if (Test-Path -LiteralPath $InstallerPath) { Remove-IfExists $InstallerPath }
        $isccArgs = @('/Q', ("/DAppVersion={0}" -f $Version), ("/DStageDir={0}" -f $StageDir), ("/DOutputDir={0}" -f $OutputRoot), $InstallerScript)
        Write-Host ("  ISCC {0}" -f ($isccArgs -join ' '))
        & $Iscc @isccArgs
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
        Assert-File $InstallerPath '安装器未生成'
        $setupHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ("{0}  {1}" -f $setupHash, (Split-Path -Leaf $InstallerPath)) |
            Set-Content -LiteralPath ($InstallerPath + '.sha256') -Encoding ascii
        Write-Host ("  安装器: {0} ({1:N0} 字节)" -f $InstallerPath, (Get-Item -LiteralPath $InstallerPath).Length) -ForegroundColor Green
    }
}

Write-Host ''
Write-Host ("发布目录: {0}" -f $StageDir) -ForegroundColor Green
Write-Host ("ZIP: {0}" -f $ZipPath) -ForegroundColor Green
Write-Host ("ZIP SHA256: {0}" -f $ZipHashPath) -ForegroundColor Green
if ($Iscc -and -not $NoInstaller) { Write-Host ("安装器: {0}" -f $InstallerPath) -ForegroundColor Green }
Write-Host ("PUBLIC_DISTRIBUTION_READY = {0}" -f $publicDistributionReady.ToString().ToLowerInvariant()) `
    -ForegroundColor $(if ($publicDistributionReady) { 'Green' } else { 'Yellow' })
Write-Host ("LIVE_CAPTURE_STATUS       = {0}" -f $liveCaptureStatus) -ForegroundColor Green
Write-Host ("PACKAGED_PROFILE_STATUS   = {0}" -f $packagedProfileStatus) -ForegroundColor Green

exit 0
