#Requires -Version 5.1
<#
.SYNOPSIS
    提交前验证 / Pre-commit verification.

.DESCRIPTION
    依次执行以下关卡，任一失败即整体失败：

      1.  环境自检：不存在遗留的 MentorRecorder 进程（失败即立即退出：它们会占用单实例
          租约并锁住 build/ 中的 DLL，继续执行只会得到误导性的构建失败）
      2.  静态硬边界检查（tools/static-boundary-check/check.py）
      3.  架构依赖门禁（tools/architecture-boundary-check/check.py）
      3b. 协议档案校验（tools/protocol-profile-validator/validate.py），覆盖随包全部档案
      4.  tools/ 下全部 Python 自测，其中边界检查器的反向自测（selftest.py）验证它确实
          会拦截；`dotnet test` 与 `ctest` 均不覆盖这些脚本
      5.  完整构建与全部测试（.NET 按 TRX 计数，Qt/C++ 走 ctest），报告真实用例数
      6.  监听端口核对：启动一个 Collector，用 Get-NetTCPConnection 与 netstat 确认它不占端口
      7.  `LIVE_CAPTURE_STATUS` 断言：执行 `--capture-doctor --json`，要求
          `VERIFIED_POP_TO_EXIT`、无注入式钩子、monitor_type 为 WinPCap（与 package.ps1 同一组断言）
      8.  注入载荷扫描：所有输出目录中都不得存在 deucalion 原生载荷
      9.  许可证材料齐全（LICENSE / THIRD_PARTY_NOTICES.md / docs/）

    静态边界检查失败按构建失败处理，它守护的是不可协商的行为边界
    （见 docs/privacy-boundary.md）。

    第 6、7 步不依赖 Npcap 或游戏客户端：断言对象是编译期常量，以及"本机当前无法抓包"
    这一合法结论，因此 CI 与开发机必须得到相同结果。

    退出码：0 表示全部通过；1 表示任一关卡失败。

.EXAMPLE
    pwsh -File scripts/verify.ps1
    pwsh -File scripts/verify.ps1 -Configuration Debug
    pwsh -File scripts/verify.ps1 -TestFilter 'Category!=Soak'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Reuse the existing build outputs instead of rebuilding.
    [switch]$NoBuild,

    # xunit trait filter forwarded to scripts/test.ps1 (which forwards it to dotnet test).
    # CI passes 'Category!=Soak': the soak suite asserts a 5000 msg/s throughput floor and a
    # shared GitHub runner has no stable throughput baseline, so that gate stays local.
    [string]$TestFilter,

    # Skip one named gate, for an environment that genuinely cannot run it. A skipped gate
    # is reported inline and named again in the summary, so it never passes silently.
    # Nothing in this list needs Npcap or a game client: the capture gates assert
    # compile-time boundary constants and accept the "this machine cannot capture" answer.
    [ValidateSet('tool-selftests', 'listener-check', 'live-capture-status', 'injection-payload')]
    [string[]]$SkipGate = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

# Shared-calibration kill switch (docs/privacy-boundary.md section 8.2): nothing verification
# starts - tests, the probe Collector, the doctor run - may download anything. Set process-wide
# so every child inherits it.
$env:MR_DISABLE_SHARED_FETCH = '1'
# Online-speech kill switch (docs/privacy-boundary.md section 8.3), for the second request class.
$env:MR_DISABLE_ONLINE_SPEECH = '1'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$CheckPy             = Join-Path $RepoRoot 'tools\static-boundary-check\check.py'
$ArchitectureCheckPy = Join-Path $RepoRoot 'tools\architecture-boundary-check\check.py'
$TestScript          = Join-Path $PSScriptRoot 'test.ps1'
$ToolTests           = Join-Path $PSScriptRoot 'run-python-tool-tests.ps1'

$failures = New-Object System.Collections.Generic.List[string]
$skipped = New-Object System.Collections.Generic.List[string]

function Test-GateSkipped([string]$Name) {
    if ($SkipGate -contains $Name) {
        Write-Host ("  [skipped] {0} —— 由 -SkipGate 显式跳过。" -f $Name) -ForegroundColor Yellow
        if (-not $skipped.Contains($Name)) { $skipped.Add($Name) }
        return $true
    }
    return $false
}

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Add-Failure([string]$Name, [string]$Detail) {
    Write-Host ("  {0}" -f $Detail) -ForegroundColor Red
    $failures.Add($Name)
}

