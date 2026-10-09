#Requires -Version 7.0
<#
.SYNOPSIS
    准备固定的离线 OCR 依赖，不安装软件、不执行安装器、不改变 PATH。
.DESCRIPTION
    校验已下载到本机的 UB Mannheim 固定 Windows 发行包及官方 chi_sim/eng 模型，
    使用已安装的 7-Zip 只提取清单列出的运行文件，附上仓库中的许可证。
    完成后 build.ps1/package.ps1 只做本地部署，应用运行时不联网下载。
    默认缓存位于已忽略的 artifacts/dependencies/ocr；MR_OCR_DIR 可覆盖运行目录。
.EXAMPLE
    pwsh -File scripts/bootstrap-ocr.ps1 -SevenZipExe C:/Tools/7-Zip/7z.exe `
        -RuntimeArchive C:/Downloads/tesseract-ocr-w64-setup-5.4.0.20240606.exe `
        -ModelDirectory C:/Downloads/tessdata
#>
[CmdletBinding()]
param(
    [string]$SevenZipExe = [Environment]::GetEnvironmentVariable('MR_7ZIP_EXE'),
    [string]$RuntimeArchive,
    [string]$ModelDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ocr-runtime.ps1')
$ocrRepoRoot = Split-Path -Parent $PSScriptRoot
$ocrManifest = Read-OcrDependencyManifest -RepoRoot $ocrRepoRoot
$ocrRuntime = Get-OcrRuntimeDirectory -RepoRoot $ocrRepoRoot

if ([string]::IsNullOrWhiteSpace($RuntimeArchive) -and [string]::IsNullOrWhiteSpace($ModelDirectory) -and
    (Test-Path -LiteralPath (Join-Path $ocrRuntime 'tesseract.exe'))) {
    try {
        Assert-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
        Test-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
        Write-Host ("离线 OCR 已就绪: {0}" -f $ocrRuntime) -ForegroundColor Green
        exit 0
    }
    catch { Write-Host '现有 OCR 目录不完整，将按固定清单补齐。' -ForegroundColor Yellow }
}

if ([string]::IsNullOrWhiteSpace($SevenZipExe)) {
    $sevenZipCommand = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($sevenZipCommand) { $SevenZipExe = $sevenZipCommand.Source }
}
if (-not $SevenZipExe -or -not (Test-Path -LiteralPath $SevenZipExe -PathType Leaf)) {
    throw '需要本地 7-Zip 提取 NSIS 包。请用 -SevenZipExe 或 MR_7ZIP_EXE 指定 7z.exe；此脚本不会执行或安装下载的安装器。'
}

$ocrCache = Join-Path $ocrRepoRoot ("artifacts/dependencies/ocr/{0}" -f $ocrManifest.cache_id)
New-Item -ItemType Directory -Path $ocrCache -Force | Out-Null

function Assert-PinnedOcrInput([string]$Path, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("缺少本地 OCR 输入: {0}。请按 docs/licenses/ocr/dependency-manifest.json 的 URL 和 SHA256 手动获取，并用 -RuntimeArchive/-ModelDirectory 指定。" -f $Path)
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) {
        throw ("本地 OCR 输入哈希与固定清单不一致，拒绝提取: {0}" -f $Path)
    }
}

$ocrArchive = if ([string]::IsNullOrWhiteSpace($RuntimeArchive)) {
    Join-Path $ocrCache $ocrManifest.runtime.archive_name
} else { [System.IO.Path]::GetFullPath($RuntimeArchive) }
$ocrModelInput = if ([string]::IsNullOrWhiteSpace($ModelDirectory)) {
    Join-Path $ocrCache 'models'
} else { [System.IO.Path]::GetFullPath($ModelDirectory) }
Assert-PinnedOcrInput $ocrArchive $ocrManifest.runtime.archive_sha256
foreach ($file in $ocrManifest.files | Where-Object origin -EQ 'model') {
    Assert-PinnedOcrInput (Join-Path $ocrModelInput (Split-Path -Leaf $file.path)) $file.sha256
}
$ocrExtraction = Join-Path $ocrCache ('extract-' + [guid]::NewGuid().ToString('N'))
try {
    $ocrArchiveEntries = @($ocrManifest.files | Where-Object origin -EQ 'runtime-archive' | ForEach-Object source_path)
    & $SevenZipExe x $ocrArchive ("-o{0}" -f $ocrExtraction) '-y' '-bso0' '-bsp0' @ocrArchiveEntries
    if ($LASTEXITCODE -ne 0) { throw '7-Zip 提取固定 OCR 发行包失败。' }
    New-Item -ItemType Directory -Path $ocrRuntime -Force | Out-Null
    foreach ($file in $ocrManifest.files) {
        $target = Join-Path $ocrRuntime $file.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        switch ($file.origin) {
            'runtime-archive' { Copy-Item -LiteralPath (Join-Path $ocrExtraction $file.source_path) -Destination $target -Force }
            'model' { Copy-Item -LiteralPath (Join-Path $ocrModelInput (Split-Path -Leaf $file.path)) -Destination $target -Force }
            'repository' {
                $source = Join-Path $ocrRepoRoot $file.source_path
                if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
                    throw ("仓库 OCR 许可证与固定清单不一致: {0}" -f $file.source_path)
                }
                Copy-Item -LiteralPath $source -Destination $target -Force
            }
            default { throw ("未知 OCR 文件来源: {0}" -f $file.origin) }
        }
    }
    Copy-Item -LiteralPath (Join-Path $ocrRepoRoot 'docs/licenses/ocr/dependency-manifest.json') `
        -Destination (Join-Path $ocrRuntime 'OCR-DEPENDENCIES.json') -Force
    Assert-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
    Test-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
}
finally {
    $ocrCachePrefix = [System.IO.Path]::GetFullPath($ocrCache).TrimEnd('\') + '\'
    $ocrExtractionPath = [System.IO.Path]::GetFullPath($ocrExtraction)
    if ($ocrExtractionPath.StartsWith($ocrCachePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($ocrExtractionPath).StartsWith('extract-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $ocrExtractionPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host ("离线 OCR 已就绪: {0}" -f $ocrRuntime) -ForegroundColor Green
