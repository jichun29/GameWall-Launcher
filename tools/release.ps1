# GameWall 发版脚本
#
# 版本规则（由 tools/release-history.json 记录，必须提交进仓库）：
#   * 版本号统一走 1.0.x：主版本 1、次版本 0 固定不变，每次发版只把第三段 +1。
#     （1.0.4 → 1.0.5 → 1.0.6 …）这样永远不会出现 1.1 / 1.2 那种「一天跳好几个大版本」。
#   * 想启用新的次版本，得手动改 -Minor 参数，默认不动。
#
# 用法：
#   1) 只算下一个版本号，不改任何文件、不发布
#      .\tools\release.ps1 -DryRun
#   2) 算好版本号 → 写回 csproj → 编译 → 发布单文件 exe
#      .\tools\release.ps1 -Build
#   3) 发布 + 生成校验值与 Release 说明草稿
#      .\tools\release.ps1 -Build -Checksums
#   4) 连安装包一起打（需要装了 Inno Setup 6）
#      .\tools\release.ps1 -Build -Checksums -Installer
#   5) 传完 GitHub Release 后，把这一条记进历史（否则下次还会算出同样的版本号）
#      .\tools\release.ps1 -Record
#   6) 只重出 Release 说明草稿（改了 CHANGELOG 措辞时用，不编译、不动版本号）
#      .\tools\release.ps1 -Notes
#   7) 版本号不变、重新打一遍包（改了代码但还没到下一个版本号时用）
#      .\tools\release.ps1 -Rebuild -Checksums -Installer
#
# 产物：
#   dist\GameWall.exe                   单文件绿色版
#   dist\GameWall-Setup-<版本>.exe      安装包（加 -Installer）
#   dist\SHA256SUMS.txt                 校验值
#   dist\RELEASE-NOTES-<版本>.md        Release 正文草稿（从 CHANGELOG 摘对应版本）

