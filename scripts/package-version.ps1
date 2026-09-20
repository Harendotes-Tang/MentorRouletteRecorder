#Requires -Version 5.1
<#
.SYNOPSIS
    版本号的纯函数 / Pure version helpers shared by the packaging scripts.

.DESCRIPTION
    这些函数没有任何副作用（除读取传入的文件外），因此可以被点源引入并单独自测：
    tools/package-verification/test_package_version.py 会逐条验证，
    scripts/run-python-tool-tests.ps1 与 scripts/verify.ps1 会运行该自测。

    版本号只有一个来源：Directory.Build.props 的 <VersionPrefix>（三段数字）与可选的
    <VersionSuffix>（先行版标签，如 beta.1）。二者合成的完整版本号是人看到的那一个；
    Windows 版本资源只能容纳数字，因此需要数字部分时取 <VersionPrefix>。
#>

Set-StrictMode -Version Latest

# A release number: three plain numbers, nothing else.
$script:MrVersionPrefixPattern = '^\d+\.\d+\.\d+$'

# A prerelease label as semantic versioning writes it after the dash: dot-separated
# identifiers of letters, digits and hyphens. `beta.1` is the one this project uses.
$script:MrVersionSuffixPattern = '^[0-9A-Za-z]+(?:[0-9A-Za-z-]*)(?:\.[0-9A-Za-z][0-9A-Za-z-]*)*$'

function Get-NormalizedVersion {
    <#
    .SYNOPSIS
        取版本号的数字部分 / The three numbers a Windows version resource can hold.
    .DESCRIPTION
        FileVersion 资源是四段（"1.4.0.0"），Directory.Build.props 是三段，完整版本号还可能
        带 "-beta.1" 后缀。统一取 major.minor.patch，使三种形状可以互相比较；读不出时返回 $null。
    #>
    param([AllowEmptyString()][AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $match = [regex]::Match($Value.Trim(), '^(\d+)\.(\d+)\.(\d+)(?:[.\-+]|$)')
    if (-not $match.Success) { return $null }
    return ('{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value)
}

function Get-FullVersion {
    <#
    .SYNOPSIS
        取完整版本号 / The full version, prerelease label included.
    .DESCRIPTION
        接受 "x.y.z"、"x.y.z-beta.1" 与 "x.y.z-beta.1+2f3a4b5"，返回前两种形状（构建元数据
        不参与版本比较，因此丢弃）。四段的 FileVersion 与其他任何形状返回 $null：
        这个函数用于核对 InformationalVersion / ProductVersion，不能把四段数字当成合法输入。
    #>
    param([AllowEmptyString()][AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $match = [regex]::Match(
        $Value.Trim(), '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$')
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value + $match.Groups[2].Value
}

function Read-ProductVersion {
    <#
    .SYNOPSIS
        读取唯一的版本号来源 / Read the one source of truth for the version.
    .DESCRIPTION
        返回 @{ Prefix; Suffix; Full; Prerelease }。格式不合法时抛出异常而不是静默降级：
        测试包丢掉后缀之后与正式版一模一样，必须在打包最开始就停下。
    #>
    param([Parameter(Mandatory = $true)][string]$PropsPath)

    if (-not (Test-Path -LiteralPath $PropsPath)) {
        throw ("找不到 Directory.Build.props: {0}" -f $PropsPath)
    }

    $properties = ([xml](Get-Content -LiteralPath $PropsPath -Raw)).Project.PropertyGroup
    $prefix = $properties | ForEach-Object { $_.VersionPrefix } |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $prefix) { throw ("无法从 {0} 读取 <VersionPrefix>" -f $PropsPath) }
    $prefix = ([string]$prefix).Trim()
    if ($prefix -notmatch $script:MrVersionPrefixPattern) {
        throw ("Directory.Build.props 的 <VersionPrefix> 不是 x.y.z: {0}" -f $prefix)
    }

    # An absent element and an empty one mean the same thing: this is a release.
    $suffix = ''
    foreach ($group in $properties) {
        $node = $group.SelectSingleNode('VersionSuffix')
        if ($node) { $suffix = ([string]$node.InnerText).Trim(); break }
    }
    if ($suffix -and $suffix -notmatch $script:MrVersionSuffixPattern) {
        throw ("Directory.Build.props 的 <VersionSuffix> 不是先行版标签（如 beta.1）: {0}" -f $suffix)
    }

    return [ordered]@{
        Prefix     = $prefix
        Suffix     = $suffix
        Full       = if ($suffix) { "$prefix-$suffix" } else { $prefix }
        Prerelease = [bool]$suffix
    }
}

function Get-TopChangelogHeading {
    <#
    .SYNOPSIS
        读 CHANGELOG 最上方的段落标题 / The label of the first "## [...]" heading.
    .DESCRIPTION
        返回方括号里的原文（"Unreleased" 或 "1.4.0"），没有这样的标题时返回 $null。
        与只认数字标题的旧实现不同：先行版要求最上方是 [Unreleased]，因此必须能读到它。
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $match = [regex]::Match($line, '^##\s*\[([^\]]+)\]')
        if ($match.Success) { return $match.Groups[1].Value.Trim() }
    }
    return $null
}

function Test-ChangelogTopIsCorrect {
    <#
    .SYNOPSIS
        CHANGELOG 顶部段落是否与当前版本相符 / Does the top section match this version?
    .DESCRIPTION
        正式版：最上方必须是 "## [x.y.z]"，且与版本号一致——这是 0.2.2 以来的规则。
        先行版（x.y.z-beta.N）：最上方必须是 "## [Unreleased]"。测试包是从尚未发布的工作
        中切出来的，它携带的条目仍然属于未发布内容；写成编号标题等于声称该版本已经发布，
        而 Assert-ReleasedChangelogSectionsUnchanged 随后还会把那个段落锁死在一个并不存在的 tag 上。
    #>
    param(
        [AllowEmptyString()][AllowNull()][string]$Heading,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($Heading)) { return $false }
    if ($Version -match '-') { return $Heading -eq 'Unreleased' }
    return $Heading -eq $Version
}

function Get-ChangelogTopRefusal {
    <#
    .SYNOPSIS
        顶部段落不符时的说明 / Why the top CHANGELOG section was refused; $null when it is fine.
    #>
    param(
        [AllowEmptyString()][AllowNull()][string]$Heading,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if (Test-ChangelogTopIsCorrect -Heading $Heading -Version $Version) { return $null }
    $observed = if ([string]::IsNullOrWhiteSpace($Heading)) { '(没有 ## [..] 标题)' } else { "[$Heading]" }
    if ($Version -match '-') {
        return ("CHANGELOG.md 最上方应为 ## [Unreleased]（当前版本 {0} 是先行版，其条目尚未发布），实际为 {1}" -f
            $Version, $observed)
    }
    return ("CHANGELOG.md 最上方应为 ## [{0}]，实际为 {1}" -f $Version, $observed)
}
