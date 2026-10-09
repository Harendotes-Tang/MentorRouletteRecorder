#Requires -Version 7.0

# Offline OCR deployment is shared by development builds and release staging.
# Both helpers consume local, hash-pinned inputs only. Dependency source URLs
# live in the manifest for manual provisioning; the application stays offline.
. (Join-Path $PSScriptRoot 'package-runtime.ps1')

function Read-OcrDependencyManifest {
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
    $manifestPath = Join-Path $RepoRoot 'docs/licenses/ocr/dependency-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema_version -ne 1 -or $manifest.files.Count -lt 3) {
        throw '离线 OCR 依赖清单格式无效。'
    }
    $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ($file.path -notmatch '^[A-Za-z0-9_+.-]+(?:/[A-Za-z0-9_+.-]+)*$' -or
            $file.path.Split('/') -contains '..' -or
            $file.sha256 -notmatch '^[0-9a-f]{64}$' -or -not $paths.Add($file.path)) {
            throw ("离线 OCR 依赖清单含有非法或重复条目: {0}" -f $file.path)
        }
    }
    $covered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($component in $manifest.components) {
        if (-not $component.licence -or $component.texts.Count -eq 0) {
            throw ("OCR 组件缺少许可证声明: {0}" -f $component.name)
        }
        foreach ($name in $component.files) {
            if (-not $paths.Contains($name) -or -not $covered.Add($name)) {
                throw ("OCR 组件登记含重复或未知文件: {0}" -f $name)
            }
        }
        foreach ($name in $component.texts) {
            if (-not $paths.Contains('licenses/' + $name)) {
                throw ("OCR 组件缺少许可证正文: {0}" -f $name)
            }
        }
    }
    foreach ($file in $manifest.files | Where-Object origin -NE 'repository') {
        if (-not $covered.Contains($file.path)) {
            throw ("OCR 文件没有所属组件和许可证: {0}" -f $file.path)
        }
    }
    return $manifest
}

function Get-OcrRuntimeDirectory {
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
    $configured = [Environment]::GetEnvironmentVariable('MR_OCR_DIR')
    if ([string]::IsNullOrWhiteSpace($configured)) {
        $manifest = Read-OcrDependencyManifest -RepoRoot $RepoRoot
        return Join-Path $RepoRoot ("artifacts/dependencies/ocr/{0}/runtime" -f $manifest.cache_id)
    }
    if ([System.IO.Path]::IsPathRooted($configured)) {
        return [System.IO.Path]::GetFullPath($configured)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $configured))
}

function Assert-OcrRuntime {
    param(
        [Parameter(Mandatory)][string]$RuntimeDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $manifest = Read-OcrDependencyManifest -RepoRoot $RepoRoot
    foreach ($file in $manifest.files) {
        $path = Join-Path $RuntimeDirectory $file.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw ("离线 OCR 缺少 {0}。先运行 scripts/bootstrap-ocr.ps1；MR_OCR_DIR 可指定已核验的离线依赖目录。" -f $file.path)
        }
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw ("离线 OCR 文件与固定清单不一致，拒绝使用: {0}" -f $file.path)
        }
    }
    $expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) { [void]$expected.Add($file.path) }
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeDirectory -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($RuntimeDirectory, $file.FullName).Replace('\', '/')
        if ($relative -ne 'OCR-DEPENDENCIES.json' -and -not $expected.Contains($relative)) {
            throw ("离线 OCR 目录含有清单之外的文件，拒绝部署: {0}" -f $relative)
        }
    }
    $deployedManifest = Join-Path $RuntimeDirectory 'OCR-DEPENDENCIES.json'
    if (Test-Path -LiteralPath $deployedManifest) {
        $sourceManifest = Join-Path $RepoRoot 'docs/licenses/ocr/dependency-manifest.json'
        if ((Get-FileHash -LiteralPath $deployedManifest).Hash -ne (Get-FileHash -LiteralPath $sourceManifest).Hash) {
            throw '已部署的 OCR 依赖清单与当前源码清单不一致。'
        }
    }
    return $manifest
}

