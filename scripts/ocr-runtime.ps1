#Requires -Version 7.0

# Development and package staging accept only the complete local payload pinned
# below. The helper, Python runtime and models never come from the user's PATH.
. (Join-Path $PSScriptRoot 'package-runtime.ps1')

function Read-OcrDependencyManifest {
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
    $manifestPath = Join-Path $RepoRoot 'docs/licenses/ocr/dependency-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema_version -ne 2 -or $manifest.files.Count -lt 4 -or
        $manifest.cache_id -notmatch '^[A-Za-z0-9_-]+$' -or
        $manifest.runtime.entry_point -ne 'local-ai-ocr.exe' -or
        $manifest.runtime.model_directory -ne 'models' -or -not $manifest.runtime.version) {
        throw '离线 OCR 依赖清单格式无效。'
    }
    $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $components = @{}
    foreach ($component in $manifest.components) {
        if (-not $component.id -or $components.ContainsKey($component.id) -or
            -not $component.licence -or $component.texts.Count -eq 0 -or
            $component.source -notmatch '^https://') {
            throw ("OCR 组件缺少或重复来源和许可证声明: {0}" -f $component.id)
        }
        $components[$component.id] = $component
    }
    foreach ($file in $manifest.files) {
        if ($file.path -notmatch '^[A-Za-z0-9_+.-]+(?:/[A-Za-z0-9_+.-]+)*$' -or
            $file.path.Split('/') -contains '..' -or $file.path.Split('/') -contains '.' -or
            $file.path -ieq 'OCR-DEPENDENCIES.json' -or
            $file.sha256 -notmatch '^[0-9a-f]{64}$' -or
            ($file.bytes -isnot [long] -and $file.bytes -isnot [int]) -or $file.bytes -lt 0 -or
            -not $paths.Add($file.path) -or -not $components.ContainsKey($file.component) -or
            -not $file.source) {
            throw ("离线 OCR 依赖清单含有非法、重复或未归属条目: {0}" -f $file.path)
        }
    }
    foreach ($component in $manifest.components) {
        foreach ($name in $component.texts) {
            if (-not $paths.Contains('licenses/' + $name)) {
                throw ("OCR 组件缺少许可证正文: {0}" -f $name)
            }
        }
        foreach ($name in $component.embedded_in) {
            if (-not $paths.Contains($name)) {
                throw ("OCR 内嵌组件登记了未知文件: {0}" -f $name)
            }
        }
    }
    foreach ($name in @('local-ai-ocr.exe', 'models/PP-OCRv6_det_small.onnx',
            'models/PP-OCRv6_rec_small.onnx', 'models/ch_ppocr_mobile_v2.0_cls_mobile.onnx')) {
        if (-not $paths.Contains($name)) { throw ("OCR 清单缺少必需文件: {0}" -f $name) }
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
    if ([System.IO.Path]::IsPathRooted($configured)) { return [System.IO.Path]::GetFullPath($configured) }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $configured))
}

function Assert-OcrDirectoryNoLinks {
    param([Parameter(Mandatory)][string]$Directory)
    $fullPath = [System.IO.Path]::GetFullPath($Directory)
    for ($parent = $fullPath; $parent; $parent = [System.IO.Path]::GetDirectoryName($parent)) {
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw ("拒绝通过链接目录访问 OCR: {0}" -f $parent)
        }
    }
    if (Test-Path -LiteralPath $fullPath) {
        foreach ($item in Get-ChildItem -LiteralPath $fullPath -Recurse -Force) {
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw ("拒绝含链接的 OCR 目录: {0}" -f $item.FullName)
            }
        }
    }
}