function Resolve-Python {
    # MR_PYTHON_EXE names the interpreter explicitly (see run-python-tool-tests.ps1):
    # the one CI installed the signature tool's dependencies into.
    if ($env:MR_PYTHON_EXE) {
        if (-not (Test-Path -LiteralPath $env:MR_PYTHON_EXE)) {
            Add-Failure 'python' ("MR_PYTHON_EXE 指向的解释器不存在: {0}" -f $env:MR_PYTHON_EXE)
            return $null
        }
        return Get-Command $env:MR_PYTHON_EXE
    }
    $python = Get-Command 'python' -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command 'python3' -ErrorAction SilentlyContinue }
    return $python
}

# --------------------------------------------------- 1. no leftover processes -----
Write-Head '环境自检 / Environment preflight'

$orphans = @(Get-Process -Name 'MentorRecorder*' -ErrorAction SilentlyContinue)
if ($orphans.Count -gt 0) {
    # The single-instance lease is per user, so a leftover process makes the process-level
    # tests impossible. Refusing here names the cause instead of surfacing it later as a
    # confusing build or test failure.
    foreach ($orphan in $orphans) {
        Write-Host ("  遗留进程: {0} (pid {1})" -f $orphan.ProcessName, $orphan.Id) -ForegroundColor Red
    }
    Write-Host ''
    Write-Host '存在遗留的 MentorRecorder 进程。它们会占用单实例租约、锁住 build/ 里的 DLL，' `
        -ForegroundColor Red
    Write-Host '并让进程级测试与监听端口核对失去意义，所以这里直接停下，而不是继续跑一个注定' `
        -ForegroundColor Red
    Write-Host '失败、且失败原因会误导人的构建。' -ForegroundColor Red
    Write-Host ''
    Write-Host '  Get-Process MentorRecorder* | Stop-Process -Force' -ForegroundColor Yellow
    exit 1
}
else {
    Write-Host '  没有遗留的 MentorRecorder 进程。' -ForegroundColor Green
}

# ------------------------------------------------------- 2. static boundary -------
Write-Head '静态硬边界检查 / Static boundary check'

$python = Resolve-Python
if (-not $python) {
    Add-Failure 'static-boundary-check' '未找到 Python 3。静态边界检查是强制的，不能跳过。'
}
elseif (-not (Test-Path -LiteralPath $CheckPy)) {
    Add-Failure 'static-boundary-check' ("未找到检查脚本: {0}" -f $CheckPy)
}
else {
    & $python.Source $CheckPy --root $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '硬边界检查未通过。请修复上面列出的位置，不要放宽规则。' -ForegroundColor Red
        Write-Host '规则依据见 docs/privacy-boundary.md。' -ForegroundColor Red
        $failures.Add('static-boundary-check')
    }
    else {
        Write-Host '硬边界检查通过。' -ForegroundColor Green
    }
}

# ---------------------------------------------- 3. architecture boundary ---------
Write-Head '架构依赖门禁 / Architecture dependency gate'

if (-not $python) {
    Add-Failure 'architecture-boundary-check' '未找到 Python 3。架构依赖门禁是强制的，不能跳过。'
}
elseif (-not (Test-Path -LiteralPath $ArchitectureCheckPy)) {
    Add-Failure 'architecture-boundary-check' ("未找到检查脚本: {0}" -f $ArchitectureCheckPy)
}
else {
    & $python.Source -B $ArchitectureCheckPy --root $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Add-Failure 'architecture-boundary-check' '架构依赖门禁未通过；请按文件、行号和规则修复依赖。'
    }
    else {
        Write-Host '架构依赖门禁通过。' -ForegroundColor Green
    }
}

# ---------------------------------------------- 3b. protocol profile validation --
Write-Head '协议档案校验 / Protocol profile validation'

