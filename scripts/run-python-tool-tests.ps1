#Requires -Version 7.0
<#
.SYNOPSIS
    运行 tools/ 下的 Python 自测 / Run the Python self-tests under tools/.

.DESCRIPTION
    逐个执行 tools/ 下的 Python 自测脚本。这些自测不在 dotnet test 与 ctest 的覆盖
    范围内，却守护着发布级约束：static-boundary-check 检查 docs/privacy-boundary.md
    的硬边界，oodle-signature-finder 产出真机加载的签名档案，duty-data-generator
    生成随包发布的副本参考数据。

    发现规则（不写死清单，新增工具测试自动纳入）：
      * `tools/**/selftest.py`
      * `tools/**/test_*.py`

    解释器：优先 MR_PYTHON_EXE，否则取 PATH 上的 python 或 python3。
    运行任何脚本之前，先在该解释器中逐个导入 tools/requirements-ci.txt 列出的包
    （CI 安装的正是这份清单）；缺少任何一个即失败，并给出安装方法，而不是让某个
    测试脚本在日志深处以“退出码 1”失败。
    unittest 报告的跳过数（skipped=N）按脚本汇总在末尾：跳过的测试没有验证任何东西，
    不能算作“全部通过”，但也不改变退出码。
    退出码：0 表示没有失败（可能有跳过）；1 表示有脚本失败、缺少所需的包、未发现任何脚本，或未找到
    Python 且未指定 -SkipIfNoPython。缺少 Python 按失败处理，静默跳过的闸门等于没有闸门。

.EXAMPLE
    pwsh -NoProfile -File scripts/run-python-tool-tests.ps1
#>
[CmdletBinding()]
param(
    # 未找到 Python 时以退出码 0 跳过，代表接受这些闸门在本机未被验证。
    [switch]$SkipIfNoPython,

    # Optional in-process result for verify.ps1; logs and exit 0 alone cannot distinguish skips.
    [ref]$Result
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $RepoRoot 'tools'
if ($null -ne $Result) {
    $Result.Value = [pscustomobject]@{
        Succeeded = $false; Complete = $false; ScriptCount = 0; FailedScriptCount = 0; SkippedTestCount = 0
    }
}

function Resolve-Python {
    # MR_PYTHON_EXE pins the interpreter: CI installs the signature tool's
    # capstone / pefile into one Python, which is not necessarily the `python`
    # first on PATH.
    if ($env:MR_PYTHON_EXE) {
        if (-not (Test-Path -LiteralPath $env:MR_PYTHON_EXE)) {
            Write-Host ("  MR_PYTHON_EXE 指向的解释器不存在: {0}" -f $env:MR_PYTHON_EXE) -ForegroundColor Red
            exit 1
        }
        return Get-Command $env:MR_PYTHON_EXE
    }
    $python = Get-Command 'python' -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command 'python3' -ErrorAction SilentlyContinue }
    return $python
}

$python = Resolve-Python
if (-not $python) {
    if ($SkipIfNoPython) {
        if ($null -ne $Result) { $Result.Value.Succeeded = $true }
        Write-Host '  未找到 Python 3；按 -SkipIfNoPython 跳过工具自测。' -ForegroundColor Yellow
        Write-Host '  这台机器上的边界与签名闸门因此没有被验证过。' -ForegroundColor Yellow
        exit 0
    }
    Write-Host '  未找到 Python 3。tools/ 下的自测是强制的，不能跳过。' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $ToolsRoot)) {
    Write-Host ("  找不到 tools 目录: {0}" -f $ToolsRoot) -ForegroundColor Red
    exit 1
}

# Preflight (audit 2026-10-20, N2-6): import every package tools/requirements-ci.txt lists, in
# the interpreter that will run the tests. A distribution whose module has another name is
# mapped here; every other name is imported with '-' and '.' read as '_'.
$Requirements = Join-Path $ToolsRoot 'requirements-ci.txt'
if (-not (Test-Path -LiteralPath $Requirements -PathType Leaf)) {
    Write-Host ("  找不到工具自测的依赖清单: {0}" -f $Requirements) -ForegroundColor Red
    exit 1
}
$preflight = @'
import importlib, re, sys
MODULES = {'opencv-python-headless': 'cv2', 'opencv-python': 'cv2'}
missing = 0
with open(sys.argv[1], encoding='utf-8') as handle:
    for raw in handle:
        line = raw.split('#', 1)[0].strip()
        if not line or line.startswith('-'):
            continue
        name = re.split(r'[\s\[<>=!~;@]', line, maxsplit=1)[0]
        module = MODULES.get(name.lower(), re.sub(r'[-.]', '_', name.lower()))
        try:
            importlib.import_module(module)
        except Exception as error:
            missing += 1
            print('    缺少 {0}（import {1}: {2}: {3}）'.format(name, module, type(error).__name__, error))
