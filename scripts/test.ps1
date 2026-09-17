#Requires -Version 5.1
<#
.SYNOPSIS
    运行测试并报告真实用例数 / Run tests and report real case counts.

.DESCRIPTION
    枚举 tests/ 下声明 IsTestProject=true 的全部 .NET 测试项目，逐个运行并解析
    TRX 结果文件取得真实的通过/失败/跳过数量，再运行 Qt/C++ ctest。

    判定只依据计数，不依据退出码：`dotnet test MentorRecorder.sln` 的解决方案里
    只有 Collector 一个项目，不含测试项目，"0 个测试" 同样退出 0。规则：
      * 任一项目失败数 > 0        → 失败
      * 任一项目用例总数 = 0      → 失败（空测试不是成功）
      * 找不到测试项目            → 失败
      * 找不到 TRX 文件           → 失败

    Qt/C++ 侧同理，并把静默跳过按失败处理：`MentorRecorderIpcIntegration` 在
    Collector 可执行文件未 stage 到测试二进制旁时会 `QSKIP` 掉整个二进制，
    QtTest 与 ctest 均记为通过，桌面端↔真实 Collector 的集成通路因此可能从未
    被执行。脚本用 `--output-junit` 抓取每个用例的输出，逐条核对跳过数。

    默认先执行完整构建；-NoBuild 要求 build/ 已由同一配置生成。
    -Python 额外运行 tools/ 下的 Python 自测（`dotnet test` 与 `ctest` 不覆盖）。
    结果文件写入 artifacts/test-results/。
    退出码：0 表示全部通过；1 表示任一闸门失败。

.EXAMPLE
    pwsh -File scripts/test.ps1
    pwsh -File scripts/test.ps1 -Configuration Debug -NoBuild
    pwsh -File scripts/test.ps1 -Python
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    [switch]$NoRestore,

    # xunit trait filter passed to dotnet test, e.g. 'Category!=Soak'.
    [string]$Filter,

    # Also run the Python self-tests under tools/. verify.ps1 runs them as a
    # separate step, so this switch is for direct invocations of test.ps1.
    [switch]$Python,

    # Seconds to idle between the .NET suite and ctest; see the CTest section below.
    [ValidateRange(0, 120)]
    [int]$SettleSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TestsRoot = Join-Path $RepoRoot 'tests'
