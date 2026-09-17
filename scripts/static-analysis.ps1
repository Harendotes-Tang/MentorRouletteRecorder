#Requires -Version 7.0
# PowerShell 7, unlike build.ps1 / test.ps1: ProcessStartInfo.ArgumentList is needed to start
# the tools without a shell re-quoting the arguments. CI already runs every script with pwsh.
<#
.SYNOPSIS
    桌面端 C++ 静态分析 / Static analysis of the Qt/C++ Desktop tree.

.DESCRIPTION
    对 src/Desktop/cpp 与 tests/Desktop.Tests 的每个编译单元运行 clang-tidy 与 cppcheck，
    把原始输出写到 artifacts/static-analysis/，并在控制台按检查项汇总。

    输入是 build/compile_commands.json（由 scripts/build.ps1 的 CMake configure 生成），
    因此运行前必须先构建一次；MR_BUILD_DIR 与 build.ps1 含义相同。

    退出码只由"硬"结果决定：clang-tidy 的编译错误或 clang-analyzer-* 告警、cppcheck 的
    error 级结果。风格类告警（bugprone-narrowing-conversions、performance-* 等）会列出
    但不改变退出码；-Strict 让任何未被抑制的告警都算失败。

    脚本代为绕开的工具限制：
      * llvm-mingw 自带的 clang-tidy 默认找 libc++，而 compile_commands.json 来自 MinGW
        g++，标准库头文件只存在于 GCC 目录；因此加 -stdlib=libstdc++。
      * cppcheck 用 ANSI 文件 API，带非 ASCII 字符的绝对路径会打不开；因此一律以仓库根
        为工作目录传相对路径，并把编译宏写进一个强制包含的头文件而不是命令行。
      * cppcheck 不解析 Qt 头文件（太慢，且 --library=qt 已描述其语义），Q_OS_WIN 一类
        由 Qt 头文件推导的宏要显式给出，否则 #ifdef Q_OS_WIN 分支会被当作不存在。

.PARAMETER Tool
    all（默认）、clang-tidy 或 cppcheck。

.PARAMETER Path
    只分析仓库相对路径包含这些片段的文件（不区分大小写），例如 -Path IpcFraming。

.PARAMETER Strict
    任何未被抑制的告警都令退出码非零。

.EXAMPLE
    pwsh -File scripts/static-analysis.ps1
    pwsh -File scripts/static-analysis.ps1 -Tool cppcheck
    pwsh -File scripts/static-analysis.ps1 -Path IpcFraming,IpcClient -Strict
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'clang-tidy', 'cppcheck')]
    [string]$Tool = 'all',

    [string[]]$Path,

    [ValidateRange(1, 64)]
    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount),

    [string]$OutputDir = 'artifacts/static-analysis',

    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# ---------------------------------------------------------------- locations --
function Resolve-RepoPath([string]$Configured, [string]$Default) {
    $value = if ([string]::IsNullOrWhiteSpace($Configured)) { $Default } else { $Configured }
    if ([System.IO.Path]::IsPathRooted($value)) { return [System.IO.Path]::GetFullPath($value) }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $value))
}

# Same override as build.ps1 / test.ps1: the analysis reads whatever that build configured.
$BuildDir = Resolve-RepoPath ([Environment]::GetEnvironmentVariable('MR_BUILD_DIR')) 'build'
$CompileDb = Join-Path $BuildDir 'compile_commands.json'
$OutputRoot = Resolve-RepoPath $OutputDir $OutputDir

