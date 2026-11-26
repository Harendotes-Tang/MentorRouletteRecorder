#Requires -Version 7.0
<#
.SYNOPSIS
    版本号的纯函数 / Pure version helpers shared by the packaging scripts.

.DESCRIPTION
    这些函数没有任何副作用（除读取传入的文件与只读地询问 git 外），因此可以被点源引入并单独自测：
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
        先行版（x.y.z-beta.N）：最上方必须是 "## [Unreleased]"。先行版的发布提交把条目移入它自己的
        "## [x.y.z-beta.N] - 日期" 段落，放在一个空的 [Unreleased] 之下（docs/release-checklist.md §9.3）；
        这里只核对最上方仍是 [Unreleased]，不核对其下的先行版段落。
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

function Get-ChangelogSection {
    <#
    .SYNOPSIS
        取出一个版本段落 / The "## [X.Y.Z] - date" section of one version.
    .DESCRIPTION
        返回 @{ Heading; Body }：Heading 是标题行原文（含日期），Body 是到下一个 "## " 标题为止的
        正文；没有该版本的标题时返回 $null。换行先统一为 LF：tag 中的文件由 git 以 LF 输出，
        工作区是 CRLF；PowerShell 读取 git 输出时把单独的 CR 也当作换行，这里同样处理。
    #>
    param(
        [AllowEmptyString()][AllowNull()][string]$Text,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $heading = $null
    $body = New-Object System.Collections.Generic.List[string]
    foreach ($line in (([string]$Text) -replace "`r`n?", "`n") -split "`n") {
        if ($line -match '^##\s*\[([^\]]+)\]') {
            if ($null -ne $heading) { break }
            if ($Matches[1] -eq $Version) { $heading = $line.Trim() }
            continue
        }
        if ($null -ne $heading) { $body.Add($line.TrimEnd()) }
    }
    if ($null -eq $heading) { return $null }
    return [ordered]@{ Heading = $heading; Body = ($body -join "`n").Trim() }
}

function Get-ReleasedChangelogSectionChange {
    <#
    .SYNOPSIS
        已发布段落是否被改动 / How a released section differs from what its tag recorded.
    .DESCRIPTION
        $Tagged 是 vX.Y.Z（或 vX.Y.Z-beta.N）tag 中的 CHANGELOG.md，$Working 是工作区中的。tag 中没有这一段时返回
        $null（该版本发布时没有段落，无可保护）；否则工作区中的同名段落必须存在，标题行（含日期）
        与正文都与 tag 逐字一致，不一致时返回说明，一致时返回 $null。工作区中缺少该段落——被删除，
        或标题被改成了别的版本号——同样算作改动。
    #>
    param(
        [AllowEmptyString()][AllowNull()][string]$Tagged,
        [AllowEmptyString()][AllowNull()][string]$Working,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $released = Get-ChangelogSection -Text $Tagged -Version $Version
    if ($null -eq $released) { return $null }
    $current = Get-ChangelogSection -Text $Working -Version $Version
    if ($null -eq $current) {
        return ("工作区中没有 ## [{0}] 段落（被删除或标题被改名）" -f $Version)
    }
    if ($current.Heading -cne $released.Heading) {
        return ("标题行由「{0}」改成了「{1}」" -f $released.Heading, $current.Heading)
    }
    if ($current.Body -cne $released.Body) {
        return ("## [{0}] 的正文与 tag 中的不一致" -f $Version)
    }
    return $null
}

function Get-ReleasedChangelogTagVersion {
    <#
    .SYNOPSIS
        tag 对应的已发布版本号 / The version a published tag protects; $null for any other tag.
    .DESCRIPTION
        正式版 tag（v1.9.0）与先行版 tag（v1.9.1-beta.1）都算：先行版现在以自己的
        "## [X.Y.Z-beta.N] - 日期" 段落发布在空的 [Unreleased] 之下，tag 与 GitHub 预发布都由这段文字生成，
        发布后同样不能再改（审计 2026-11-20 N2-3）。该 tag 中的 CHANGELOG 没有这一段时（旧流程的
        先行版，条目还在 [Unreleased] 下），Get-ReleasedChangelogSectionChange 返回 $null，无可保护。
    #>
    param([AllowEmptyString()][AllowNull()][string]$Tag)

    if ([string]::IsNullOrWhiteSpace($Tag)) { return $null }
    $match = [regex]::Match($Tag.Trim(), '^v(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$')
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Get-HeadCommitSubject {
    <#
    .SYNOPSIS
        HEAD 的提交标题 / The subject line of HEAD; $null when git cannot answer.
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { return $null }
    $subject = @(& git -C $RepoRoot log -1 '--format=%s' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $subject.Count -ne 1) { return $null }
    return [string]$subject[0]
}

function Get-UnsuffixedVersionRefusal {
    <#
    .SYNOPSIS
        正式版本号能否在这次打包中使用 / Why a version without a prerelease label may not be packaged here.
    .DESCRIPTION
        没有先行版后缀的版本号一经交到别人手里就被用掉了：之后真正发布的同号版本不会被当作更新提供
        （审计 2026-11-20 M-2：1.9.1 曾被七个按 -SkipVerify 打出的本地测试包用掉）。因此正式版本号只能在
        HEAD 正是它的发布提交（标题为 "chore(release): X.Y.Z"）且没有 -SkipVerify 时打包；其余情况返回
        拒绝的说明，由 package.ps1 在验证与构建之前停下，除非显式给出 -AllowUnsuffixedVersion。
        先行版（带 "-" 后缀）与满足条件的正式版返回 $null。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [switch]$SkipVerify,
        [AllowEmptyString()][AllowNull()][string]$HeadSubject
    )

    if ($Version -match '-') { return $null }
    $advice = '测试包请在 Directory.Build.props 中加上 <VersionSuffix>（如 beta.1 或 dev.1）；确需以正式版本号打包时加 -AllowUnsuffixedVersion'
    if ($SkipVerify) {
        return ("版本 {0} 没有先行版后缀，不能与 -SkipVerify 一起打包：正式版本号只用于通过验证的发布构建。{1}" -f
            $Version, $advice)
    }
    $release = 'chore(release): {0}' -f $Version
    if ($HeadSubject -cne $release) {
        $observed = if ([string]::IsNullOrWhiteSpace($HeadSubject)) { '无法读取（git 没有回答）' } else { "「$HeadSubject」" }
        return ("版本 {0} 没有先行版后缀，但 HEAD 不是它的发布提交「{1}」，HEAD 的标题为{2}。{3}" -f
            $Version, $release, $observed, $advice)
    }
    return $null
}

function Get-VersionTagState {
    <#
    .SYNOPSIS
        版本 tag 与 HEAD 各指向哪个提交 / Which commit tag v<Version> and HEAD point at.
    .DESCRIPTION
        返回 @{ Tag; TagCommit; HeadCommit; Problem }。没有这个 tag 时 TagCommit 为 $null；
        找不到 git 或任一命令失败时 Problem 说明原因，两个提交都不可信。
        rev-list 取 tag 所指的提交，轻量 tag 与附注 tag 都一样。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $state = [ordered]@{ Tag = "v$Version"; TagCommit = $null; HeadCommit = $null; Problem = $null }
    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
        $state.Problem = '找不到 git'
        return $state
    }

    $head = @(& git -C $RepoRoot rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -notmatch '^[0-9a-f]{40}$') {
        $state.Problem = ("git rev-parse HEAD 失败（退出码 {0}）" -f $LASTEXITCODE)
        return $state
    }
    $state.HeadCommit = [string]$head[0]

    $listed = @(& git -C $RepoRoot tag --list $state.Tag 2>$null)
    if ($LASTEXITCODE -ne 0) {
        $state.Problem = ("git tag --list 失败（退出码 {0}）" -f $LASTEXITCODE)
        return $state
    }
    if (@($listed | Where-Object { $_ -ceq $state.Tag }).Count -eq 0) { return $state }

    $tagged = @(& git -C $RepoRoot rev-list -n 1 ("refs/tags/{0}" -f $state.Tag) 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tagged.Count -ne 1 -or $tagged[0] -notmatch '^[0-9a-f]{40}$') {
        $state.Problem = ("无法读取 tag {0} 指向的提交（退出码 {1}）" -f $state.Tag, $LASTEXITCODE)
        return $state
    }
    $state.TagCommit = [string]$tagged[0]
    return $state
}

function Get-ReusedVersionRefusal {
    <#
    .SYNOPSIS
        这个版本号是否已经发过另一份内容 / Why this version may not be packaged again; $null when it may.
    .DESCRIPTION
        一个发出去的版本号（先行版也一样）不再用于另一份内容：已安装旧包的人比较版本号时认为两者相同，
        永远不会被引到重打的包上（审计 2026-11-26 M-2：1.10.0-beta.2、1.9.1-beta.3/4 与 1.9.1 都曾以同一
        版本号重打）。tag v<Version> 已存在且不指向 HEAD 时返回拒绝的说明；git 无法回答时同样拒绝，
        因为无法确认这个号是否已被用掉。没有这个 tag，或 tag 正指向 HEAD（同一提交重打）时返回 $null。
        package.ps1 在验证与构建之前停下，除非显式给出 -AllowReusedVersion。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [AllowEmptyString()][AllowNull()][string]$TagCommit,
        [AllowEmptyString()][AllowNull()][string]$HeadCommit,
        [AllowEmptyString()][AllowNull()][string]$Problem
    )

    $advice = '撤回的版本请改用下一个号（如 beta.N 之后用 beta.N+1）；确需以同一版本号重打时加 -AllowReusedVersion'
    if (-not [string]::IsNullOrWhiteSpace($Problem)) {
        return ("无法确认版本 {0} 的 tag v{0} 是否已存在：{1}。{2}" -f $Version, $Problem, $advice)
    }
    if ([string]::IsNullOrWhiteSpace($TagCommit)) { return $null }
    if ($TagCommit -ceq $HeadCommit) { return $null }
    $short = { param($commit) if ([string]::IsNullOrWhiteSpace($commit)) { '（未知）' } else { $commit.Substring(0, [Math]::Min(12, $commit.Length)) } }
    return ("版本 {0} 已经发过：tag v{0} 指向 {1}，而 HEAD 是 {2}。同一个版本号不能再打出另一份内容。{3}" -f
        $Version, (& $short $TagCommit), (& $short $HeadCommit), $advice)
}

