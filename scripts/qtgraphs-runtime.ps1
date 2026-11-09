#Requires -Version 7.0

# One recipe serves Qt Creator, scripted builds and the packaged application.
# The Qt SDK is read-only; only a project-specific, verified DLL is deployed.
function Read-QtGraphsRecipe {
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
    $directory = Join-Path $RepoRoot 'docs/licenses/qtgraphs'
    $path = Join-Path $directory 'dependency-manifest.json'
    $recipe = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($recipe.schema_version -ne 1 -or $recipe.qt_version -ne '6.11.2' -or
        $recipe.source_commit -notmatch '^[a-f0-9]{40}$' -or
        $recipe.source_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $recipe.patch_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $recipe.original_file_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $recipe.patched_file_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $recipe.patch_file -ne 'qtgraphs-d3d11.patch' -or
        $recipe.source_archive -ne 'qtgraphs-source.tar.gz' -or
        $recipe.patched_file -ne 'src/common/utils/commonutils.cpp' -or
        $recipe.cache_id -notmatch '^[A-Za-z0-9_-]+$' -or
        $recipe.source_url -ne ('https://codeload.github.com/qt/qtgraphs/tar.gz/' + $recipe.source_commit)) {
        throw 'QtGraphs 修补清单格式无效。'
    }
    $patch = Join-Path $directory $recipe.patch_file
    if ((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $recipe.patch_sha256) {
        throw 'QtGraphs 补丁与固定清单不一致。'
    }
    return $recipe
}

function Get-QtGraphsMaterialNames {
    return @('dependency-manifest.json', 'qtgraphs-d3d11.patch', 'GPL-3.0-only.txt', 'README.md')
}

function Get-QtGraphsTextHash {
    param([Parameter(Mandatory)][string]$Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '').ToLowerInvariant()
    } finally { $algorithm.Dispose() }
}

function Test-QtGraphsPatchApplicable {
    param([Parameter(Mandatory)][string]$QtPrefix)
    $dll = Join-Path $QtPrefix 'bin/Qt6Graphs.dll'
    return (Test-Path -LiteralPath $dll) -and ((Get-Item -LiteralPath $dll).VersionInfo.FileVersion -match '^6\.11\.2(?:\D|$)')
}