param(
    [switch]$Build,
    [switch]$Rebuild,
    [switch]$Record,
    [switch]$DryRun,
    [switch]$Checksums,
    [switch]$Installer,
    [switch]$Notes,
    [string]$Minor = "0"
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\GameWall\GameWall.csproj'
$history = Join-Path $PSScriptRoot 'release-history.json'
$changelog = Join-Path $root 'CHANGELOG.md'
$distDir = Join-Path $root 'dist'

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ── CHANGELOG 辅助 ──────────────────────────────────────
# 从 CHANGELOG.md 取某个版本的整段正文。
# 结束条件是「下一个二级标题」（## [1.0.21] 这种也要算），
# 因为文件末尾还有 "## 历史版本"、"## 版本号约定" 这类普通章节，
# 只认 "## [" 会把整张历史表格一起摘进 Release 说明里。
function Get-ChangelogSection([string]$version) {
    if (-not (Test-Path $changelog)) { return $null }

    $all = [System.IO.File]::ReadAllText($changelog, $utf8NoBom)
    $pattern = "(?ms)^##\s*\[$([regex]::Escape($version))\][^\r\n]*\r?\n(.*?)(?=^##[^#]|\z)"
    $m = [regex]::Match($all, $pattern)
    if (-not $m.Success) { return $null }

    # 段落末尾常挂一条分隔线 "---"，摘进 Release 说明里会多出一条横线
    $body = $m.Groups[1].Value.Trim()
    $body = [regex]::Replace($body, '(?m)^\s*-{3,}\s*$[\r\n]*\s*$', '')
    return $body.Trim()
}

# 取该版本里的第一条要点，作为历史记录的 note（纯文本、截断）
function Get-ChangelogHeadline([string]$version) {
    $section = Get-ChangelogSection $version
    if (-not $section) { return '' }

    $first = ($section -split "`r?`n") |
        Where-Object { $_.Trim().StartsWith('- ') } |
        Select-Object -First 1

    if (-not $first) { return '' }

    $clean = $first.Trim().TrimStart('-').Trim()
    $clean = $clean -replace '\*\*', ''
    if ($clean.Length -gt 60) { $clean = $clean.Substring(0, 60) + '…' }
    return $clean
}

# ── 读历史 ──────────────────────────────────────────────
function Read-History {
    $entries = @()
    if (Test-Path $history) {
        $raw = [System.IO.File]::ReadAllText($history, $utf8NoBom)
        if ($raw.Trim().Length -gt 0) {
            # 注意：PowerShell 5.1 的 ConvertFrom-Json 不会展开顶层数组，
            # 直接 @($raw | ConvertFrom-Json) 会把整个数组包成「一个元素」，
            # 再 ConvertTo-Json 写回去就变成 { value: [...], Count: n } 的怪结构。
            # 所以这里手动逐条展开。
            $parsed = $raw | ConvertFrom-Json
            foreach ($item in $parsed) { $entries += $item }
        }
    }
    return $entries
}

$entries = Read-History
$today = (Get-Date).ToString('yyyy-MM-dd')

# 找最近一条记录
$last = $null
if ($entries.Count -gt 0) {
    $last = $entries[$entries.Count - 1]
}

if ($last -eq $null) {
    $next = "1.$Minor.0"
    $reason = "还没有发版记录，从 1.$Minor.0 起步"
}
else {
    $parts = ($last.version -split '\.')
    $major = [int]$parts[0]
    $patch = 0
    if ($parts.Count -ge 3) { $patch = [int]$parts[2] }

    # 只递增第三段：主次两段固定 1.0，避免版本号跑得比功能还快
    $next = "$major.$Minor.$($patch + 1)"
    $reason = "上次发布 $($last.version)，1.0.x 规范下只递增第三段"
}

Write-Host ''
Write-Host "  今天        : $today"
if ($last -ne $null) {
    Write-Host "  上次发布    : $($last.version)  ($($last.date))"
}
else {
    Write-Host "  上次发布    : 无记录"
}
Write-Host "  规则        : $reason"
Write-Host "  下一个版本  : $next"
Write-Host ''

# ── 只记录历史（传完 Release 之后跑）─────────────────────
if ($Record) {
    $exe = Join-Path $distDir 'GameWall.exe'
    if (-not (Test-Path $exe)) { throw "找不到 $exe，先跑一次 -Build" }

    # 拿 dist 里 exe 的真实版本号，避免「-Record 单独跑时算出的 next 跟已发的对不上」。
    # 注意：.NET 会把 InformationalVersion 拼成 "1.0.22+<commit sha>" 这种形式，
    # 直接 split('.') 会得到 "22+ba18979..." 这种脏值，所以先用正则只取 x.y.z。
    $recordVersion = $next
    $fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
    if ($fileVersion) {
        $vm = [regex]::Match($fileVersion, '^(\d+\.\d+\.\d+)')
        if ($vm.Success) { $recordVersion = $vm.Groups[1].Value }
    }

    if ($entries.Count -gt 0 -and $entries[-1].version -eq $recordVersion) {
        Write-Host "  历史里已有 $recordVersion，跳过（避免重复记录）。"
        Write-Host ''
        return
    }

    $item = Get-Item $exe
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash

    $entries += [pscustomobject]@{
        date    = $today
        version = $recordVersion
        size    = $item.Length
        sha256  = $hash
        note    = (Get-ChangelogHeadline $recordVersion)
    }

    # 注意：-InputObject 传数组，PS 5.1 才会正确输出顶层数组
    $json = ConvertTo-Json -InputObject @($entries) -Depth 4
    [System.IO.File]::WriteAllText($history, $json, $utf8NoBom)
    Write-Host "  已记入 $history ：$recordVersion"
    Write-Host ''
    return
}

# 组装 Release 正文：CHANGELOG 对应版本段落 + 下载列表 + 校验值
function Write-ReleaseNotes([string]$ver, [string]$exePath, [string]$setupPath) {
    $notesPath = Join-Path $distDir "RELEASE-NOTES-$ver.md"

    $hashes = @()
    foreach ($target in @($exePath, $setupPath)) {
        if ($target -and (Test-Path $target)) {
            $name = Split-Path -Leaf $target
            $h = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $hashes += "$h  $name"
        }
    }

    $body = New-Object System.Collections.Generic.List[string]
    $body.Add("## GameWall v$ver")
    $body.Add('')

    $section = Get-ChangelogSection $ver
    if ($section) { $body.Add($section) }
    else { $body.Add("_（CHANGELOG.md 里还没有 $ver 这一条，补上之后重跑会更完整）_") }

    $body.Add('')
    $body.Add('---')
    $body.Add('')
    $body.Add('### 下载')
    $body.Add('')
    if ($setupPath -and (Test-Path $setupPath)) {
        $body.Add("- ``GameWall-Setup-$ver.exe`` —— 安装版（推荐，默认装到非系统盘）")
    }
    $body.Add('- `GameWall.exe` —— 免安装单文件版，双击即用')
    $body.Add('')
    $body.Add('### SHA256 校验')
    $body.Add('')
    $body.Add('下载后建议先校验，确认文件没被改动：')
    $body.Add('')
    $body.Add('```powershell')
    $body.Add('Get-FileHash .\GameWall.exe -Algorithm SHA256')
    $body.Add('```')
    $body.Add('')
    $body.Add('```')
    $body.Add(($hashes -join "`r`n"))
    $body.Add('```')
    $body.Add('')
    $body.Add('> 数据全在本机，联网只有「取 Steam 封面」和「查更新」两件事，都可以在设置里关掉。详见 README 的「隐私与安全」。')

    [System.IO.File]::WriteAllText($notesPath, ($body -join "`r`n"), $utf8NoBom)
    Write-Host "  已生成 $notesPath"
}

# ── 只重出 Release 说明（不动版本号、不编译）──────────────
# 用途：CHANGELOG 措辞改了、或说明模板改了，不重新打包也能更新草稿。
# 注意：跟 -Build / -Checksums 一起给时不该抢在前面返回，那种情况下面
# 的构建流程最后会自己调 Write-ReleaseNotes，这里直接跳过。
if ($Notes -and -not ($Build -or $Checksums)) {
    $csprojText = [System.IO.File]::ReadAllText($csproj, $utf8NoBom)
    $m = [regex]::Match($csprojText, '<Version>([^<]*)</Version>')
    $ver = if ($m.Success) { $m.Groups[1].Value.Trim() } else { $next }

    $exePath = Join-Path $distDir 'GameWall.exe'
    $setupPath = Join-Path $distDir "GameWall-Setup-$ver.exe"

    Write-Host ''
    Write-Host "  按 csproj 当前版本 $ver 重出 Release 说明（不编译）"
    if (-not (Test-Path $exePath)) {
        Write-Warning "  没找到 $exePath，校验值会缺失，先跑一次 -Build -Checksums"
    }
    Write-ReleaseNotes $ver $exePath $setupPath
    Write-Host ''
    return
}

if ($DryRun -or (-not ($Build -or $Rebuild))) {
    if (-not $DryRun) {
        Write-Host '  加 -Build 才会真正改文件并编译发布。'
    }
    Write-Host ''
    return
}

# ── 版本号：-Rebuild 用 csproj 里的现值，-Build 才往上加 ──
if ($Rebuild) {
    $csprojText = [System.IO.File]::ReadAllText($csproj, $utf8NoBom)
    $m = [regex]::Match($csprojText, '<Version>([^<]*)</Version>')
    if (-not $m.Success) { throw "csproj 里读不到 <Version>" }
    $next = $m.Groups[1].Value.Trim()
    Write-Host "  按 csproj 现值重打：$next（不递增）"
    Write-Host ''
}
else {
    # ── 写回 csproj ─────────────────────────────────────
    $text = [System.IO.File]::ReadAllText($csproj, $utf8NoBom)
    $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$next</Version>")
    $text = [regex]::Replace($text, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$next.0</FileVersion>")
    $text = [regex]::Replace($text, '<InformationalVersion>[^<]*</InformationalVersion>',
        "<InformationalVersion>$next</InformationalVersion>")
    [System.IO.File]::WriteAllText($csproj, $text, $utf8NoBom)
    Write-Host "  已写入 csproj: $next"
}

# ── 编译 + 发布 ─────────────────────────────────────────
$dotnetRoot = Join-Path $env:LOCALAPPDATA 'gamewall-dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = 'dotnet'
}
$env:DOTNET_ROOT = $dotnetRoot

# 程序被占用会让 publish 失败
Get-Process GameWall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

& $dotnet build (Join-Path $root 'GameWall.sln') -c Release -v m
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

& $dotnet publish (Join-Path $root 'src\GameWall\GameWall.csproj') `
    -c Release -r win-x64 --self-contained true -o $distDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $distDir 'GameWall.exe'
$item = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ''
Write-Host "  $exe"
Write-Host "  大小   : $($item.Length) 字节"
Write-Host "  SHA256 : $hash"
Write-Host ''

# ── 安装包 ──────────────────────────────────────────────
$setupExe = $null
if ($Installer) {
    $iss = Join-Path $root 'installer\GameWall.iss'
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning '  没找到 ISCC.exe（Inno Setup 6），跳过安装包。装好后再跑一次，或手动 ISCC.exe installer\GameWall.iss'
    }
    else {
        & $iscc $iss | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

        $setupExe = Join-Path $distDir "GameWall-Setup-$next.exe"
        if (-not (Test-Path $setupExe)) { throw "安装包没生成：$setupExe" }

        $setupItem = Get-Item $setupExe
        Write-Host "  $setupExe"
        Write-Host "  大小   : $($setupItem.Length) 字节"
        Write-Host "  SHA256 : $((Get-FileHash $setupExe -Algorithm SHA256).Hash)"
        Write-Host ''
    }
}

# ── 校验值与 Release 说明 ────────────────────────────────
if ($Checksums) {
    $lines = @()

    # 只列这一版真正要上传的产物，不要列 dist 里残留的旧文件
    foreach ($target in @($exe, $setupExe)) {
        if ($target -and (Test-Path $target)) {
            $name = Split-Path -Leaf $target
            $h = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $lines += "$h  $name"
        }
    }

    $sumFile = Join-Path $distDir 'SHA256SUMS.txt'
    $content = "# GameWall $next  SHA256`r`n" +
               "# 校验：certutil -hashfile <文件> SHA256   或   Get-FileHash <文件> -Algorithm SHA256`r`n" +
               ($lines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($sumFile, $content, $utf8NoBom)
    Write-Host "  已生成 $sumFile"

    Write-ReleaseNotes $next $exe $setupExe
    Write-Host ''
}

Write-Host '  下一步：'
Write-Host "    1. 把 dist 里的产物传到 GitHub Release（tag 用 v$next）"
Write-Host "    2. Release 正文直接贴 RELEASE-NOTES-$next.md 的内容"
Write-Host "    3. 跑 .\tools\release.ps1 -Record 把这一版记进历史"
Write-Host ''