sys.exit(1 if missing else 0)
'@
$previousPreflightIoEncoding = $env:PYTHONIOENCODING
$env:PYTHONIOENCODING = 'utf-8'
try {
    & $python.Source -c $preflight $Requirements
    $preflightExit = $LASTEXITCODE
}
finally {
    $env:PYTHONIOENCODING = $previousPreflightIoEncoding
}
if ($preflightExit -ne 0) {
    Write-Host ("  工具自测所需的 Python 包没有全部装在这个解释器里: {0}" -f $python.Source) -ForegroundColor Red
    Write-Host '  请运行 python -m pip install -r tools/requirements-ci.txt（最好在虚拟环境中），' -ForegroundColor Red
    Write-Host '  或把 MR_PYTHON_EXE 指向已安装这些包的解释器，然后重新运行。' -ForegroundColor Red
    exit 1
}

$scripts = @(
    Get-ChildItem -LiteralPath $ToolsRoot -Recurse -File -Include 'selftest.py', 'test_*.py' |
        Where-Object { $_.FullName -notmatch '\\__pycache__\\' } |
        Sort-Object FullName
)

if ($scripts.Count -eq 0) {
    # An empty discovery would otherwise report success; an empty test run must
    # never count as a pass.
    Write-Host '  tools/ 下一个自测脚本都没有找到；空测试不算成功。' -ForegroundColor Red
    exit 1
}

# Python's stdout encoding follows the code page unless overridden, and several of
# these tests print Chinese. The previous values are restored in the finally block
# so this script stays free of side effects when called from another script.
$previousIoEncoding = $env:PYTHONIOENCODING
$previousUtf8Mode = $env:PYTHONUTF8
$previousUnbuffered = $env:PYTHONUNBUFFERED
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
# The output is read line by line below; unbuffered, it still appears as the tests run.
$env:PYTHONUNBUFFERED = '1'

$failed = New-Object System.Collections.Generic.List[string]
# Tests a script skipped (unittest's "OK (skipped=N)" / "FAILED (..., skipped=N)"): each one is
# a check that did not run on this machine, such as a class that needs a missing tool.
$skipped = New-Object System.Collections.Generic.List[object]
# Logged so a missing-module failure can be traced to the interpreter that ran.
Write-Host ("  Python: {0}" -f $python.Source)
try {
    foreach ($script in $scripts) {
        $relative = $script.FullName.Substring($RepoRoot.Length).TrimStart('\')
        Write-Host ''
        Write-Host ("-- {0}" -f $relative) -ForegroundColor DarkCyan

        $lines = @(& $python.Source $script.FullName 2>&1 | ForEach-Object {
                $text = "$_"
                Write-Host $text
                $text
            })
        $exitCode = $LASTEXITCODE
        $skippedHere = 0
        foreach ($line in $lines) {
            if ($line -match '^(?:OK|FAILED) \(.*\bskipped=(\d+)') { $skippedHere += [int]$Matches[1] }
        }
        if ($skippedHere -gt 0) {
            $skipped.Add([pscustomobject]@{ Script = $relative; Count = $skippedHere })
        }
        if ($exitCode -ne 0) {
            Write-Host ("   失败（退出码 {0}）: {1}" -f $exitCode, $relative) -ForegroundColor Red
            $failed.Add($relative)
        }
    }
}
finally {
    $env:PYTHONIOENCODING = $previousIoEncoding
    $env:PYTHONUTF8 = $previousUtf8Mode
    $env:PYTHONUNBUFFERED = $previousUnbuffered
}

Write-Host ''
$skippedTotal = 0
if ($skipped.Count -gt 0) {
    Write-Host '  以下脚本有测试被跳过，这些测试在本机没有验证任何东西：' -ForegroundColor Yellow
    foreach ($entry in $skipped) {
        Write-Host ("    {0}：跳过 {1} 项" -f $entry.Script, $entry.Count) -ForegroundColor Yellow
        $skippedTotal += $entry.Count
    }
}
if ($null -ne $Result) {
    $Result.Value = [pscustomobject]@{
        Succeeded = $failed.Count -eq 0
        Complete = $failed.Count -eq 0 -and $skippedTotal -eq 0
        ScriptCount = $scripts.Count
        FailedScriptCount = $failed.Count
        SkippedTestCount = $skippedTotal
    }
}
if ($failed.Count -gt 0) {
    Write-Host ("  工具自测失败 {0}/{1}: {2}" -f
        $failed.Count, $scripts.Count, ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

if ($skippedTotal -gt 0) {
    Write-Host ("  工具自测没有失败（{0} 个脚本），但有 {1} 项测试被跳过，见上。" -f
        $scripts.Count, $skippedTotal) -ForegroundColor Yellow
    exit 0
}
Write-Host ("  工具自测全部通过（{0} 个脚本）。" -f $scripts.Count) -ForegroundColor Green
exit 0