function Assert-QtGraphsWritableDirectory {
    param([Parameter(Mandatory)][string]$Directory,
          [Parameter(Mandatory)][string]$QtPrefix, [string]$CacheRoot)
    $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $prefix = [IO.Path]::GetFullPath($QtPrefix).TrimEnd('\', '/')
    if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\', '/') -or
        $target.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        $target.StartsWith($prefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'QtGraphs 写入目标不能是根目录或 Qt SDK。'
    }
    if ($CacheRoot) {
        $cache = [IO.Path]::GetFullPath($CacheRoot).TrimEnd('\', '/')
        if ($target.Equals($cache, [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith($cache + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'QtGraphs 部署目标不能是构建缓存。'
        }
    }
    # A junction alias must not bypass the read-only SDK/cache boundaries.
    for ($parent = $target; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
        $item = Get-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "QtGraphs 写入目标或祖先包含链接目录：$parent"
        }
        if ($null -ne $item -and $item.LinkType -eq 'HardLink') {
            throw "QtGraphs 写入目标包含硬链接：$parent"
        }
    }
}

function Get-QtGraphsFeatures {
    param([Parameter(Mandatory)][string]$Path)
    $features = @([IO.File]::ReadAllLines($Path) | ForEach-Object {
        if ($_ -match '^#define (QT_FEATURE_graphs_\w+) (-?1)$') { $Matches[1] + '=' + $Matches[2] }
    } | Sort-Object)
    if ($features.Count -ne 12) { throw "QtGraphs 功能清单不完整：$Path" }
    return $features
}

function Get-QtGraphsContext {
    param(
        [Parameter(Mandatory)][string]$QtPrefix,
        [Parameter(Mandatory)][string]$CompilerPath,
        [Parameter(Mandatory)][string]$CMakeExecutable,
        [Parameter(Mandatory)][string]$NinjaExecutable,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $recipe = Read-QtGraphsRecipe -RepoRoot $RepoRoot
    $prefix = [IO.Path]::GetFullPath($QtPrefix)
    $compiler = [IO.Path]::GetFullPath($CompilerPath)
    $cmake = [IO.Path]::GetFullPath($CMakeExecutable)
    $ninja = [IO.Path]::GetFullPath($NinjaExecutable)
    $originalDll = Join-Path $prefix 'bin/Qt6Graphs.dll'
    $featureHeader = Join-Path $prefix 'include/QtGraphs/qtgraphs-config.h'
    $version = (Get-Item -LiteralPath $originalDll).VersionInfo.FileVersion
    if ($version -notmatch '^6\.11\.2(?:\D|$)' -or
        [IO.Path]::GetFileName($compiler) -ine 'g++.exe') {
        throw 'QtGraphs 修补配方只适用于 Qt 6.11.2 / MinGW x64；请为其他 SDK 单独核对配方。'
    }
    $sdkInputs = @('Qt6Core.dll', 'Qt6Gui.dll', 'Qt6Quick.dll', 'Qt6Quick3D.dll', 'Qt6Graphs.dll') |
        ForEach-Object { $_ + '=' + (Get-FileHash -LiteralPath (Join-Path $prefix "bin/$_")).Hash }
    $sdkHash = Get-QtGraphsTextHash (($sdkInputs + (Get-FileHash -LiteralPath $featureHeader).Hash) -join "`n")
    $recipePath = Join-Path $RepoRoot 'docs/licenses/qtgraphs/dependency-manifest.json'
    $key = Get-QtGraphsTextHash ((@(
        (Get-FileHash -LiteralPath $recipePath).Hash, $sdkHash,
        (Get-FileHash -LiteralPath $compiler).Hash,
        (Get-FileHash -LiteralPath $cmake).Hash,
        (Get-FileHash -LiteralPath $ninja).Hash
    )) -join "`n")
    $cacheRoot = [Environment]::GetEnvironmentVariable('MR_QT_GRAPHS_CACHE_DIR')
    if ([string]::IsNullOrWhiteSpace($cacheRoot)) {
        $cacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MentorRecorder-build-dependencies/qtgraphs'
    } elseif (-not [IO.Path]::IsPathRooted($cacheRoot)) {
        $cacheRoot = Join-Path $RepoRoot $cacheRoot
    }
    $cacheRoot = [IO.Path]::GetFullPath($cacheRoot)
    # Qt's own configure/strip and resource generators cannot consistently use Unicode paths.
    if ($cacheRoot -match '[^\x20-\x7e]' -or $cacheRoot.Length -gt 100) {
        throw 'QtGraphs 依赖缓存需要不超过 100 字符的 ASCII 路径；请用 MR_QT_GRAPHS_CACHE_DIR 指定缓存目录。'
    }
    Assert-QtGraphsWritableDirectory -Directory $cacheRoot -QtPrefix $prefix
    $entry = Join-Path $cacheRoot ('qg-' + $key.Substring(0, 16))
    Assert-QtGraphsWritableDirectory -Directory $entry -QtPrefix $prefix
    return [pscustomobject]@{
        Recipe = $recipe; RepoRoot = $RepoRoot; QtPrefix = $prefix
        Compiler = $compiler; CMake = $cmake; Ninja = $ninja
        OriginalDll = $originalDll; Features = @(Get-QtGraphsFeatures $featureHeader)
        SdkHash = $sdkHash; Key = $key; CacheRoot = $cacheRoot; Entry = $entry
        RuntimeDirectory = (Join-Path $entry 'runtime')
        Archive = (Join-Path $entry 'qtgraphs-source.tar.gz')
    }
}

function Get-QtGraphsContextFromBuild {
    param([Parameter(Mandatory)][string]$BuildDirectory,
          [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
    $cache = [IO.File]::ReadAllText((Join-Path $BuildDirectory 'CMakeCache.txt'))
    $read = {
        param([string]$Name)
        $match = [regex]::Match($cache, '(?m)^' + [regex]::Escape($Name) + ':[^=]+=([^\r\n]+)')
        if (-not $match.Success) { throw "CMake 缓存缺少 $Name。" }
        return $match.Groups[1].Value
    }
    $source = & $read 'CMAKE_HOME_DIRECTORY'
    if (-not [IO.Path]::GetFullPath($source).Equals([IO.Path]::GetFullPath($RepoRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒绝从其他源码工作区的构建缓存选择 QtGraphs SDK。'
    }
    $prefix = [IO.Path]::GetFullPath((Join-Path (& $read 'Qt6_DIR') '../../..'))
    $compiler = & $read 'CMAKE_CXX_COMPILER'
    if ([IO.Path]::GetFileName($compiler) -ine 'g++.exe' -or -not (Test-QtGraphsPatchApplicable $prefix)) {
        return $null
    }
    $ninja = & $read 'CMAKE_MAKE_PROGRAM'
    if ([IO.Path]::GetFileName($ninja) -notmatch '(?i)ninja') { $ninja = & $read 'MR_QTGRAPHS_NINJA' }
    return Get-QtGraphsContext -QtPrefix $prefix -CompilerPath $compiler `
        -CMakeExecutable (& $read 'CMAKE_COMMAND') -NinjaExecutable $ninja -RepoRoot $RepoRoot
}

function Get-QtGraphsDeploymentArguments {
    param([Parameter(Mandatory)]$Context)
    # This diagnostic is compiled only into QtGui's !QRHI_D3D12_AVAILABLE branch.
    # DXC is unused by that SDK; keep deploying the separate D3D11 compiler.
    $gui = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $Context.QtPrefix 'bin/Qt6Gui.dll')))
    if ($gui.Contains('Qt was built without Direct3D 12 support.')) {
        return '--no-system-dxc-compiler'
    }
}

function Assert-QtGraphsCachedRuntime {
    param([Parameter(Mandatory)]$Context)
    $dll = Join-Path $Context.RuntimeDirectory 'Qt6Graphs.dll'
    $receiptPath = Join-Path $Context.RuntimeDirectory 'runtime.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.schema_version -ne 1 -or $receipt.recipe_key -ne $Context.Key -or
        $receipt.source_commit -ne $Context.Recipe.source_commit -or
        $receipt.patch_sha256 -ne $Context.Recipe.patch_sha256 -or
        $receipt.source_sha256 -ne $Context.Recipe.source_sha256 -or
        $receipt.sdk_sha256 -ne $Context.SdkHash -or
        $receipt.dll_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $receipt.architecture -ne 'AMD64' -or $receipt.missing_exports -ne 0 -or
        @(Compare-Object @($Context.Features) @($receipt.features)).Count -ne 0 -or
        (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() -ne $receipt.dll_sha256 -or
        (Get-FileHash -LiteralPath $Context.Archive).Hash.ToLowerInvariant() -ne $Context.Recipe.source_sha256) {
        throw 'QtGraphs 缓存与配方、SDK 或文件哈希不一致，拒绝部署。'
    }
    return $receipt
}

function Get-QtGraphsPeExports {
    param([Parameter(Mandatory)][string]$Dll, [Parameter(Mandatory)][string]$Objdump)
    $report = @(& $Objdump -p $Dll 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not ($report -match 'file format pei-x86-64')) {
        throw "QtGraphs 不是有效的 MinGW AMD64 DLL：$Dll"
    }
    $exports = @($report | ForEach-Object {
        if ($_ -match '^\s*\[\s*\d+\]\s+(\S+)\s*$') { $Matches[1] }
    } | Sort-Object -Unique)
    if ($exports.Count -lt 100) { throw "QtGraphs 导出表不完整：$Dll" }
    return $exports
}

function Initialize-QtGraphsRuntime {
    param([Parameter(Mandatory)]$Context)
    foreach ($path in @($Context.Entry, $Context.RuntimeDirectory, $Context.Archive,
            (Join-Path $Context.Entry 'prepare.lock'),
            (Join-Path $Context.RuntimeDirectory 'Qt6Graphs.dll'),
            (Join-Path $Context.RuntimeDirectory 'runtime.json'))) {
        Assert-QtGraphsWritableDirectory -Directory $path -QtPrefix $Context.QtPrefix
    }
    New-Item -ItemType Directory -Force -Path $Context.Entry | Out-Null
    $lock = $null
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    while ($null -eq $lock) {
        try {
            $lock = [IO.File]::Open((Join-Path $Context.Entry 'prepare.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw '等待另一个 QtGraphs 构建超时。' }
            Start-Sleep -Milliseconds 500
        }
    }
    try {
        if (Test-Path -LiteralPath (Join-Path $Context.RuntimeDirectory 'runtime.json')) {
            return Assert-QtGraphsCachedRuntime -Context $Context
        }
        if (-not (Test-Path -LiteralPath $Context.Archive)) {
            $download = $Context.Archive + '.copy-' + [guid]::NewGuid().ToString('N')
            $offlineArchive = [Environment]::GetEnvironmentVariable('MR_QT_GRAPHS_SOURCE_ARCHIVE')
            if ([string]::IsNullOrWhiteSpace($offlineArchive)) {
                $offlineArchive = Join-Path $Context.RepoRoot ('docs/licenses/qtgraphs/' + $Context.Recipe.source_archive)
            }
            Copy-Item -LiteralPath $offlineArchive -Destination $download
            if ((Get-FileHash -LiteralPath $download).Hash.ToLowerInvariant() -ne $Context.Recipe.source_sha256) {
                throw 'QtGraphs 源码包 SHA256 不匹配，未解压或执行。'
            }
            Move-Item -LiteralPath $download -Destination $Context.Archive
        }
        if ((Get-FileHash -LiteralPath $Context.Archive).Hash.ToLowerInvariant() -ne $Context.Recipe.source_sha256) {
            throw 'QtGraphs 缓存源码包 SHA256 不匹配。'
        }
        # A failed build leaves its own work directory for diagnostics, never a valid receipt.
        $work = Join-Path $Context.Entry ('w-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
        New-Item -ItemType Directory -Path $work | Out-Null
        & tar.exe -xzf $Context.Archive -C $work | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'QtGraphs 源码解压失败。' }
        $source = Join-Path $work ('qtgraphs-' + $Context.Recipe.source_commit)
        $patchedFile = Join-Path $source $Context.Recipe.patched_file
        if ((Get-FileHash -LiteralPath $patchedFile).Hash.ToLowerInvariant() -ne $Context.Recipe.original_file_sha256) {
            throw 'QtGraphs 待修补源码与固定版本不一致。'
        }
        $patch = Join-Path $Context.RepoRoot ('docs/licenses/qtgraphs/' + $Context.Recipe.patch_file)
        & git -c core.autocrlf=false -C $source apply --check -- $patch | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'QtGraphs 补丁不能完整应用。' }
        & git -c core.autocrlf=false -C $source apply -- $patch | Out-Host
        if ($LASTEXITCODE -ne 0 -or
            (Get-FileHash -LiteralPath $patchedFile).Hash.ToLowerInvariant() -ne $Context.Recipe.patched_file_sha256) {
            throw 'QtGraphs 修补结果与固定哈希不一致。'
        }
        $build = Join-Path $work 'build'
        $compilerBin = Split-Path -Parent $Context.Compiler
        $oldPath = $env:PATH
        try {
            $env:PATH = $compilerBin + ';' + (Join-Path $Context.QtPrefix 'bin') + ';' + $oldPath
            $mappedWork = $work.Replace('\', '/')
            $flags = '-ffile-prefix-map="' + $mappedWork + '=/qtgraphs" -fdebug-prefix-map="' + $mappedWork + '=/qtgraphs"'
            $configure = @('-S', $source, '-B', $build, '-G', 'Ninja',
                ('-DCMAKE_MAKE_PROGRAM=' + $Context.Ninja),
                ('-DCMAKE_PREFIX_PATH=' + $Context.QtPrefix),
                ('-DCMAKE_CXX_COMPILER=' + $Context.Compiler),
                ('-DCMAKE_C_COMPILER=' + (Join-Path $compilerBin 'gcc.exe')),
                ('-DCMAKE_INSTALL_PREFIX=' + (Join-Path $work 'install')),
                ('-DCMAKE_CXX_FLAGS=' + $flags), ('-DCMAKE_C_FLAGS=' + $flags)) + @($Context.Recipe.cmake_options)
            & $Context.CMake @configure | Out-Host
            if ($LASTEXITCODE -ne 0) { throw '专用 QtGraphs 配置失败。' }
            $features = @(Get-QtGraphsFeatures (Join-Path $build 'src/qtgraphs-config.h'))
            if (@(Compare-Object @($Context.Features) $features).Count) {
                throw '专用 QtGraphs 与 SDK 功能配置不一致，拒绝削减 2D/3D 功能。'
            }
            & $Context.CMake --build $build --target Graphs --parallel ([Math]::Min(8, [Environment]::ProcessorCount)) | Out-Host
            if ($LASTEXITCODE -ne 0) { throw '专用 QtGraphs 构建失败。' }
        } finally { $env:PATH = $oldPath }
        $dll = Join-Path $build 'bin/Qt6Graphs.dll'
        $objdump = Join-Path $compilerBin 'objdump.exe'
        $originalExports = @(Get-QtGraphsPeExports -Dll $Context.OriginalDll -Objdump $objdump)
        $patchedExports = @(Get-QtGraphsPeExports -Dll $dll -Objdump $objdump)
        $missing = @($originalExports | Where-Object { $_ -notin $patchedExports })
        if ($missing.Count) { throw ('专用 QtGraphs 缺少原 DLL 的导出符号：' + ($missing -join ', ')) }
        New-Item -ItemType Directory -Force -Path $Context.RuntimeDirectory | Out-Null
        Copy-Item -LiteralPath $dll -Destination (Join-Path $Context.RuntimeDirectory 'Qt6Graphs.dll')
        $receipt = [ordered]@{
            schema_version = 1; qt_version = $Context.Recipe.qt_version
            recipe_key = $Context.Key; source_commit = $Context.Recipe.source_commit
            source_sha256 = $Context.Recipe.source_sha256; patch_sha256 = $Context.Recipe.patch_sha256
            sdk_sha256 = $Context.SdkHash; dll_sha256 = (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant()
            architecture = 'AMD64'; features = $features
            original_exports = $originalExports.Count; patched_exports = $patchedExports.Count; missing_exports = 0
        }
        $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Context.RuntimeDirectory 'runtime.json') -Encoding utf8
        return Assert-QtGraphsCachedRuntime -Context $Context
    } finally { $lock.Dispose() }
}

function Assert-InstalledQtGraphsRuntime {
    param([Parameter(Mandatory)][string]$DestinationDirectory, [Parameter(Mandatory)]$Context)
    $expected = Assert-QtGraphsCachedRuntime -Context $Context
    $dll = Join-Path $DestinationDirectory 'Qt6Graphs.dll'
    $materials = Join-Path $DestinationDirectory 'docs/licenses/qtgraphs'
    $installedReceipt = Join-Path $materials 'runtime.json'
    $receipt = Get-Content -LiteralPath $installedReceipt -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.recipe_key -ne $expected.recipe_key -or $receipt.dll_sha256 -ne $expected.dll_sha256 -or
        (Get-FileHash -LiteralPath $installedReceipt).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $Context.RuntimeDirectory 'runtime.json')).Hash -or
        (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() -ne $expected.dll_sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $materials 'qtgraphs-source.tar.gz')).Hash.ToLowerInvariant() -ne $Context.Recipe.source_sha256) {
        throw '部署的 QtGraphs 不是当前核验过的专用库。'
    }
    foreach ($name in (Get-QtGraphsMaterialNames)) {
        if ((Get-FileHash -LiteralPath (Join-Path $materials $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $Context.RepoRoot ('docs/licenses/qtgraphs/' + $name))).Hash) {
            throw "QtGraphs 对应源码/许可证材料不一致：$name"
        }
    }
    return $expected
}

function Install-QtGraphsRuntime {
    param([Parameter(Mandatory)][string[]]$DestinationDirectory, [Parameter(Mandatory)]$Context, [switch]$Prepare)
    $targets = @($DestinationDirectory | ForEach-Object {
        $target = [IO.Path]::GetFullPath($_).TrimEnd('\', '/')
        $materials = Join-Path $target 'docs/licenses/qtgraphs'
        $writePaths = @($target, (Join-Path $target 'Qt6Graphs.dll'), $materials,
            (Join-Path $materials 'runtime.json'), (Join-Path $materials 'qtgraphs-source.tar.gz'))
        $writePaths += @(Get-QtGraphsMaterialNames | ForEach-Object { Join-Path $materials $_ })
        foreach ($path in $writePaths) {
            Assert-QtGraphsWritableDirectory -Directory $path -QtPrefix $Context.QtPrefix -CacheRoot $Context.CacheRoot
        }
        $target
    })
    $receipt = if ($Prepare) { Initialize-QtGraphsRuntime -Context $Context } else { Assert-QtGraphsCachedRuntime -Context $Context }
    foreach ($target in $targets) {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        $dll = Join-Path $target 'Qt6Graphs.dll'
        if (-not (Test-Path -LiteralPath $dll) -or (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() -ne $receipt.dll_sha256) {
            Copy-Item -LiteralPath (Join-Path $Context.RuntimeDirectory 'Qt6Graphs.dll') -Destination $dll -Force
        }
        $materials = Join-Path $target 'docs/licenses/qtgraphs'
        New-Item -ItemType Directory -Force -Path $materials | Out-Null
        foreach ($name in (Get-QtGraphsMaterialNames)) {
            Copy-Item -LiteralPath (Join-Path $Context.RepoRoot ('docs/licenses/qtgraphs/' + $name)) -Destination (Join-Path $materials $name) -Force
        }
        $archive = Join-Path $materials 'qtgraphs-source.tar.gz'
        if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $Context.Recipe.source_sha256) {
            Copy-Item -LiteralPath $Context.Archive -Destination $archive -Force
        }
        Copy-Item -LiteralPath (Join-Path $Context.RuntimeDirectory 'runtime.json') -Destination (Join-Path $materials 'runtime.json') -Force
        Assert-InstalledQtGraphsRuntime -DestinationDirectory $target -Context $Context | Out-Null
    }
    return $receipt
}