function Get-SourceTreeState {
    <#
    .SYNOPSIS
        源码提交与工作区状态 / The commit a package is built from, and whether the tree is clean.
    .DESCRIPTION
        返回 @{ Commit; Dirty; Problem }。git 回答了两个问题时，Commit 是 40 位提交号，Dirty 表示
        工作区是否有未提交改动（含未跟踪文件），Problem 为 $null。找不到 git、目录不是仓库、git 因
        “dubious ownership” 拒绝该仓库，或任一命令失败时，Commit 与 Dirty 均为 $null，Problem 说明
        原因：这些情况下 `git status` 什么也不输出，绝不能被读成“工作区干净”。
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $state = [ordered]@{ Commit = $null; Dirty = $null; Problem = $null }
    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
        $state.Problem = '找不到 git，无法确定源码提交与工作区状态'
        return $state
    }

    $commit = @(& git -C $RepoRoot rev-parse --verify HEAD 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or $commit.Count -ne 1 -or $commit[0] -notmatch '^[0-9a-f]{40}$') {
        $state.Problem = ("git rev-parse HEAD 失败（退出码 {0}）：目录可能不是 git 仓库，或 git 拒绝了该仓库（dubious ownership）" -f
            $exitCode)
        return $state
    }

    $changes = @(& git -C $RepoRoot status --porcelain --untracked-files=all 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $state.Problem = ("git status 失败（退出码 {0}），无法确认源码工作区是否干净" -f $exitCode)
        return $state
    }

    $state.Commit = [string]$commit[0]
    $state.Dirty = $changes.Count -gt 0
    return $state
}