function Assert-OcrRuntime {
    param(
        [Parameter(Mandatory)][string]$RuntimeDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $manifest = Read-OcrDependencyManifest -RepoRoot $RepoRoot
    Assert-OcrDirectoryNoLinks -Directory $RuntimeDirectory
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
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeDirectory -Recurse -File -Force) {
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

function Copy-PinnedOcrRuntime {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$TargetDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $manifest = Assert-OcrRuntime -RuntimeDirectory $SourceDirectory -RepoRoot $RepoRoot
    $source = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $target = [System.IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\', '/')
    $targetRoot = [System.IO.Path]::GetPathRoot($target).TrimEnd('\', '/')
    if ($target.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase) -or $target -eq '' -or
        $target.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
        $target.StartsWith($source + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($target + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒绝覆盖 OCR 源依赖或包含源依赖的目标目录。'
    }
    Assert-OcrDirectoryNoLinks -Directory $target
    if (Test-Path -LiteralPath $target) {
        $existingFiles = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force)
        if ($existingFiles.Count -gt 0) {
            # MR_OCR_DIR may point outside the default cache. Never erase an
            # arbitrary existing folder just because the configured path exists.
            $oldManifestPath = Join-Path $target 'OCR-DEPENDENCIES.json'
            if (-not (Test-Path -LiteralPath $oldManifestPath -PathType Leaf)) {
                throw 'OCR 目标含有未登记文件，拒绝覆盖；请指定新的运行目录。'
            }
            $oldManifest = Get-Content -LiteralPath $oldManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($oldManifest.schema_version -notin @(1, 2) -or -not $oldManifest.runtime.version -or
                $oldManifest.files.Count -lt 4) { throw '旧 OCR 清单无效，拒绝删除目标内容。' }
            $ownedFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            [void]$ownedFiles.Add('OCR-DEPENDENCIES.json')
            foreach ($oldFile in $oldManifest.files) {
                if ($oldFile.path -notmatch '^[A-Za-z0-9_+.-]+(?:/[A-Za-z0-9_+.-]+)*$' -or
                    $oldFile.path.Split('/') -contains '..' -or $oldFile.path.Split('/') -contains '.') {
                    throw '旧 OCR 清单含非法路径，拒绝删除目标内容。'
                }
                [void]$ownedFiles.Add($oldFile.path)
            }
            if (-not $ownedFiles.Contains('local-ai-ocr.exe') -and -not $ownedFiles.Contains('tesseract.exe')) {
                throw '目标没有登记 OCR helper，拒绝删除。'
            }
            foreach ($oldFile in $existingFiles) {
                $oldRelative = [System.IO.Path]::GetRelativePath($target, $oldFile.FullName).Replace('\', '/')
                if (-not $ownedFiles.Contains($oldRelative)) {
                    throw ("OCR 目标含未登记的本地文件，拒绝删除: {0}" -f $oldRelative)
                }
            }
        }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in $manifest.files) {
        $destination = Join-Path $target $file.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $destination -Force
    }
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs/licenses/ocr/dependency-manifest.json') `
        -Destination (Join-Path $target 'OCR-DEPENDENCIES.json') -Force
    Assert-OcrRuntime -RuntimeDirectory $target -RepoRoot $RepoRoot | Out-Null
    return $manifest
}

function Install-OcrRuntime {
    param(
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $destinationRoot = [System.IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\', '/')
    if ($destinationRoot -match '^[A-Za-z]:$') { throw '拒绝在磁盘根目录部署 OCR。' }
    $destination = [System.IO.Path]::GetFullPath((Join-Path $destinationRoot 'ocr'))
    if (-not $destination.StartsWith($destinationRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'OCR 目标不在指定部署目录中。' }
    $manifest = Copy-PinnedOcrRuntime -SourceDirectory (Get-OcrRuntimeDirectory -RepoRoot $RepoRoot) `
        -TargetDirectory $destination -RepoRoot $RepoRoot
    Write-Host ("  [ok] 离线 OCR {0}: {1} 个固定文件" -f $manifest.runtime.version, $manifest.files.Count) -ForegroundColor Green
}

function Invoke-OcrRuntimeProcess {
    param(
        [Parameter(Mandatory)][string]$RuntimeDirectory,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $info = New-IsolatedPackageStartInfo -Executable (Join-Path $RuntimeDirectory 'local-ai-ocr.exe') `
        -WorkingDirectory $RuntimeDirectory -Arguments $Arguments
    foreach ($name in @($info.get_EnvironmentVariables().Keys)) {
        if ($name -match '^(PYTHON|TESSDATA|OMP_|OPENBLAS_|MKL_|NUMEXPR_)') {
            $info.get_EnvironmentVariables().Remove($name)
        }
    }
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
    if ($version.Stdout.Trim() -ne $manifest.runtime.version) { throw '离线 OCR 报告的版本不符合固定清单。' }
    # A version string alone does not prove bundled libraries can infer.
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
        $output = Join-Path $scratch 'output.tsv'
        Invoke-OcrRuntimeProcess -RuntimeDirectory $RuntimeDirectory -Arguments @('--input', $image,
            '--output', $output, '--models', (Join-Path $RuntimeDirectory 'models')) | Out-Null
        if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or (Get-Item -LiteralPath $output).Length -gt 4MB) {
            throw '离线 OCR 没有生成有界 TSV 输出。'
        }
        $tsv = Get-Content -LiteralPath $output -Raw -Encoding UTF8
        $recognizedWords = foreach ($line in $tsv -split '\r?\n') {
            $columns = $line -split "`t", 12
            if ($columns.Count -eq 12 -and $columns[0] -eq '5') {
                foreach ($index in 6..9) {
                    $value = 0
                    if (-not [int]::TryParse($columns[$index], [ref]$value) -or $value -lt 0) {
                        throw '离线 OCR TSV 含非法矩形坐标。'
                    }
                }
                if ([int]$columns[6] + [int]$columns[8] -gt 1000 -or
                    [int]$columns[7] + [int]$columns[9] -gt 180) { throw 'OCR 坐标超出原图。' }
                $columns[11]
            }
        }
        $recognizedText = ($recognizedWords -join '') -replace '\s', ''
        if ($tsv -notmatch '^level\tpage_num\t' -or
            -not $recognizedText.Contains('MentorRecorder1234567890') -or
            -not $recognizedText.Contains('中文截图导入记录')) {
            throw '离线 OCR 未实际识别本地中文/英文测试图。'
        }
        Write-Host '  [ok] 离线 OCR 固定模型及中文 TSV 识别（自包含子进程，PATH 仅含 Windows 系统目录）' -ForegroundColor Green
        return [pscustomobject]@{ Version = $manifest.runtime.version; Tsv = $tsv }
    }
    finally {
        $fullScratch = [System.IO.Path]::GetFullPath($scratch)
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($fullScratch.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($fullScratch).StartsWith('mr-ocr-probe-', [StringComparison]::Ordinal)) {
            Assert-OcrDirectoryNoLinks -Directory $fullScratch
            Remove-Item -LiteralPath $fullScratch -Recurse -Force
        }
    }
}