function Install-OcrRuntime {
    param(
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $source = Get-OcrRuntimeDirectory -RepoRoot $RepoRoot
    $manifest = Assert-OcrRuntime -RuntimeDirectory $source -RepoRoot $RepoRoot
    $destinationRoot = [System.IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\', '/')
    $sourceRoot = [System.IO.Path]::GetFullPath($source).TrimEnd('\', '/')
    $destination = [System.IO.Path]::GetFullPath((Join-Path $destinationRoot 'ocr'))
    if (-not $destination.StartsWith($destinationRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        $destination.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($sourceRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($destination + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $destinationRoot -match '^[A-Za-z]:$') {
        throw '拒绝覆盖 OCR 源依赖或目标目录之外的路径。'
    }
    # Only this owned code-resource child is replaced. Reject links before any
    # recursive delete so an overridden build directory cannot redirect cleanup.
    for ($parent = $destinationRoot; $parent; $parent = [System.IO.Path]::GetDirectoryName($parent)) {
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw ("拒绝通过链接目录部署 OCR: {0}" -f $parent)
        }
    }
    if (Test-Path -LiteralPath $destination) {
        $linked = @(Get-Item -LiteralPath $destination -Force) + @(Get-ChildItem -LiteralPath $destination -Recurse -Force)
        if ($linked | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }) {
            throw '拒绝删除含链接的旧 OCR 目录。'
        }
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $manifest.files) {
        $target = Join-Path $destination $file.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $target -Force
    }
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs/licenses/ocr/dependency-manifest.json') `
        -Destination (Join-Path $destination 'OCR-DEPENDENCIES.json') -Force
    Assert-OcrRuntime -RuntimeDirectory $destination -RepoRoot $RepoRoot | Out-Null
    Write-Host ("  [ok] 离线 OCR {0}: {1} 个固定文件" -f $manifest.runtime.version, $manifest.files.Count) -ForegroundColor Green
}

function Invoke-OcrRuntimeProcess {
    param(
        [Parameter(Mandatory)][string]$RuntimeDirectory,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $info = New-IsolatedPackageStartInfo -Executable (Join-Path $RuntimeDirectory 'tesseract.exe') `
        -WorkingDirectory $RuntimeDirectory -Arguments $Arguments
    $info.get_EnvironmentVariables().Remove('TESSDATA_PREFIX')
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw '离线 OCR 验证超过 30 秒，进程已停止。'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw ("离线 OCR 验证失败 (exit {0}): {1}" -f $process.ExitCode, $stderr.Trim())
        }
        return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr }
    }
    finally { $process.Dispose() }
}

function Test-OcrRuntime {
    param(
        [Parameter(Mandatory)][string]$RuntimeDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $manifest = Assert-OcrRuntime -RuntimeDirectory $RuntimeDirectory -RepoRoot $RepoRoot
    $version = Invoke-OcrRuntimeProcess -RuntimeDirectory $RuntimeDirectory -Arguments @('--version')
    if (($version.Stdout + $version.Stderr) -notmatch ('(?m)^tesseract v' + [regex]::Escape($manifest.runtime.version) + '\r?$')) {
        throw '离线 OCR 报告的版本不符合固定清单。'
    }
    $modelDirectory = Join-Path $RuntimeDirectory 'tessdata'
    $languages = Invoke-OcrRuntimeProcess -RuntimeDirectory $RuntimeDirectory `
        -Arguments @('--tessdata-dir', $modelDirectory, '--list-langs')
    foreach ($language in @('chi_sim', 'eng')) {
        if ($languages.Stdout -notmatch ('(?m)^' + $language + '\r?$')) {
            throw ("离线 OCR 没有加载随包模型: {0}" -f $language)
        }
    }
    # Exercise model loading and TSV configuration with real local Chinese text,
    # under the same isolated PATH used for unpacked release probes.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('mr-ocr-probe-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        Add-Type -AssemblyName System.Drawing
        $bitmap = [System.Drawing.Bitmap]::new(1000, 180)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $font = [System.Drawing.Font]::new('Microsoft YaHei', 36, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.DrawString('中文截图 导入记录', $font, [System.Drawing.Brushes]::Black, 24, 20)
            $graphics.DrawString('MentorRecorder 1234567890', $font, [System.Drawing.Brushes]::Black, 24, 90)
            $image = Join-Path $scratch 'input.png'
            $bitmap.Save($image, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
        $recognition = Invoke-OcrRuntimeProcess -RuntimeDirectory $RuntimeDirectory `
            -Arguments @($image, 'stdout', '--tessdata-dir', $modelDirectory, '-l', 'chi_sim+eng', '--psm', '11', 'tsv')
        $recognizedWords = foreach ($line in $recognition.Stdout -split '\r?\n') {
            $columns = $line -split "`t", 12
            if ($columns.Count -eq 12 -and $columns[0] -eq '5') { $columns[11] }
        }
        $recognizedText = $recognizedWords -join ''
        if ($recognition.Stdout -notmatch '^level\tpage_num\t' -or
            -not $recognizedText.Contains('MentorRecorder1234567890') -or
            -not $recognizedText.Contains('中文截图导入记录')) {
            throw '离线 OCR 未实际识别本地中文/英文测试图或 TSV 配置不完整。'
        }
        Write-Host '  [ok] 离线 OCR 版本、双语言及中文 TSV 识别（子进程 PATH 仅含 Windows 系统目录）' -ForegroundColor Green
        return [pscustomobject]@{ Version = $manifest.runtime.version; Languages = @('chi_sim', 'eng'); Tsv = $recognition.Stdout }
    }
    finally {
        $fullScratch = [System.IO.Path]::GetFullPath($scratch)
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($fullScratch.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($fullScratch).StartsWith('mr-ocr-probe-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $fullScratch -Recurse -Force
        }
    }
}
