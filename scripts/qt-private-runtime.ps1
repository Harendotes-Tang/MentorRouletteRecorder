#Requires -Version 7.0

function Read-QtPrivateRuntime {
    param([Parameter(Mandatory)][string]$BuildDirectory)
    $manifest = Get-Content -LiteralPath (Join-Path $BuildDirectory 'qt-private-runtime.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema_version -ne 1 -or $manifest.qt_version -notmatch '^6\.\d+\.\d+$') {
        throw 'Qt 私有接口运行时清单格式无效。'
    }
    foreach ($entry in $manifest.dll_sha256.PSObject.Properties) {
        if ($entry.Name -notmatch '^Qt6(Core|Gui|Qml|Quick|QuickTemplates2)d?\.dll$' -or
            $entry.Value -isnot [string] -or
            $entry.Value -notmatch '^[a-f0-9]{64}$') {
            throw 'Qt 私有接口运行时清单包含无效文件或哈希。'
        }
    }
    foreach ($module in @('Core', 'Gui', 'Qml', 'Quick', 'QuickTemplates2')) {
        if (-not ($manifest.dll_sha256.PSObject.Properties.Name -match ('^Qt6' + $module + 'd?\.dll$'))) {
            throw "Qt 私有接口运行时清单缺少 $module。"
        }
    }
    return $manifest
}

function Assert-QtPrivateRuntime {
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)][string]$Directory)
    foreach ($module in @('Core', 'Gui', 'Qml', 'Quick', 'QuickTemplates2')) {
        $names = @("Qt6$module.dll", "Qt6${module}d.dll")
        $present = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf })
        if ($present.Count -eq 0) { throw "部署的 Qt 私有接口运行时缺少 $module。" }
        foreach ($name in $present) {
            $expected = $Manifest.dll_sha256.PSObject.Properties[$name]
            if ($null -eq $expected -or
                (Get-FileHash -LiteralPath (Join-Path $Directory $name)).Hash.ToLowerInvariant() -ne $expected.Value) {
                throw "部署的 $name 与编译时 Qt SDK 不一致，拒绝混用私有 ABI。"
            }
        }
    }
}
