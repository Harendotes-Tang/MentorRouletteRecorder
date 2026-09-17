#Requires -Version 5.1
<#
.SYNOPSIS
    环境自检 / Environment check for MentorRecorder.

.DESCRIPTION
    检测构建所需的工具链并报告 Npcap 安装状态，结果写入控制台。
    脚本仅做检测，不下载、不安装、不修改系统设置；Npcap 免费版禁止再分发，
    因此本项目既不内置也不代为下载（见 docs/third-party-licenses.md）。
    退出码：0 表示完成检测；指定 -Strict 且存在缺失的必需工具时为 1。

.EXAMPLE
    pwsh -File scripts/bootstrap.ps1
#>
[CmdletBinding()]
param(
    # Fail (exit 1) when a required tool is missing. Npcap is never required here.
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Missing = New-Object System.Collections.Generic.List[string]

# --- Expected toolchain locations (Qt-bundled CMake/Ninja/MinGW) -------------
$script:QtRoot      = 'D:\APPS\Qt\6.11.2\mingw_64'
$script:MinGwRoot   = 'D:\APPS\Qt\Tools\mingw1310_64'
$script:NinjaExe    = 'D:\APPS\Qt\Tools\Ninja\ninja.exe'
$script:CMakeExe    = 'D:\APPS\Qt\Tools\CMake_64\bin\cmake.exe'

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Name, [string]$Detail) {
    Write-Host ("  [ OK ] {0,-24} {1}" -f $Name, $Detail) -ForegroundColor Green
}

function Write-Warn2([string]$Name, [string]$Detail) {
    Write-Host ("  [WARN] {0,-24} {1}" -f $Name, $Detail) -ForegroundColor Yellow
}

function Write-Bad([string]$Name, [string]$Detail) {
    Write-Host ("  [FAIL] {0,-24} {1}" -f $Name, $Detail) -ForegroundColor Red
    $script:Missing.Add($Name)
}

function Test-CommandPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { return $null }
    return $cmd.Source
}

Write-Host 'MentorRecorder / FF14 导随记录器 - 环境自检' -ForegroundColor White
Write-Host ("仓库根目录: {0}" -f $script:RepoRoot)

# ---------------------------------------------------------------- .NET ------
Write-Head '.NET'
$dotnet = Test-CommandPath 'dotnet'
if (-not $dotnet) {
    Write-Bad 'dotnet' '未找到。请安装 .NET SDK（需包含 .NET 8 运行时）。'
}
else {
    $sdkVersion = (& dotnet --version) 2>$null
    Write-Ok 'dotnet SDK' ("{0}  ({1})" -f $sdkVersion, $dotnet)

    $runtimes = @(& dotnet --list-runtimes) 2>$null
    $net8 = $runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 8\.' }
    if ($net8) {
        $ver = ($net8 | Select-Object -Last 1) -replace '^Microsoft\.NETCore\.App\s+(\S+).*$', '$1'
        Write-Ok '.NET 8 runtime' $ver
    }
    else {
        Write-Bad '.NET 8 runtime' '未找到 Microsoft.NETCore.App 8.x。项目目标框架是 net8.0。'
    }
}

# ---------------------------------------------------------------- C++ -------
Write-Head 'C++ / Qt 工具链 (MinGW)'

if (Test-Path -LiteralPath $script:CMakeExe) {
    $v = (& $script:CMakeExe --version | Select-Object -First 1)
    Write-Ok 'cmake (Qt 附带)' ("{0}  ({1})" -f $v, $script:CMakeExe)
}
else {
    $sys = Test-CommandPath 'cmake'
    if ($sys) { Write-Warn2 'cmake' ("未找到 {0}，改用 PATH 上的 {1}" -f $script:CMakeExe, $sys) }
    else { Write-Bad 'cmake' ("未找到：{0}" -f $script:CMakeExe) }
}

if (Test-Path -LiteralPath $script:NinjaExe) {
    $v = (& $script:NinjaExe --version)
    Write-Ok 'ninja' ("{0}  ({1})" -f $v, $script:NinjaExe)
}
else {
    Write-Bad 'ninja' ("未找到：{0}" -f $script:NinjaExe)
}

$gxx = Join-Path $script:MinGwRoot 'bin\g++.exe'
if (Test-Path -LiteralPath $gxx) {
    $v = (& $gxx --version | Select-Object -First 1)
    Write-Ok 'g++ (MinGW-w64)' ("{0}  ({1})" -f $v, $gxx)
}
else {
    Write-Bad 'g++ (MinGW-w64)' ("未找到：{0}" -f $gxx)
}