# Toolchain locations follow the build.ps1 convention: the literal is the example default
# for the documented machine, the MR_* variable is the supported override. No user-profile
# path may ever appear here (this file is public).
function Get-ToolchainPath([string]$Variable, [string]$ExampleDefault) {
    $configured = [Environment]::GetEnvironmentVariable($Variable)
    if ([string]::IsNullOrWhiteSpace($configured)) { return $ExampleDefault }
    return $configured.Replace('\', '/')
}
$ClangTidyExe = Get-ToolchainPath 'MR_CLANG_TIDY_EXE' 'D:/APPS/Qt/Tools/llvm-mingw1706_64/bin/clang-tidy.exe'
$CppcheckExe  = Get-ToolchainPath 'MR_CPPCHECK_EXE'   'C:/Program Files/Cppcheck/cppcheck.exe'

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

# Repository-relative form of a path, or $null when it lies outside the repository. Every
# tool is started with the repository root as working directory, so a path that is not
# rooted is already relative to it.
function Convert-ToRepoRelative([string]$FullPath) {
    $normalizedRoot = $RepoRoot.Replace('\', '/').TrimEnd('/') + '/'
    $normalized = $FullPath.Replace('\', '/')
    if (-not [System.IO.Path]::IsPathRooted($normalized)) { return $normalized }
    if ($normalized.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $normalized.Substring($normalizedRoot.Length)
    }
    return $null
}

# ----------------------------------------------------- compile_commands.json --
if (-not (Test-Path -LiteralPath $CompileDb)) {
    throw ("找不到 {0}。先运行 pwsh -File scripts/build.ps1（或设置 MR_BUILD_DIR 指向已 configure 的目录）。" -f $CompileDb)
}

# Splits one compile command the way the shell would: whitespace separates arguments unless
# inside double quotes, and a backslash-escaped quote is a literal quote (that is how CMake
# writes -DMR_APP_VERSION=\"0.9.0\").
function Split-CommandLine([string]$Command) {
    $arguments = New-Object System.Collections.Generic.List[string]
    $current = New-Object System.Text.StringBuilder
    $inQuotes = $false
    $pending = $false
    for ($i = 0; $i -lt $Command.Length; $i++) {
        $c = $Command[$i]
        if ($c -eq '\' -and $i + 1 -lt $Command.Length -and $Command[$i + 1] -eq '"') {
            [void]$current.Append('"'); $pending = $true; $i++; continue
        }
        if ($c -eq '"') { $inQuotes = -not $inQuotes; $pending = $true; continue }
        if (-not $inQuotes -and [char]::IsWhiteSpace($c)) {
            if ($pending) { $arguments.Add($current.ToString()); [void]$current.Clear(); $pending = $false }
            continue
        }
        [void]$current.Append($c); $pending = $true
    }
    if ($pending) { $arguments.Add($current.ToString()) }
    return $arguments
}

$entries = Get-Content -LiteralPath $CompileDb -Raw -Encoding UTF8 | ConvertFrom-Json
$units = @()
foreach ($entry in $entries) {
    $relative = Convert-ToRepoRelative $entry.file
    if (-not $relative) { continue }
    if ($relative -notmatch '^(src/Desktop/cpp|tests/Desktop\.Tests)/[^/]+\.cpp$') { continue }
    if ($Path) {
        $wanted = $false
        foreach ($fragment in $Path) {
            if ($relative.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $wanted = $true }
        }
        if (-not $wanted) { continue }
    }
    $units += [pscustomobject]@{
        Relative  = $relative
        Arguments = Split-CommandLine $entry.command
    }
}
if ($units.Count -eq 0) {
    throw ("compile_commands.json 里没有匹配的桌面端编译单元（-Path {0}）。" -f ($Path -join ','))
}

# For messages: the repository-relative path when there is one, the absolute path otherwise.
function Format-PathLabel([string]$FullPath) {
    $relative = Convert-ToRepoRelative $FullPath
    if ($relative) { return $relative }
    return $FullPath
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$outputLabel = Format-PathLabel $OutputRoot
Write-Head ("静态分析 / static analysis: {0} 个编译单元, {1} 并发, 输出 {2}" -f $units.Count, $Jobs, $outputLabel)

# Findings from every tool land here in one shape so the summary and the exit code are
# computed once.
$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding([string]$ToolName, [string]$File, [int]$Line, [string]$Severity, [string]$Check, [string]$Message, [bool]$Hard, [bool]$ThirdParty) {
    $findings.Add([pscustomobject]@{
        Tool = $ToolName; File = $File; Line = $Line; Severity = $Severity
        Check = $Check; Message = $Message; Hard = $Hard; ThirdParty = $ThirdParty
    })
}
$toolFailures = @()

# Runs one external program with the repository root as working directory and UTF-8 output,
# without going through a shell (so a non-ASCII repository path never gets re-encoded).
function Start-Tool([string]$Exe, [string[]]$ArgumentList, [string]$StdoutPath) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.WorkingDirectory = $RepoRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $Utf8NoBom
    $info.StandardErrorEncoding = $Utf8NoBom
    foreach ($argument in $ArgumentList) { [void]$info.ArgumentList.Add($argument) }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    # Both streams are drained asynchronously; a full pipe would otherwise block the tool.
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ Process = $process; Stdout = $stdoutTask; Stderr = $stderrTask; StdoutPath = $StdoutPath }
}

function Wait-Tool($Handle) {
    $Handle.Process.WaitForExit()
    [System.IO.File]::WriteAllText($Handle.StdoutPath, $Handle.Stdout.Result, $Utf8NoBom)
    [System.IO.File]::WriteAllText(($Handle.StdoutPath -replace '\.log$', '.err'), $Handle.Stderr.Result, $Utf8NoBom)
    return $Handle.Process.ExitCode
}

# ----------------------------------------------------------------- clang-tidy --
if ($Tool -in @('all', 'clang-tidy')) {
    Write-Head 'clang-tidy'
    if (-not (Test-Path -LiteralPath $ClangTidyExe)) {
        $toolFailures += ("clang-tidy 不存在: {0}（设置 MR_CLANG_TIDY_EXE）" -f $ClangTidyExe)
    } else {
        $tidyDir = Join-Path $OutputRoot 'clang-tidy'
        New-Item -ItemType Directory -Force -Path $tidyDir | Out-Null
        Get-ChildItem -LiteralPath $tidyDir -File | Remove-Item -Force
        $buildArgument = Convert-ToRepoRelative $BuildDir
        if (-not $buildArgument) { $buildArgument = $BuildDir }
        $queue = New-Object System.Collections.Generic.Queue[object]
        foreach ($unit in $units) { $queue.Enqueue($unit) }
        $running = New-Object System.Collections.Generic.List[object]
        $done = 0
        try {
            while ($queue.Count -gt 0 -or $running.Count -gt 0) {
                while ($queue.Count -gt 0 -and $running.Count -lt $Jobs) {
                    $unit = $queue.Dequeue()
                    # The whole relative path names the log, so two units that share a base
                    # name can never race for the same file.
                    $logName = ($unit.Relative -replace '\.cpp$', '' -replace '[/\\]', '_') + '.log'
                    $arguments = @(
                        '-p', $buildArgument,
                        '--config-file=.clang-tidy',
                        '--quiet',
                        # See the header comment: GCC's libstdc++ is the only standard library
                        # the MinGW compile commands can see.
                        '--extra-arg=-stdlib=libstdc++',
                        $unit.Relative
                    )
                    $handle = Start-Tool $ClangTidyExe $arguments (Join-Path $tidyDir $logName)
                    $handle | Add-Member -NotePropertyName Unit -NotePropertyValue $unit.Relative
                    $running.Add($handle)
                }
                $finished = $running | Where-Object { $_.Process.HasExited }
                if (-not $finished) { Start-Sleep -Milliseconds 100; continue }
                foreach ($handle in @($finished)) {
                    $exitCode = Wait-Tool $handle
                    $running.Remove($handle) | Out-Null
                    $done++
                    Write-Host ("  [{0}/{1}] {2}" -f $done, $units.Count, $handle.Unit)
                    # clang-tidy exits 0 and prints location-less "error:" lines when it could
                    # not analyse a unit at all (bad compile command, missing file). Silence
                    # there would read as "no findings", so it is a tool failure instead.
                    $output = $handle.Stdout.Result + "`n" + $handle.Stderr.Result
                    $unanalysed = $output -match '(?m)^(Error while processing |error: )'
                    if ($exitCode -ne 0 -or $unanalysed) {
                        $toolFailures += ("clang-tidy 未能分析 {0}（退出码 {1}），见 {2}" -f $handle.Unit, $exitCode, (Format-PathLabel ($handle.StdoutPath -replace '\.log$', '.err')))
                    }
                }
            }
        } finally {
            # Ctrl+C or an exception above must not leave analyzers running detached.
            foreach ($handle in $running) {
                try { if (-not $handle.Process.HasExited) { $handle.Process.Kill() } } catch { }
            }
        }

        # "path:line:col: severity: message [check]" - the location decides whether the
        # finding is ours or lives in a Qt header the analyzer walked into.
        $pattern = '^(?<file>.+?):(?<line>\d+):\d+: (?<severity>warning|error): (?<message>.*) \[(?<check>[\w.,-]+)\]\s*$'
        foreach ($log in Get-ChildItem -LiteralPath $tidyDir -Filter '*.log') {
            foreach ($line in [System.IO.File]::ReadAllLines($log.FullName, $Utf8NoBom)) {
                if ($line -notmatch $pattern) { continue }
                $relative = Convert-ToRepoRelative $Matches.file
                $thirdParty = -not $relative
                $file = if ($relative) { $relative } else { $Matches.file.Replace('\', '/') }
                $check = $Matches.check
                $hard = ($Matches.severity -eq 'error') -or $check.StartsWith('clang-analyzer-')
                Add-Finding 'clang-tidy' $file ([int]$Matches.line) $Matches.severity $check $Matches.message ($hard -and -not $thirdParty) $thirdParty
            }
        }
    }
}

# ------------------------------------------------------------------- cppcheck --
if ($Tool -in @('all', 'cppcheck')) {
    Write-Head 'cppcheck'
    if (-not (Test-Path -LiteralPath $CppcheckExe)) {
        $toolFailures += ("cppcheck 不存在: {0}（设置 MR_CPPCHECK_EXE）" -f $CppcheckExe)
    } else {
        $cppcheckDir = Join-Path $OutputRoot 'cppcheck'
        New-Item -ItemType Directory -Force -Path $cppcheckDir | Out-Null

        # Every -D from the compile commands becomes a #define in one forced-include header,
        # so quoting never goes through a command line; every project -I is kept and every
        # -isystem (all of them Qt) is dropped in favour of --library=qt.
        $defines = New-Object System.Collections.Generic.List[string]
        $defineValues = @{}
        $includes = New-Object System.Collections.Generic.List[string]
        foreach ($unit in $units) {
            $arguments = $unit.Arguments
            for ($i = 0; $i -lt $arguments.Count; $i++) {
                $argument = $arguments[$i]
                if ($argument -eq '-isystem') { $i++; continue }
                if ($argument.StartsWith('-D')) {
                    $body = $argument.Substring(2)
                    $name, $value = $body -split '=', 2
                    if ($null -eq $value) { $value = '1' }
                    # One header serves every unit, so a macro must mean the same thing in
                    # all of them; a conflict would make the analysis order-dependent.
                    if ($defineValues.ContainsKey($name)) {
                        if ($defineValues[$name] -ne $value) {
                            $toolFailures += ("宏 {0} 在不同编译单元里取值不同（{1} / {2}），cppcheck 的强制包含头无法表达" -f $name, $defineValues[$name], $value)
                        }
                    } else {
                        $defineValues[$name] = $value
                        $defines.Add("#define $name $value")
                    }
                } elseif ($argument.StartsWith('-I')) {
                    $directory = $argument.Substring(2)
                    $relative = Convert-ToRepoRelative $directory
                    $include = if ($relative) { $relative } else { $directory }
                    if (-not $includes.Contains($include)) { $includes.Add($include) }
                }
            }
        }
        # Macros Qt's own headers would have derived from the compiler; cppcheck never sees
        # those headers, so without these every `#ifdef Q_OS_WIN` reads as dead code.
        foreach ($platformDefine in @('_WIN32', '_WIN64', '__MINGW32__', '__MINGW64__', 'Q_OS_WIN', 'Q_OS_WINDOWS')) {
            $defines.Add("#ifndef $platformDefine")
            $defines.Add("#define $platformDefine 1")
            $defines.Add('#endif')
        }
        $definesHeader = Join-Path $cppcheckDir 'compile-defines.h'
        [System.IO.File]::WriteAllLines($definesHeader, $defines, $Utf8NoBom)
        $xmlPath = Join-Path $cppcheckDir 'cppcheck.xml'
        # Relative when the output directory is inside the repository (the usual case, and
        # the one that keeps a non-ASCII repository path off cppcheck's command line);
        # absolute when -OutputDir points elsewhere.
        $definesArgument = Convert-ToRepoRelative $definesHeader
        if (-not $definesArgument) { $definesArgument = $definesHeader }
        $xmlArgument = Convert-ToRepoRelative $xmlPath
        if (-not $xmlArgument) { $xmlArgument = $xmlPath }
        $arguments = @(
            '--enable=warning,style,performance,portability',
            '--library=qt',
            '--std=c++20',
            '--platform=win64',
            '--inline-suppr',
            ('-j{0}' -f $Jobs),
            # A -D on the command line puts cppcheck in single-configuration mode (the
            # forced-include header carries the actual values).
            '-D_WIN32',
            ('--include={0}' -f $definesArgument),
            # Qt headers are not on the include path on purpose (see above).
            '--suppress=missingInclude',
            '--suppress=missingIncludeSystem',
            # A test's `#include "XTests.moc"` pulls in moc output whose first lines #error
            # unless Qt's own headers were parsed first - which, see above, they never are.
            '--suppress=preprocessorErrorDirective:*.moc',
            # Needs whole-program analysis of one executable; the test binaries alone would
            # report every helper of another binary as unused.
            '--suppress=unusedFunction',
            # Style opinions this code base has decided the other way:
            #   Q_PROPERTY getters return implicitly shared Qt values by value (QString,
            #   QVariantList) - that is the Qt convention, not a copy worth flagging.
            '--suppress=returnByReference',
            #   setFoo(const T &foo) next to foo() is the Qt setter/getter pairing.
            '--suppress=shadowFunction',
            #   A Q_PROPERTY or Q_INVOKABLE getter cannot become static; cppcheck cannot know.
            '--suppress=functionStatic',
            #   Raw loops over QJsonArray/QList are readable here; no STL rewrite wanted.
            '--suppress=useStlAlgorithm',
            '--suppress=checkersReport',
            '--xml',
            ('--output-file={0}' -f $xmlArgument)
        )
        foreach ($include in $includes) { $arguments += ('-I{0}' -f $include) }
        foreach ($unit in $units) { $arguments += $unit.Relative }

        $handle = Start-Tool $CppcheckExe $arguments (Join-Path $cppcheckDir 'cppcheck.log')
        $exitCode = Wait-Tool $handle
        if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $xmlPath)) {
            $toolFailures += ("cppcheck 退出码 {0}，见 {1}" -f $exitCode, (Format-PathLabel (Join-Path $cppcheckDir 'cppcheck.err')))
        } else {
            [xml]$report = [System.IO.File]::ReadAllText($xmlPath, $Utf8NoBom)
            foreach ($issue in $report.SelectNodes('/results/errors/error')) {
                $location = $issue.SelectSingleNode('location')
                $rawFile = if ($location) { $location.GetAttribute('file') } else { '' }
                $line = if ($location) { [int]$location.GetAttribute('line') } else { 0 }
                $relative = if ($rawFile) { Convert-ToRepoRelative $rawFile } else { $null }
                $thirdParty = -not $relative
                $file = if ($relative) { $relative } elseif ($rawFile) { $rawFile.Replace('\', '/') } else { '(none)' }
                $hard = ($issue.GetAttribute('severity') -eq 'error')
                Add-Finding 'cppcheck' $file $line $issue.GetAttribute('severity') $issue.GetAttribute('id') $issue.GetAttribute('msg') ($hard -and -not $thirdParty) $thirdParty
            }
        }
    }
}

# -------------------------------------------------------------------- summary --
Write-Head '汇总 / summary'
$ours = @($findings | Where-Object { -not $_.ThirdParty })
$qt = @($findings | Where-Object { $_.ThirdParty })
$hard = @($ours | Where-Object { $_.Hard })

if ($ours.Count -gt 0) {
    $ours | Group-Object Tool, Severity, Check | Sort-Object -Property @{ Expression = 'Count'; Descending = $true }, Name |
        ForEach-Object {
            $sample = $_.Group[0]
            '{0,5}  {1,-10} {2,-12} {3}' -f $_.Count, $sample.Tool, $sample.Severity, $sample.Check
        } | Write-Host
    Write-Host ''
    Write-Host '硬结果 / hard findings（决定退出码）:' -ForegroundColor Yellow
    if ($hard.Count -eq 0) { Write-Host '  无 / none' }
    foreach ($finding in $hard | Sort-Object File, Line) {
        Write-Host ('  {0}:{1}  [{2}] {3}' -f $finding.File, $finding.Line, $finding.Check, $finding.Message)
    }
} else {
    Write-Host '  没有落在本仓库内的告警 / no findings inside the repository'
}
if ($qt.Count -gt 0) {
    Write-Host ('  另有 {0} 条分析器路径告警落在 Qt 头文件内（QPointer/QSharedPointer 引用计数），已忽略；原文见 {1}' -f $qt.Count, $outputLabel)
}

$summaryPath = Join-Path $OutputRoot 'findings.csv'
$findings | Sort-Object Tool, File, Line | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8
Write-Host ('  明细 / details: {0}' -f (Format-PathLabel $summaryPath))

foreach ($failure in $toolFailures) { Write-Host ("  工具失败 / tool failure: {0}" -f $failure) -ForegroundColor Red }

$failing = $toolFailures.Count -gt 0 -or $hard.Count -gt 0 -or ($Strict -and $ours.Count -gt 0)
if ($failing) {
    Write-Host ''
    Write-Host '静态分析未通过 / static analysis FAILED' -ForegroundColor Red
    exit 1
}
Write-Host ''
Write-Host ('静态分析通过 / static analysis passed（{0} 条风格类告警未阻塞，-Strict 可令其阻塞）' -f $ours.Count) -ForegroundColor Green
exit 0
