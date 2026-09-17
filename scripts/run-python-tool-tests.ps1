#Requires -Version 5.1
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
    退出码：0 表示全部通过；1 表示有脚本失败、未发现任何脚本，或未找到 Python
    且未指定 -SkipIfNoPython。缺少 Python 按失败处理，静默跳过的闸门等于没有闸门。

.EXAMPLE
    pwsh -NoProfile -File scripts/run-python-tool-tests.ps1
#>
[CmdletBinding()]
param(
    # 未找到 Python 时以退出码 0 跳过，代表接受这些闸门在本机未被验证。
    [switch]$SkipIfNoPython
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 so a legacy code page (e.g. GBK) does not mangle the Chinese
# output. Failure to set it is not fatal.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolsRoot = Join-Path $RepoRoot 'tools'

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
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'

$failed = New-Object System.Collections.Generic.List[string]
# Logged so a missing-module failure can be traced to the interpreter that ran.
Write-Host ("  Python: {0}" -f $python.Source)
try {
    foreach ($script in $scripts) {
        $relative = $script.FullName.Substring($RepoRoot.Length).TrimStart('\')
        Write-Host ''
        Write-Host ("-- {0}" -f $relative) -ForegroundColor DarkCyan

        & $python.Source $script.FullName
        if ($LASTEXITCODE -ne 0) {
            Write-Host ("   失败（退出码 {0}）: {1}" -f $LASTEXITCODE, $relative) -ForegroundColor Red
            $failed.Add($relative)
        }
    }
}
finally {
    $env:PYTHONIOENCODING = $previousIoEncoding
    $env:PYTHONUTF8 = $previousUtf8Mode
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ("  工具自测失败 {0}/{1}: {2}" -f
        $failed.Count, $scripts.Count, ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host ("  工具自测全部通过（{0} 个脚本）。" -f $scripts.Count) -ForegroundColor Green
exit 0