$qtConfig = Join-Path $script:QtRoot 'lib\cmake\Qt6\Qt6Config.cmake'
if (Test-Path -LiteralPath $qtConfig) {
    Write-Ok 'Qt 6.11.2 (mingw_64)' $script:QtRoot
    foreach ($m in @('Quick', 'QuickControls2', 'Graphs', 'TextToSpeech', 'Svg')) {
        $p = Join-Path $script:QtRoot ("lib\cmake\Qt6{0}\Qt6{0}Config.cmake" -f $m)
        if (Test-Path -LiteralPath $p) { Write-Ok ("  Qt6::{0}" -f $m) '已安装' }
        else { Write-Bad ("  Qt6::{0}" -f $m) '缺失' }
    }
}
else {
    Write-Bad 'Qt 6' ("未找到：{0}" -f $qtConfig)
}

# ---------------------------------------------------------------- misc ------
Write-Head '其他工具'
$git = Test-CommandPath 'git'
if ($git) { Write-Ok 'git' ("{0}" -f (& git --version)) }
else { Write-Warn2 'git' '未找到（可选）。' }

# ---------------------------------------------------------------- Npcap ----
Write-Head 'Npcap（只检测，不下载、不安装、不分发）'

$npcapFound = $false
$npcapVersion = $null
$npcapEvidence = New-Object System.Collections.Generic.List[string]

foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Npcap', 'HKLM:\SOFTWARE\Npcap')) {
    try {
        if (Test-Path -LiteralPath $key) {
            $npcapFound = $true
            $npcapEvidence.Add($key)
            $props = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if ($props -and $props.PSObject.Properties.Name -contains 'Version' -and -not $npcapVersion) {
                $npcapVersion = [string]$props.Version
            }
        }
    }
    catch {
        # A missing or inaccessible registry hive is not an error for this check.
    }
}

$wpcap = Join-Path $env:SystemRoot 'System32\Npcap\wpcap.dll'
if (Test-Path -LiteralPath $wpcap) {
    $npcapFound = $true
    $npcapEvidence.Add($wpcap)
    if (-not $npcapVersion) {
        try { $npcapVersion = (Get-Item -LiteralPath $wpcap).VersionInfo.FileVersion } catch { }
    }
}

if ($npcapFound) {
    $detail = if ($npcapVersion) { "已安装，版本 $npcapVersion" } else { '已安装（版本未知）' }
    Write-Ok 'Npcap' $detail
    foreach ($e in $npcapEvidence) { Write-Host ("         证据: {0}" -f $e) -ForegroundColor DarkGray }
}
else {
    Write-Warn2 'Npcap' '未安装 —— 自动抓包不可用'
    Write-Host ''
    Write-Host '  本软件需要 Npcap 才能被动读取本机网卡流量，但**不会**替您下载或安装。' -ForegroundColor Yellow
    Write-Host '  请从 Npcap 官方站点 https://npcap.com/ 自行下载安装，' -ForegroundColor Yellow
    Write-Host '  安装时请勾选 "WinPcap API-compatible Mode"，装好后重新运行本脚本。' -ForegroundColor Yellow
    Write-Host '  Npcap 的免费版禁止对外再分发，因此本项目不内置它。' -ForegroundColor Yellow
    Write-Host '  详见 docs/third-party-licenses.md 与 docs/capture-diagnostics.md。' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- summary --
Write-Head '结论'
if ($script:Missing.Count -eq 0) {
    Write-Host '  构建所需的工具链齐备。' -ForegroundColor Green
}
else {
    Write-Host ('  缺少 {0} 项: {1}' -f $script:Missing.Count, ($script:Missing -join ', ')) -ForegroundColor Red
}
Write-Host '  LIVE_CAPTURE_STATUS     本脚本不复述取值：构建出 Collector 后跑'
Write-Host '                          MentorRecorder.Collector.exe --capture-doctor --json'
Write-Host '                          读它的 live_capture_status (见 docs/live-validation-guide.md)'
Write-Host '  PROTOCOL_PROFILE_STATUS 见 protocol-profiles/README.md 顶部的同名标记'
Write-Host ''

if ($Strict -and $script:Missing.Count -gt 0) { exit 1 }
exit 0