$DesktopTests = Join-Path $RepoRoot 'tests\Desktop.Tests\CMakeLists.txt'
# Keep the CTest target directory aligned with build.ps1 for isolated builds.
$configuredBuildDir = [Environment]::GetEnvironmentVariable('MR_BUILD_DIR')
$BuildDir = if ([string]::IsNullOrWhiteSpace($configuredBuildDir)) {
    Join-Path $RepoRoot 'build'
} elseif ([System.IO.Path]::IsPathRooted($configuredBuildDir)) {
    [System.IO.Path]::GetFullPath($configuredBuildDir)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $configuredBuildDir))
}
# Example default (see docs/build-and-package.md); override with MR_CTEST_EXE to
# test against another CMake installation.
$CTestExe = if ([string]::IsNullOrWhiteSpace($env:MR_CTEST_EXE)) {
    'D:/APPS/Qt/Tools/CMake_64/bin/ctest.exe'
} else { $env:MR_CTEST_EXE.Replace('\', '/') }
$ResultsRoot = Join-Path $RepoRoot 'artifacts\test-results'
$ToolTestScript = Join-Path $PSScriptRoot 'run-python-tool-tests.ps1'

# Qt tests that must never report a skip. QtTest counts a QSKIP as a pass, so a
# skipped binary is indistinguishable from a green one unless the counts are read.
$NoSkipCTests = @('MentorRecorderIpcIntegration')

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

<#
.SYNOPSIS
    Reads the counters out of one TRX file.
.DESCRIPTION
    TRX ResultSummary/Counters is the only place dotnet test records what
    actually ran. The element is namespaced, but PowerShell's XML adapter
    resolves the path regardless, so no namespace manager is needed.
#>
function Read-TrxCounters([string]$Path) {
    [xml]$trx = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $counters = $trx.TestRun.ResultSummary.Counters

    $get = {
        param($name)
        $value = $counters.GetAttribute($name)
        if ([string]::IsNullOrWhiteSpace($value)) { 0 } else { [int]$value }
    }

    [pscustomobject]@{
        Total    = & $get 'total'
        Executed = & $get 'executed'
        Passed   = & $get 'passed'
        Failed   = & $get 'failed'
        Errors   = & $get 'error'
        Timeout  = & $get 'timeout'
        Aborted  = & $get 'aborted'
        NotRun   = & $get 'notExecuted'
    }
}

# --------------------------------------------------------------------- build ------
if (-not $NoBuild -and (Test-Path -LiteralPath $DesktopTests)) {
    Write-Head '完整构建（.NET + Qt） / Full build'
    & (Join-Path $PSScriptRoot 'build.ps1') `
        -Configuration $Configuration `
        -NoRestore:$NoRestore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$code = 0

# Shared-calibration kill switch (docs/privacy-boundary.md section 8.2): nothing a test run
# starts - the .NET suites, or the Collectors the Qt suites launch - may reach the network.
# Restored just before this script exits.
$previousSharedFetch = $env:MR_DISABLE_SHARED_FETCH
$env:MR_DISABLE_SHARED_FETCH = '1'
# Online-speech kill switch (docs/privacy-boundary.md section 8.3), set and restored the same way.
$previousOnlineSpeech = $env:MR_DISABLE_ONLINE_SPEECH
$env:MR_DISABLE_ONLINE_SPEECH = '1'
# Update-check kill switch (docs/privacy-boundary.md section 8.4), set and restored the same way.
$previousUpdateCheck = $env:MR_DISABLE_UPDATE_CHECK
$env:MR_DISABLE_UPDATE_CHECK = '1'

# -------------------------------------------------------------- Python tools ------
if ($Python) {
    Write-Head '工具自测 / Python tool self-tests'
    if (-not (Test-Path -LiteralPath $ToolTestScript)) {
        Write-Host ("  找不到工具自测脚本: {0}" -f $ToolTestScript) -ForegroundColor Red
        $code = 1
    }
    else {
        & $ToolTestScript
        if ($LASTEXITCODE -ne 0) { $code = 1 }
    }
}

# ---------------------------------------------------------------- .NET tests ------
Write-Head ("dotnet test ({0})" -f $Configuration)

$dotNetTestProjects = @(
    if (Test-Path -LiteralPath $TestsRoot) {
        Get-ChildItem -LiteralPath $TestsRoot -Recurse -File -Filter '*.csproj' |
            Where-Object {
                Select-String -LiteralPath $_.FullName `
                    -Pattern '<IsTestProject>\s*true\s*</IsTestProject>' `
                    -Quiet
            } |
            Sort-Object FullName
    }
)

$summaries = New-Object System.Collections.Generic.List[object]

if ($dotNetTestProjects.Count -eq 0) {
    Write-Host '未找到任何 .NET 测试项目，拒绝把空测试运行报告为成功。' -ForegroundColor Red
    $code = 1
}
else {
    if (Test-Path -LiteralPath $ResultsRoot) {
        Remove-Item -LiteralPath $ResultsRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null

    # The test projects run against a throw-away MR_DATA_DIR
    # (src/Collector/Storage/DatabasePaths.cs), created before the loop and removed once
    # every project has run; the previous value is restored in `finally`. Without it,
    # ProfileCatalog.LoadMerged / FindLocalRoot and anything else reading
    # DatabasePaths.RootDirectory resolves to the developer's real
    # %LOCALAPPDATA%\MentorRecorder.
    #
    # Limitation: this covers runs through this script only. The test project has no
    # assembly-wide fixture ([ModuleInitializer] / ICollectionFixture) defaulting
    # MR_DATA_DIR, so a bare `dotnet test` still uses the real data directory for anything
    # that does not isolate itself (DataPathOptionTests and TestDatabase do so for the
    # database). scripts/verify.ps1 always goes through this script.
    $previousDataDir = $env:MR_DATA_DIR
    $testDataDir = Join-Path ([System.IO.Path]::GetTempPath()) `
        ("MentorRecorder.TestDataDir." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDataDir -Force | Out-Null
    $env:MR_DATA_DIR = $testDataDir
    try {
        foreach ($project in $dotNetTestProjects) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($project.FullName)
            $relativeProject = $project.FullName.Substring($RepoRoot.Length).TrimStart('\')
            Write-Host ''
            Write-Host ("-- {0}" -f $relativeProject) -ForegroundColor DarkCyan

            $trxName = "$name.trx"
            $trxPath = Join-Path $ResultsRoot $trxName

            $testArgs = @(
                $project.FullName
                '-c'; $Configuration
                '--logger'; "trx;LogFileName=$trxName"
                '--logger'; 'console;verbosity=minimal'
                '--results-directory'; $ResultsRoot
            )
            if ($NoBuild) { $testArgs += '--no-build' }
            if ($NoRestore) {
                $testArgs += '--no-restore'
                $testArgs += '-p:NuGetAudit=false'
            }
            if ($Filter) { $testArgs += @('--filter', $Filter) }

            & dotnet test @testArgs
            $runExit = $LASTEXITCODE

            if (-not (Test-Path -LiteralPath $trxPath)) {
                Write-Host ("  没有生成 TRX 结果文件: {0}" -f $trxPath) -ForegroundColor Red
                Write-Host '  无法证明测试真的跑过，按失败处理。' -ForegroundColor Red
                $code = 1
                continue
            }

            $counters = Read-TrxCounters $trxPath
            $summaries.Add([pscustomobject]@{
                Project  = $relativeProject
                Total    = $counters.Total
                Passed   = $counters.Passed
                Failed   = $counters.Failed + $counters.Errors + $counters.Timeout + $counters.Aborted
                # VSTest records skips as the gap between total and executed, not in
                # notExecuted; take the larger of the two so either shape is reported.
                Skipped  = [Math]::Max($counters.Total - $counters.Executed, $counters.NotRun)
                Trx      = $trxPath
            })

            if ($counters.Total -eq 0) {
                Write-Host '  该项目一个用例都没有运行；空测试不算成功。' -ForegroundColor Red
                $code = 1
            }
            elseif ($counters.Failed -gt 0 -or $counters.Errors -gt 0 -or
                    $counters.Timeout -gt 0 -or $counters.Aborted -gt 0) {
                $code = 1
            }
            elseif ($runExit -ne 0) {
                Write-Host ("  dotnet test 退出码 {0}，但 TRX 没有失败用例；按失败处理。" -f $runExit) `
                    -ForegroundColor Red
                $code = 1
            }
        }
    }
    finally {
        if ($null -eq $previousDataDir) {
            Remove-Item Env:\MR_DATA_DIR -ErrorAction SilentlyContinue
        }
        else {
            $env:MR_DATA_DIR = $previousDataDir
        }
        if (Test-Path -LiteralPath $testDataDir) {
            Remove-Item -LiteralPath $testDataDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ------------------------------------------------------------------- report -------
Write-Head '.NET 用例统计 / .NET case counts'

if ($summaries.Count -eq 0) {
    Write-Host '  （没有任何结果）' -ForegroundColor Red
}
else {
    $format = '  {0,-56} {1,7} {2,7} {3,7} {4,7}'
    Write-Host ($format -f '项目 / project', '总数', '通过', '失败', '跳过')
    foreach ($summary in $summaries) {
        $colour = if ($summary.Failed -gt 0 -or $summary.Total -eq 0) { 'Red' } else { 'Green' }
        Write-Host ($format -f
            $summary.Project, $summary.Total, $summary.Passed, $summary.Failed, $summary.Skipped) `
            -ForegroundColor $colour
    }

    $totals = [pscustomobject]@{
        Total   = ($summaries | Measure-Object -Property Total   -Sum).Sum
        Passed  = ($summaries | Measure-Object -Property Passed  -Sum).Sum
        Failed  = ($summaries | Measure-Object -Property Failed  -Sum).Sum
        Skipped = ($summaries | Measure-Object -Property Skipped -Sum).Sum
    }
    Write-Host ($format -f '合计 / total', $totals.Total, $totals.Passed, $totals.Failed, $totals.Skipped) `
        -ForegroundColor $(if ($totals.Failed -gt 0) { 'Red' } else { 'Green' })
    Write-Host ("  TRX: {0}" -f $ResultsRoot) -ForegroundColor DarkGray
}

# --------------------------------------------------------------- Qt / CTest -------
if (-not (Test-Path -LiteralPath $DesktopTests)) {
    Write-Host ''
    Write-Host '桌面端测试尚未引入。' -ForegroundColor Yellow
}
else {
    Write-Head 'Qt/C++ CTest'

    # Let the machine settle first: the .NET suite ends with a soak pushing tens of
    # thousands of messages a second, while several Qt tests assert on restart backoff and
    # screenshot timing and would fail for unrelated reasons under that load.
    $orphans = @(Get-Process -Name 'MentorRecorder*' -ErrorAction SilentlyContinue)
    if ($orphans.Count -gt 0) {
        Write-Host ('  等待 {0} 个 MentorRecorder 进程退出…' -f $orphans.Count) -ForegroundColor Yellow
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and
               @(Get-Process -Name 'MentorRecorder*' -ErrorAction SilentlyContinue).Count -gt 0) {
            Start-Sleep -Milliseconds 500
        }
    }
    Start-Sleep -Seconds $SettleSeconds

    if (-not (Test-Path -LiteralPath $CTestExe)) {
        Write-Host ("找不到 ctest: {0}" -f $CTestExe) -ForegroundColor Red
        $code = 1
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $BuildDir 'CTestTestfile.cmake'))) {
        Write-Host ("{0} 尚未配置；请移除 -NoBuild 或先运行 scripts/build.ps1。" -f $BuildDir) -ForegroundColor Red
        $code = 1
    }
    else {
        # --output-junit records every case's stdout, passing ones included. It is the
        # only way to see a QSKIP: ctest prints nothing for a test it considers passed,
        # and QtTest considers a fully skipped binary passed.
        $ctestJUnit = Join-Path $ResultsRoot 'ctest.junit.xml'
        foreach ($name in $NoSkipCTests) {
            $qtLog = Join-Path $BuildDir "tests\Desktop.Tests\$name.txt"
            if (Test-Path -LiteralPath $qtLog) { Remove-Item -LiteralPath $qtLog -Force }
        }
        if (-not (Test-Path -LiteralPath $ResultsRoot)) {
            New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
        }
        $ctestOutput = & $CTestExe --test-dir $BuildDir --output-on-failure `
            --output-junit $ctestJUnit -C $Configuration 2>&1
        $ctestExit = $LASTEXITCODE
        $ctestOutput | ForEach-Object { Write-Host $_ }

        # ctest prints "N% tests passed, M tests failed out of K". Read those numbers
        # rather than trusting the exit code: a run with zero registered tests exits 0.
        $line = $ctestOutput |
            Where-Object { $_ -match 'tests passed,\s*(\d+)\s*tests failed out of\s*(\d+)' } |
            Select-Object -Last 1
        if ($line -and $line -match 'tests passed,\s*(\d+)\s*tests failed out of\s*(\d+)') {
            $ctestFailed = [int]$Matches[1]
            $ctestTotal = [int]$Matches[2]
            Write-Host ''
            Write-Host ("  Qt/C++ 用例: 总数 {0}，失败 {1}" -f $ctestTotal, $ctestFailed) `
                -ForegroundColor $(if ($ctestFailed -gt 0) { 'Red' } else { 'Green' })
            if ($ctestTotal -eq 0) {
                Write-Host '  ctest 没有注册任何用例；空测试不算成功。' -ForegroundColor Red
                $code = 1
            }
            if ($ctestFailed -gt 0) { $code = 1 }
        }
        else {
            Write-Host '  无法从 ctest 输出中读到用例统计；按失败处理。' -ForegroundColor Red
            $code = 1
        }

        if ($ctestExit -ne 0) { $code = 1 }

        # ------------------------------------------------- QSKIP is not a pass -------
        # IpcIntegrationTests QSKIPs its whole binary when MentorRecorder.Collector.exe
        # is not staged next to the test executable, or cannot be started. QtTest exits 0
        # either way, so a skipped run is indistinguishable from a green one. For the
        # desktop-to-real-Collector integration tests a skip is treated as a failure.
        if (-not (Test-Path -LiteralPath $ctestJUnit)) {
            Write-Host ''
            Write-Host ("  ctest 没有写出 JUnit 结果: {0}" -f $ctestJUnit) -ForegroundColor Red
            Write-Host '  无法证明 QSKIP 没有发生，按失败处理。' -ForegroundColor Red
            $code = 1
        }
        else {
            [xml]$junit = Get-Content -LiteralPath $ctestJUnit -Raw -Encoding UTF8
            $cases = @($junit.testsuite.testcase)

            foreach ($name in $NoSkipCTests) {
                $case = $cases | Where-Object { $_.name -eq $name } | Select-Object -First 1
                if (-not $case) {
                    Write-Host ''
                    Write-Host ("  用例 {0} 根本没有注册。" -f $name) -ForegroundColor Red
                    Write-Host '  它不存在与它被跳过是同一种结果：桌面端↔真实 Collector 的集成' `
                        -ForegroundColor Red
                    Write-Host '  通路没有被验证过。' -ForegroundColor Red
                    $code = 1
                    continue
                }

                # Preferred signal: QtTest's footer, "Totals: N passed, N failed, N
                # skipped, ...". It is only available when the binary reaches a console --
                # on MinGW these Qt targets link as GUI subsystem binaries, so ctest
                # captures nothing from them and this branch does not fire.
                $out = [string]$case.'system-out'
                $qtLog = Join-Path $BuildDir "tests\Desktop.Tests\$name.txt"
                if ($out -notmatch 'Totals:' -and (Test-Path -LiteralPath $qtLog)) {
                    $out += Get-Content -LiteralPath $qtLog -Raw
                }
                if ($out -match 'Totals:\s*(\d+)\s*passed,\s*(\d+)\s*failed,\s*(\d+)\s*skipped') {
                    $passed = [int]$Matches[1]
                    $skipped = [int]$Matches[3]
                    if ($skipped -gt 0 -or $passed -eq 0) {
                        Write-Host ''
                        Write-Host ("  {0}: {1} 通过 / {2} 跳过。QSKIP 不是通过。" -f
                            $name, $passed, $skipped) -ForegroundColor Red
                        foreach ($line in ($out -split "`r?`n" | Where-Object { $_ -match 'SKIP' })) {
                            Write-Host ("    {0}" -f $line.Trim()) -ForegroundColor DarkRed
                        }
                        $code = 1
                    }
                    else {
                        Write-Host ("  {0}: {1} 通过 / 0 跳过。" -f $name, $passed) -ForegroundColor Green
                    }
                }
                else {
                    # Fallback: assert the precondition instead of the skip count.
                    # IpcIntegrationTests looks for the Collector next to its own binary
                    # and then in ../../src/Desktop; when neither has it, the whole binary
                    # QSKIPs and still exits 0. Re-running with QtTest's file logger would
                    # give the real counts but would also start a second real Collector
                    # against the user's production data directory on every test run.
                    #
                    # Only this configured build is inspected: a recursive search could
                    # select a stale Qt Creator build nested below build/.
                    $binary = @(Get-Item -LiteralPath (Join-Path $BuildDir "tests\Desktop.Tests\$name`Tests.exe") `
                        -ErrorAction SilentlyContinue)
                    $lookup = @()
                    if ($binary.Count -gt 0) {
                        $lookup += $binary[0].DirectoryName
                        $lookup += (Join-Path $binary[0].DirectoryName '..\..\src\Desktop')
                    }
                    else {
                        $lookup += (Join-Path $BuildDir 'src\Desktop')
                    }

                    $staged = @($lookup | ForEach-Object {
                        Join-Path $_ 'MentorRecorder.Collector.exe'
                    } | Where-Object { Test-Path -LiteralPath $_ })

                    if ($staged.Count -eq 0) {
                        Write-Host ''
                        Write-Host ("  {0} 找不到 MentorRecorder.Collector.exe。" -f $name) `
                            -ForegroundColor Red
                        Write-Host '  这组用例会因此 QSKIP 掉整个二进制，而 QtTest 与 ctest 都会记成通过——' `
                            -ForegroundColor Red
                        Write-Host '  也就是桌面端↔真实 Collector 的集成通路根本没有被验证。按失败处理。' `
                            -ForegroundColor Red
                        Write-Host '  应当出现在（任一即可）：' -ForegroundColor Yellow
                        foreach ($candidate in $lookup) {
                            Write-Host ("    {0}" -f [System.IO.Path]::GetFullPath($candidate)) `
                                -ForegroundColor Yellow
                        }
                        Write-Host '  见 src/Desktop/cmake/StageCollector.cmake 与 scripts/build.ps1。' `
                            -ForegroundColor Yellow
                        $code = 1
                    }
                    else {
                        Write-Host ("  {0}: 已 stage Collector（{1}），未跳过。" -f
                            $name, [System.IO.Path]::GetFullPath($staged[0])) -ForegroundColor Green
                        Write-Host '    （该二进制是 GUI 子系统程序，ctest 抓不到它的 QtTest 输出；' `
                            -ForegroundColor DarkGray
                        Write-Host '      这里核对的是 QSKIP 的前置条件而不是跳过计数本身。）' `
                            -ForegroundColor DarkGray
                    }
                }
            }
        }
    }
}

if ($null -eq $previousSharedFetch) {
    Remove-Item Env:\MR_DISABLE_SHARED_FETCH -ErrorAction SilentlyContinue
}
else {
    $env:MR_DISABLE_SHARED_FETCH = $previousSharedFetch
}

if ($null -eq $previousOnlineSpeech) {
    Remove-Item Env:\MR_DISABLE_ONLINE_SPEECH -ErrorAction SilentlyContinue
}
else {
    $env:MR_DISABLE_ONLINE_SPEECH = $previousOnlineSpeech
}

if ($null -eq $previousUpdateCheck) {
    Remove-Item Env:\MR_DISABLE_UPDATE_CHECK -ErrorAction SilentlyContinue
}
else {
    $env:MR_DISABLE_UPDATE_CHECK = $previousUpdateCheck
}

Write-Head '结论 / Result'
if ($code -eq 0) {
    Write-Host '  全部 .NET 与 Qt/C++ 测试通过。' -ForegroundColor Green
}
else {
    Write-Host '  测试失败。' -ForegroundColor Red
}

exit $code
