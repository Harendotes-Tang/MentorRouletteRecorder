#Requires -Version 7.0
<#
.SYNOPSIS
    校验并准备已在本机构建的自包含 CPU OCR，不安装软件、不联网。
.DESCRIPTION
    从 -RuntimeDirectory 指定的固定 payload 复制到缓存或 MR_OCR_DIR。
    缺少文件、额外文件、哈希不符或链接目录均拒绝部署。构建开发依赖是独立步骤；
    此脚本及应用运行时不下载模型，也不依赖用户安装的 Python。
.EXAMPLE
    pwsh -File scripts/bootstrap-ocr.ps1 -RuntimeDirectory artifacts/ocr-punctuation-20261010/runtime-reviewed
#>
[CmdletBinding()]
param([string]$RuntimeDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ocr-runtime.ps1')
$ocrRepoRoot = Split-Path -Parent $PSScriptRoot
$ocrRuntime = Get-OcrRuntimeDirectory -RepoRoot $ocrRepoRoot

if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    Assert-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
    Test-OcrRuntime -RuntimeDirectory $ocrRuntime -RepoRoot $ocrRepoRoot | Out-Null
}
else {
    $ocrSource = [System.IO.Path]::GetFullPath($RuntimeDirectory)
    $ocrTarget = [System.IO.Path]::GetFullPath($ocrRuntime)
    if (-not $ocrSource.Equals($ocrTarget, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-PinnedOcrRuntime -SourceDirectory $ocrSource -TargetDirectory $ocrTarget -RepoRoot $ocrRepoRoot | Out-Null
    }
    Test-OcrRuntime -RuntimeDirectory $ocrTarget -RepoRoot $ocrRepoRoot | Out-Null
}
Write-Host ("离线 OCR 已就绪: {0}" -f $ocrRuntime) -ForegroundColor Green
