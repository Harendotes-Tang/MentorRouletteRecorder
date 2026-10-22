#Requires -Version 7.0
<#
.SYNOPSIS
    检查全部桌面端 QML/JS，任何诊断警告均令门禁失败。
.DESCRIPTION
    读取当前 CMake 构建生成的 qmllint 参数和模块类型信息。显式以 UTF-8
    读取响应文件，再使用 Unicode 参数启动工具，避免 Windows 中文路径损坏。
    同时检查 JSON 中的诊断，不能只用工具退出码判断是否清零。
#>
[CmdletBinding()]
param(
    [string]$BuildDirectory = $env:MR_BUILD_DIR,
    [string]$QmllintExecutable,
    [string]$OutputDirectory = ('artifacts/qml-lint/run-' + [guid]::NewGuid().ToString('N'))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) { $BuildDirectory = 'build' }
$buildRoot = Resolve-RepoPath $BuildDirectory
$outputRoot = Resolve-RepoPath $OutputDirectory
$responseFile = Join-Path $buildRoot 'src/Desktop/.rcc/qmllint/MentorRecorder.Desktop.rsp'
$moduleDirectory = Join-Path $buildRoot 'src/Desktop/MentorRecorder'
$qmlDirectory = Join-Path $repoRoot 'src/Desktop/qml'
if (-not (Test-Path -LiteralPath $responseFile -PathType Leaf)) {
    throw "未找到 CMake 的 QML 检查参数：$responseFile。请先配置当前源码的构建目录。"
}

# Refuse a different checkout's generated resource paths and type information.
$cache = [IO.File]::ReadAllText((Join-Path $buildRoot 'CMakeCache.txt'))
$configuredSource = [regex]::Match($cache, '(?m)^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)\r?$').Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($configuredSource) -or
    -not [string]::Equals((Resolve-RepoPath $configuredSource), $repoRoot,
                         [StringComparison]::OrdinalIgnoreCase)) {
    throw '该构建目录不属于当前源码目录，拒绝使用其中的 QML 检查结果。'
}

$qmldirFile = Join-Path $moduleDirectory 'qmldir'
$qmldir = [IO.File]::ReadAllText($qmldirFile)
$typeInfoName = [regex]::Match($qmldir, '(?m)^typeinfo\s+([^\r\n]+)').Groups[1].Value.Trim()
if (-not $typeInfoName -or -not (Test-Path -LiteralPath (Join-Path $moduleDirectory $typeInfoName) -PathType Leaf)) {
    throw '模块声明的 .qmltypes 尚未生成。请先构建 all_qmltyperegistrations 目标。'
}

if ([string]::IsNullOrWhiteSpace($QmllintExecutable)) {
    # Use the same Qt installation as this CMake build, rather than another kit on PATH.
    $qmlCmakeDirectory = [regex]::Match($cache, '(?m)^Qt6Qml_DIR:PATH=(.+)\r?$').Groups[1].Value.Trim()
    if (-not $qmlCmakeDirectory) { throw 'CMake 缓存中缺少 Qt6Qml_DIR，请显式指定 QmllintExecutable。' }
    $qtPrefix = [IO.Path]::GetFullPath((Join-Path $qmlCmakeDirectory '../../..'))
    $toolName = if ($IsWindows) { 'qmllint.exe' } else { 'qmllint' }
    $QmllintExecutable = Join-Path $qtPrefix "bin/$toolName"
}
if (-not (Test-Path -LiteralPath $QmllintExecutable -PathType Leaf)) {
    throw "未找到 QML 检查工具：$QmllintExecutable"
}

$arguments = @([IO.File]::ReadAllLines($responseFile, [Text.Encoding]::UTF8) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object {
        # CMake quotes a complete argument when its path contains spaces.
        if ($_.Length -ge 2 -and $_.StartsWith('"') -and $_.EndsWith('"')) {
            $_.Substring(1, $_.Length - 2)
        } else { $_ }
    })