# Every shipped profile must pass the Python validator, which mirrors ProfileLoader one for
# one: this keeps the two validators from drifting once self-calibration writes profiles, and
# catches a hand edit that did not re-stamp profile_sha256.
$ProfileValidatorPy = Join-Path $RepoRoot 'tools\protocol-profile-validator\validate.py'
$ShippedProfiles = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'protocol-profiles') -Recurse -Filter '*.json' |
    Where-Object { $_.Name -ne 'profile.schema.json' -and $_.FullName -notmatch '[\\/]oodle-signatures[\\/]' } |
    ForEach-Object { $_.FullName }
if (-not $python) {
    Add-Failure 'protocol-profile-validation' '未找到 Python 3。协议档案校验是强制的，不能跳过。'
}
elseif (-not (Test-Path -LiteralPath $ProfileValidatorPy)) {
    Add-Failure 'protocol-profile-validation' ("未找到校验脚本: {0}" -f $ProfileValidatorPy)
}
else {
    & $python.Source -B $ProfileValidatorPy @ShippedProfiles
    if ($LASTEXITCODE -ne 0) {
        Add-Failure 'protocol-profile-validation' '随包协议档案未通过校验；修改档案后请用 validate.py --stamp 重新盖章。'
    }
    else {
        Write-Host ("协议档案校验通过（{0} 份）。" -f $ShippedProfiles.Count) -ForegroundColor Green
    }
}

# ------------------------------------------------------ 4. tool self-tests --------
Write-Head '工具自测 / Tool self-tests'

# Two reasons this step exists:
#
#   * A checker that never matches anything also passes step 2. selftest.py plants each
#     forbidden token in a throwaway tree and requires the checker to reject it.
#   * The signature finder writes the profiles the Collector loads on a real machine, and
#     the duty-data generator writes the reference data shipped in the package. Both are
#     Python, so neither `dotnet test` nor `ctest` reaches them.
if (Test-GateSkipped 'tool-selftests') { }
elseif (-not (Test-Path -LiteralPath $ToolTests)) {
    Add-Failure 'tool-selftests' ("未找到工具自测脚本: {0}" -f $ToolTests)
}
else {
    & $ToolTests
    if ($LASTEXITCODE -ne 0) {
        Add-Failure 'tool-selftests' 'tools/ 下的 Python 自测没有全部通过。'
    }
}

# --------------------------------------------------- 5. build + all the tests -----
Write-Head ("完整构建与全部测试 ({0}) / Build and all tests" -f $Configuration)
# Splat a hashtable, not an array. $TestScript holds a path, and array splatting against a
# path binds every element positionally: @('-Configuration','Release') arrives as
# Configuration='-Configuration', which ValidateSet rejects. A hashtable splat binds by name.
$testArgs = @{ Configuration = $Configuration; NoRestore = $true }
if ($NoBuild) { $testArgs['NoBuild'] = $true }
if ($TestFilter) {
    $testArgs['Filter'] = $TestFilter
    Write-Host ("  测试筛选 / test filter: {0}" -f $TestFilter) -ForegroundColor Yellow
}
& $TestScript @testArgs
if ($LASTEXITCODE -ne 0) {
    $failures.Add('build-and-test')
}

# --------------------------------------------------------- 6. listener check ------
Write-Head '监听端口核对 / Listener check'

