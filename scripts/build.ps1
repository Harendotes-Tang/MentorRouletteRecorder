#Requires -Version 5.1
<#
.SYNOPSIS
    构建 MentorRecorder / Build MentorRecorder.

.DESCRIPTION
    依次执行：dotnet build 构建 Collector；若 src/Desktop/CMakeLists.txt 存在，
    则用 MinGW + Ninja + Qt 6 configure 并构建桌面端，并把 Collector 输出部署到
    桌面端产物目录旁。
    工具链路径由 MR_QT_PREFIX / MR_MINGW_BIN / MR_NINJA_EXE / MR_CMAKE_EXE 覆盖，
    构建目录由 MR_BUILD_DIR 覆盖。
    退出码：0 表示成功；非 0 为对应构建步骤的退出码。

.EXAMPLE
    pwsh -File scripts/build.ps1
    pwsh -File scripts/build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Skip the CMake/Qt part even when src/Desktop/CMakeLists.txt exists.
    [switch]$SkipDesktop,

    # Use the already restored project.assets.json files; verification uses
    # this mode so it never reaches a package source or changes dependencies.
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Solution   = Join-Path $RepoRoot 'MentorRecorder.sln'
$DesktopCMake = Join-Path $RepoRoot 'src\Desktop\CMakeLists.txt'
# Shared with test.ps1 so a verify run can use a fresh CMake directory without
# touching the developer's existing build/.
$configuredBuildDir = [Environment]::GetEnvironmentVariable('MR_BUILD_DIR')
$BuildDir = if ([string]::IsNullOrWhiteSpace($configuredBuildDir)) {
    Join-Path $RepoRoot 'build'
} elseif ([System.IO.Path]::IsPathRooted($configuredBuildDir)) {
    [System.IO.Path]::GetFullPath($configuredBuildDir)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $configuredBuildDir))
}
$CollectorOutput = Join-Path $RepoRoot ("src\Collector\bin\x64\{0}\net8.0-windows\win-x64" -f $Configuration)
$DesktopOutput = Join-Path $BuildDir 'src\Desktop'

# Toolchain locations. The literals are example defaults (see
# docs/build-and-package.md); set the matching environment variable to build
# against another installation. Defaults must never contain a user profile path:
# this file is public and C:\Users\<name> would leak an account name.
function Get-ToolchainPath([string]$Variable, [string]$ExampleDefault) {
    $configured = [Environment]::GetEnvironmentVariable($Variable)
    if ([string]::IsNullOrWhiteSpace($configured)) { return $ExampleDefault }
    return $configured.Replace('\', '/')
}

$QtPrefix   = Get-ToolchainPath 'MR_QT_PREFIX'  'D:/APPS/Qt/6.11.2/mingw_64'
$MinGwBin   = Get-ToolchainPath 'MR_MINGW_BIN'  'D:/APPS/Qt/Tools/mingw1310_64/bin'
$NinjaExe   = Get-ToolchainPath 'MR_NINJA_EXE'  'D:/APPS/Qt/Tools/Ninja/ninja.exe'
$CMakeExe   = Get-ToolchainPath 'MR_CMAKE_EXE'  'D:/APPS/Qt/Tools/CMake_64/bin/cmake.exe'

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

# ------------------------------------------------------------------ C# ------
Write-Head ("dotnet build ({0})" -f $Configuration)
$buildArgs = @($Solution, '-c', $Configuration)
if ($NoRestore) {
    $buildArgs += '--no-restore'
    $buildArgs += '-p:NuGetAudit=false'
}
& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host 'dotnet build 失败。' -ForegroundColor Red
    exit $LASTEXITCODE
}

# ------------------------------------------------------------- C++ / Qt -----
if ($SkipDesktop) {
    Write-Head '桌面端: 已按 -SkipDesktop 跳过'
    exit 0
}

if (-not (Test-Path -LiteralPath $DesktopCMake)) {
    Write-Head '桌面端: 跳过'
    Write-Host '  src/Desktop/CMakeLists.txt 尚不存在 —— Qt 6 桌面端目标在 Phase 1 引入。' -ForegroundColor Yellow
    Write-Host '  这不是错误。' -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path -LiteralPath $CMakeExe)) {
    Write-Host ("找不到 cmake: {0}" -f $CMakeExe) -ForegroundColor Red
    Write-Host '请先运行 scripts/bootstrap.ps1 检查工具链。' -ForegroundColor Red
    exit 1
}

Write-Head 'cmake configure (MinGW + Ninja + Qt 6.11.2)'
& $CMakeExe `
    -S $RepoRoot `
    -B $BuildDir `
    -G Ninja `
    "-DCMAKE_MAKE_PROGRAM=$NinjaExe" `
    "-DCMAKE_PREFIX_PATH=$QtPrefix" `
    "-DCMAKE_C_COMPILER=$MinGwBin/gcc.exe" `
    "-DCMAKE_CXX_COMPILER=$MinGwBin/g++.exe" `
    "-DCMAKE_RC_COMPILER=$MinGwBin/windres.exe" `
    "-DMR_STAGE_COLLECTOR=OFF" `
    "-DCMAKE_BUILD_TYPE=$Configuration"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'cmake configure 失败。' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Head 'cmake --build'
# windres (the .rc compiler for the executable icon) invokes gcc as its
# preprocessor via PATH, so the toolchain bin directory must be on PATH.
$env:PATH = ($MinGwBin.Replace('/', '\')) + ';' + $env:PATH
& $CMakeExe --build $BuildDir
if ($LASTEXITCODE -ne 0) {
    Write-Host 'cmake build 失败。' -ForegroundColor Red
    exit $LASTEXITCODE
}

# The Desktop defaults to the real IPC backend, so the framework-dependent
# Collector output must sit next to it: a development build then uses the same
# child-process layout as the packaged application.
Write-Head '部署开发态 Collector / Stage Collector beside Desktop'
if (-not (Test-Path -LiteralPath $CollectorOutput)) {
    Write-Host ("找不到 Collector 构建输出: {0}" -f $CollectorOutput) -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $DesktopOutput)) {
    Write-Host ("找不到 Desktop 构建输出: {0}" -f $DesktopOutput) -ForegroundColor Red
    exit 1
}

$forbiddenCollectorPayload = Get-ChildItem -LiteralPath $CollectorOutput -Recurse -File |
    Where-Object { $_.Name -like 'deucalion-*.dll' }
if ($forbiddenCollectorPayload) {
    Write-Host 'Collector 构建输出包含禁止的注入载荷，拒绝部署。' -ForegroundColor Red
    exit 1
}

$collectorDestinations = @($DesktopOutput)
$ipcTestOutput = Join-Path $BuildDir 'tests\Desktop.Tests'
if (Test-Path -LiteralPath $ipcTestOutput) {
    # Integration tests prefer their own adjacent Collector; refresh it so a
    # previously staged copy cannot shadow the version just compiled.
    $collectorDestinations += $ipcTestOutput
}
foreach ($destination in $collectorDestinations) {
    foreach ($item in Get-ChildItem -LiteralPath $CollectorOutput) {
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }
}

Write-Host ''
Write-Host '构建完成。' -ForegroundColor Green
exit 0