$expectedFiles = @($arguments | Where-Object { $_ -match '\.(qml|js|mjs)$' } |
    ForEach-Object { Resolve-RepoPath $_ } | Sort-Object -Unique)
$sourceFiles = @(Get-ChildItem -LiteralPath $qmlDirectory -Recurse -File |
    Where-Object { $_.Extension -in @('.qml', '.js', '.mjs') } |
    ForEach-Object { $_.FullName } | Sort-Object -Unique)
$difference = @(Compare-Object $sourceFiles $expectedFiles)
if ($difference.Count) {
    $difference | Format-Table InputObject, SideIndicator
    throw 'CMake 的 QML 检查清单与当前源码不一致，请重新配置并补齐模块文件清单。'
}
$sourceHashes = @{}
foreach ($file in $sourceFiles) { $sourceHashes[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$reportPath = Join-Path $outputRoot 'diagnostics.json'
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $QmllintExecutable
$startInfo.WorkingDirectory = Join-Path $repoRoot 'src/Desktop'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
$startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
$startInfo.ArgumentList.Add('--json')
$startInfo.ArgumentList.Add($reportPath)
$startInfo.ArgumentList.Add('--max-warnings')
$startInfo.ArgumentList.Add('0')
$process = [Diagnostics.Process]::Start($startInfo)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(600000)) {
        $process.Kill($true)
        throw 'QML 检查超时，未完成的结果不能作为通过证据。'
    }
    [IO.File]::WriteAllText((Join-Path $outputRoot 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $outputRoot 'stderr.log'), $stderr.GetAwaiter().GetResult())
    $toolExitCode = $process.ExitCode
} finally {
    $process.Dispose()
}

$report = [IO.File]::ReadAllText($reportPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
$currentSourceFiles = @(Get-ChildItem -LiteralPath $qmlDirectory -Recurse -File |
    Where-Object { $_.Extension -in @('.qml', '.js', '.mjs') } |
    ForEach-Object { $_.FullName } | Sort-Object -Unique)
if (@(Compare-Object $sourceFiles $currentSourceFiles).Count) {
    throw '检查过程中 QML/JS 文件清单变化，结果已失效。'
}
foreach ($file in $sourceFiles) {
    if ($sourceHashes[$file] -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) {
        throw "检查过程中源码变化，结果已失效：$file"
    }
}
$reportedFiles = @($report.files | ForEach-Object { Resolve-RepoPath $_.filename } | Sort-Object -Unique)
if (@(Compare-Object $expectedFiles $reportedFiles).Count) {
    throw 'QML 检查输出缺少预期文件，不能判定全量检查完成。'
}
$diagnostics = @($report.files | ForEach-Object { $_.warnings })
$warnings = @($diagnostics | Where-Object type -eq 'warning')
$errors = @($diagnostics | Where-Object type -eq 'error')
$infos = @($diagnostics | Where-Object type -eq 'info')
$summary = [ordered]@{
    SourceDirectory = $repoRoot
    BuildDirectory = $buildRoot
    QmllintExecutable = $QmllintExecutable
    ToolExitCode = $toolExitCode
    FileCount = $reportedFiles.Count
    WarningCount = $warnings.Count
    ErrorCount = $errors.Count
    InfoCount = $infos.Count
    SourceHashes = $sourceHashes
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'summary.json'), ($summary | ConvertTo-Json -Depth 3))
Write-Host ("QML/JS 文件 {0}，警告 {1}，错误 {2}，提示 {3}；工具退出码 {4}" -f
    $reportedFiles.Count, $warnings.Count, $errors.Count, $infos.Count, $toolExitCode)
$diagnostics | Group-Object id | Sort-Object Count -Descending |
    Select-Object Count, Name | Format-Table -AutoSize
Write-Host "完整诊断：$reportPath"
if ($toolExitCode -ne 0 -or $warnings.Count -or $errors.Count -or
    @($report.files | Where-Object { -not $_.success }).Count) {
    exit 1
}
exit 0
