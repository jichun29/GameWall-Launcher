# GameWall 发版脚本
#
# 版本规则（由 tools/release-history.json 记录，必须提交进仓库）：
#   * 同一天最多推一个「大版本」——大版本指第二段（minor）进位，如 1.5 → 1.6。
#   * 同一天后续所有发版只递增第三段（patch），如 1.6 → 1.6.1 → 1.6.2。
#   * 隔天第一次发版才允许再有新的大版本。
#
# 用法：
#   1) 只算下一个版本号，不改任何文件、不发布
#      .\tools\release.ps1 -DryRun
#   2) 算好版本号 → 写回 csproj → 编译 → 发布单文件 exe（不发布到 GitHub）
#      .\tools\release.ps1 -Build
#   3) 全流程：算版本 → 改 csproj → 编译 → 发布 → 记录历史
#      .\tools\release.ps1 -Build -Record
#
# 打了 tag、传了 release 之后记得再跑一次 -Record 把那一条写进历史，
# 否则下次同一天还会算出同样的大版本号。

param(
    [switch]$Build,
    [switch]$Record,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\GameWall\GameWall.csproj'
$history = Join-Path $PSScriptRoot 'release-history.json'
$distDir = Join-Path $root 'dist'

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ── 读历史 ──────────────────────────────────────────────
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

$today = (Get-Date).ToString('yyyy-MM-dd')

# 找最近一条记录
$last = $null
if ($entries.Count -gt 0) {
    $last = $entries[$entries.Count - 1]
}

if ($last -eq $null) {
    $next = '1.0.0'
}
else {
    $parts = ($last.version -split '\.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = 0
    if ($parts.Count -ge 3) { $patch = [int]$parts[2] }

    if ($last.date -eq $today) {
        # 今天已经发过一个大版本，只能递增第三段
        $next = "$major.$minor.$($patch + 1)"
        $reason = "今天（$today）已发布 $($last.version)，同一天只递增第三段"
    }
    else {
        # 换了一天，允许再推一个新的大版本（minor 进位）
        $next = "$major.$($minor + 1).0"
        $reason = "上次发布在 $($last.date)，新的一天允许新的大版本"
    }
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

if ($DryRun -or (-not $Build)) {
    if (-not $DryRun) {
        Write-Host '  加 -Build 才会真正改文件并编译发布。'
    }
    return
}

# ── 写回 csproj ─────────────────────────────────────────
$text = [System.IO.File]::ReadAllText($csproj, $utf8NoBom)
$text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$next</Version>")
$text = [regex]::Replace($text, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$next.0</FileVersion>")
$text = [regex]::Replace($text, '<InformationalVersion>[^<]*</InformationalVersion>',
    "<InformationalVersion>$next</InformationalVersion>")
[System.IO.File]::WriteAllText($csproj, $text, $utf8NoBom)
Write-Host "  已写入 csproj: $next"

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

# ── 记录历史 ────────────────────────────────────────────
if ($Record) {
    $entries += [pscustomobject]@{
        date    = $today
        version = $next
        size    = $item.Length
        sha256  = $hash
    }
    $json = ConvertTo-Json -InputObject @($entries) -Depth 4
    [System.IO.File]::WriteAllText($history, $json, $utf8NoBom)
    Write-Host "  已记入 $history"
    Write-Host ''
}
else {
    Write-Host '  提示：发布完 GitHub release 之后，再跑一次 -Record 把这个版本记进历史。'
    Write-Host ''
}