$collectorExe = Join-Path $RepoRoot ("src\Collector\bin\x64\{0}\net8.0-windows\win-x64\MentorRecorder.Collector.exe" -f $Configuration)
if (Test-GateSkipped 'listener-check') { }
elseif (-not (Test-Path -LiteralPath $collectorExe)) {
    Add-Failure 'listener-check' ("找不到 Collector 可执行文件: {0}" -f $collectorExe)
}
else {
    $probeDb = Join-Path ([System.IO.Path]::GetTempPath()) ("mr-verify-{0}.db" -f ([guid]::NewGuid().ToString('N')))
    $stdout = "$probeDb.out"
    $stderr = "$probeDb.err"
    # 显式指定一次性日志目录。--db 已隐含把日志写到数据库旁边，此处再写一次是为了
    # 让读脚本的人确认探针不会写入、轮转或按保留期删除使用者自己的诊断日志。
    $probeLogs = "$probeDb.logs"
    $serve = $null
    try {
        # The single-instance lease is per user, not per database, so anything else serving
        # as this user blocks the probe. Retry for a bounded time: a Collector still shutting
        # down after this run's own tests is transient, while a developer's instance or
        # another automated session must be reported rather than killed.
        $ready = $false
        $leaseHeld = $false
        $why = ''
        $leaseDeadline = (Get-Date).AddSeconds(60)

        while (-not $ready) {
            $serve = Start-Process -FilePath $collectorExe `
                -ArgumentList @('--serve', '--db', $probeDb, '--log-dir', $probeLogs, '--json') `
                -PassThru -NoNewWindow `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr

            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline) {
                if ((Test-Path -LiteralPath $stdout) -and
                    (Get-Item -LiteralPath $stdout).Length -gt 0) {
                    $ready = $true
                    break
                }
                if ($serve.HasExited) { break }
                Start-Sleep -Milliseconds 200
            }

            if ($ready) { break }

            $why = if (Test-Path -LiteralPath $stderr) { (Get-Content -LiteralPath $stderr -Raw) } else { '' }
            $leaseHeld = $why -match 'ERR_ALREADY_RUNNING|已在本机运行'
            if (-not $leaseHeld -or (Get-Date) -ge $leaseDeadline) { break }

            Write-Host '  单实例租约被占用，等待后重试…' -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }

        if (-not $ready) {
            if ($leaseHeld) {
                foreach ($holder in @(Get-Process -Name 'MentorRecorder.Collector' -ErrorAction SilentlyContinue)) {
                    Write-Host ("  占用者: pid {0}，启动于 {1:HH:mm:ss}" -f $holder.Id, $holder.StartTime) -ForegroundColor Red
                }
                Add-Failure 'listener-check' `
                    '另一个 MentorRecorder.Collector 正在以当前用户身份服务，持有单实例租约。请先结束它再验证。'
            }
            else {
                Add-Failure 'listener-check' ("Collector 没有进入服务状态: {0}" -f $why.Trim())
            }
        }
        else {
            Write-Host ("  Collector pid {0} 正在服务命名管道。" -f $serve.Id)

            $owned = @()
            $netTcp = Get-Command 'Get-NetTCPConnection' -ErrorAction SilentlyContinue
            if ($netTcp) {
                $owned = @(Get-NetTCPConnection -OwningProcess $serve.Id -ErrorAction SilentlyContinue)
                if ($owned.Count -gt 0) {
                    foreach ($row in $owned) {
                        Write-Host ("  {0}:{1} -> {2}:{3} {4}" -f
                            $row.LocalAddress, $row.LocalPort,
                            $row.RemoteAddress, $row.RemotePort, $row.State) -ForegroundColor Red
                    }
                    Add-Failure 'listener-check' 'Collector 持有 TCP 端点；本项目只允许命名管道。'
                }
                else {
                    Write-Host '  Get-NetTCPConnection: 该进程没有任何 TCP 端点。' -ForegroundColor Green
                }
            }
            else {
                Write-Host '  Get-NetTCPConnection 不可用，改用 netstat。' -ForegroundColor Yellow
            }

            # docs/privacy-boundary.md section 9 tells the user to run exactly this, so run
            # exactly this rather than only the PowerShell cmdlet.
            $netstat = @(netstat -ano | Select-String -SimpleMatch (" " + $serve.Id) |
                Where-Object { $_.Line -match ('\s' + [regex]::Escape($serve.Id) + '\s*$') })
            if ($netstat.Count -gt 0) {
                foreach ($line in $netstat) {
                    Write-Host ("  netstat: {0}" -f $line.Line.Trim()) -ForegroundColor Red
                }
                Add-Failure 'listener-check' 'netstat -ano 显示该进程占用了 TCP/UDP 端点。'
            }
            else {
                Write-Host '  netstat -ano: 该进程一行都没有。' -ForegroundColor Green
            }
        }
    }
    finally {
        if ($serve -and -not $serve.HasExited) {
            Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue
            $serve.WaitForExit(15000) | Out-Null
        }
        # -Recurse because $probeLogs is a directory: the probe's diagnostic log lands
        # beside its throw-away database, not in the user's real log folder.
        foreach ($path in @($stdout, $stderr) + @(Get-ChildItem -Path "$probeDb*" -ErrorAction SilentlyContinue | ForEach-Object FullName)) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ------------------------------------------- 7. live capture status assertion -----
Write-Head 'LIVE_CAPTURE_STATUS 断言 / Live capture status assertion'

# The summary's LIVE_CAPTURE_STATUS line is hand-written and proves nothing on its own, so
# the value is asserted against the binary here. These are the same three assertions
# package.ps1 makes, applied to the build tree instead of the unpacked zip.
if (Test-GateSkipped 'live-capture-status') { }
elseif (-not (Test-Path -LiteralPath $collectorExe)) {
    Add-Failure 'live-capture-status' ("找不到 Collector 可执行文件: {0}" -f $collectorExe)
}
else {
    # stdout and stderr go to separate files: merging them would let an unrelated warning
    # line turn valid JSON into a parse failure, which this gate reports as "the boundary
    # assertion could not be made".
    $doctorBase = Join-Path ([System.IO.Path]::GetTempPath()) ("mr-doctor-{0}" -f ([guid]::NewGuid().ToString('N')))
    $doctorOut = "$doctorBase.out"
    $doctorErr = "$doctorBase.err"
    $doctorText = ''
    $doctorExit = -1
    try {
        # The assertion is independent of the machine it runs on.
        #
        # --capture-doctor exits 0 when this machine could capture right now and 1 when it
        # could not (no Npcap, no game client, no suitable adapter). Both are accepted; only
        # a crash or a non-JSON answer is a failure. The three tokens asserted afterwards are
        # compile-time constants in src/Collector/Capture/CaptureDiagnostics.cs
        # (LiveCaptureStatus, MonitorType, InjectedHookEnabled), describing what the build is
        # allowed to do rather than what the machine has installed, so a CI runner without
        # Npcap must produce the same three values as a developer machine.
        $run = Start-Process -FilePath $collectorExe `
            -ArgumentList @('--capture-doctor', '--json') `
            -PassThru -NoNewWindow -Wait `
            -RedirectStandardOutput $doctorOut -RedirectStandardError $doctorErr
        $doctorExit = $run.ExitCode
        if (Test-Path -LiteralPath $doctorOut) {
            $doctorText = Get-Content -LiteralPath $doctorOut -Raw -Encoding UTF8
        }
    }
    finally {
        foreach ($path in @($doctorOut, $doctorErr)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }

    if ($doctorExit -notin @(0, 1)) {
        Add-Failure 'live-capture-status' `
            ("--capture-doctor 退出码 {0}（允许 0 或 1）: {1}" -f $doctorExit, $doctorText.Trim())
    }
    else {
        $doctor = $null
        try { $doctor = $doctorText | ConvertFrom-Json }
        catch {
            Add-Failure 'live-capture-status' `
                ("--capture-doctor 的输出不是 JSON: {0}" -f $doctorText.Trim())
        }

        if ($doctor) {
            if ($doctor.live_capture_status -ne 'VERIFIED_POP_TO_EXIT') {
                Add-Failure 'live-capture-status' `
                    ("live_capture_status = {0}，应为 VERIFIED_POP_TO_EXIT（docs/live-validation-guide.md §0），" -f
                        $doctor.live_capture_status)
            }
            elseif ($doctor.boundary.injected_hook_enabled) {
                Add-Failure 'live-capture-status' '报告注入式钩子已启用；这是硬边界违规。'
            }
            elseif ($doctor.boundary.monitor_type -ne 'WinPCap') {
                Add-Failure 'live-capture-status' `
                    ("monitor_type = {0}，应为 WinPCap。" -f $doctor.boundary.monitor_type)
            }
            else {
                Write-Host ("  live_capture_status={0} monitor_type={1} injected_hook={2} (exit {3})" -f
                    $doctor.live_capture_status, $doctor.boundary.monitor_type,
                    $doctor.boundary.injected_hook_enabled, $doctorExit) -ForegroundColor Green
            }
        }
    }
}

# ------------------------------------------------- 8. injection payload scan ------
Write-Head '注入载荷扫描 / Injection payload scan'

# BOUNDARY-ALLOW: boundary enforcement, not enablement. The step proves the payload is
# absent, so it must be able to name it. See docs/privacy-boundary.md section 3.
$payloadPattern = 'deucalion*'
$skipPayloadScan = Test-GateSkipped 'injection-payload'
$scanRoots = @(
    Join-Path $RepoRoot 'src'
    Join-Path $RepoRoot 'tests'
    Join-Path $RepoRoot 'build'
    Join-Path $RepoRoot 'artifacts'
) | Where-Object { Test-Path -LiteralPath $_ }

$payloads = @()
if (-not $skipPayloadScan) {
    foreach ($root in $scanRoots) {
        $payloads += @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter $payloadPattern -ErrorAction SilentlyContinue)
    }
}

if ($skipPayloadScan) { }
elseif ($payloads.Count -gt 0) {
    foreach ($payload in $payloads) {
        Write-Host ("  {0}" -f $payload.FullName) -ForegroundColor Red
    }
    Add-Failure 'injection-payload' '输出目录里出现了注入载荷；先查清它是怎么进来的，再构建。'
}
else {
    Write-Host ("  已扫描 {0} 个目录，没有注入载荷。" -f $scanRoots.Count) -ForegroundColor Green
}

# ------------------------------------------------------- 9. licence material ------
Write-Head '许可证材料 / Licence material'

$required = @(
    @{ Path = 'LICENSE';                 Why = 'GPL-3.0-or-later 正文' }
    @{ Path = 'THIRD_PARTY_NOTICES.md';  Why = '第三方组件与许可证' }
    @{ Path = 'README.md';               Why = '边界与用途说明' }
    @{ Path = 'docs\privacy-boundary.md'; Why = '硬边界定义' }
    @{ Path = 'docs\third-party-licenses.md'; Why = '许可证分析' }
    @{ Path = 'docs\release-checklist.md';    Why = 'V1 验收清单' }
)

$missing = @()
foreach ($item in $required) {
    $full = Join-Path $RepoRoot $item.Path
    if (Test-Path -LiteralPath $full) {
        Write-Host ("  [ok]      {0}" -f $item.Path) -ForegroundColor Green
    }
    else {
        Write-Host ("  [missing] {0} — {1}" -f $item.Path, $item.Why) -ForegroundColor Red
        $missing += $item.Path
    }
}

if ($missing.Count -gt 0) {
    Add-Failure 'licence-material' '缺少必须随发布一起提供的文件。'
}

# ---------------------------------------------------------------- summary --------
Write-Head '结论 / Result'
if ($skipped.Count -gt 0) {
    Write-Host ('  已跳过的关卡 / skipped gates: {0}' -f ($skipped -join ', ')) -ForegroundColor Yellow
    Write-Host '  这次验证没有覆盖以上内容；不要把它当成一次完整验收。' -ForegroundColor Yellow
}
if ($TestFilter) {
    Write-Host ('  测试筛选 / test filter: {0}' -f $TestFilter) -ForegroundColor Yellow
}
if ($failures.Count -eq 0) {
    Write-Host '  全部通过。' -ForegroundColor Green
    # PUBLIC_DISTRIBUTION_READY is a claim about a complete run. A run that skipped a gate or
    # filtered the test suite (CI passes -TestFilter 'Category!=Soak') did not make the
    # observation the claim rests on and must not print it; the exit code is still 0 because
    # everything requested passed. Release acceptance is the unfiltered run in
    # docs/release-checklist.md.
    if ($skipped.Count -gt 0 -or $TestFilter) {
        Write-Host '  这是一次部分验证，不评定 PUBLIC_DISTRIBUTION_READY；' -ForegroundColor Yellow
        Write-Host '  发布验收要用不带 -SkipGate / -TestFilter 的完整运行。' -ForegroundColor Yellow
    }
    else {
        Write-Host '  PUBLIC_DISTRIBUTION_READY = true（见 docs/release-checklist.md）' -ForegroundColor Green
    }
    Write-Host '  LIVE_CAPTURE_STATUS       = VERIFIED_POP_TO_EXIT' -ForegroundColor Green
    exit 0
}

Write-Host ('  失败: {0}' -f ($failures -join ', ')) -ForegroundColor Red
exit 1
