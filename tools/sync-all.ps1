# 一条命令把 main 分支同步推到 GitHub / Gitee / CNB 三处。
#
# 为什么要有这个脚本：三个平台各推一次容易漏，而且每个平台的认证方式不一样
# （GitHub 走凭据管理器、Gitee 走私人令牌、CNB 走 CLI 登录态），
# 手工敲三次迟早出错。这里统一处理，逐个推、失败不影响其他平台。
#
# 用法：
#   .\tools\sync-all.ps1                        # 推 main 到三个平台
#   .\tools\sync-all.ps1 -Only github,cnb       # 只推指定平台
#   .\tools\sync-all.ps1 -DryRun                # 只显示要推什么，不实际推送
#   .\tools\sync-all.ps1 -GiteeToken ghp_xxx    # 临时指定 Gitee 令牌
#
# 凭据来源（按优先级从高到低）：
#   GitHub  git credential fill 读 Windows 凭据管理器里的 git:https://github.com
#   Gitee   -GiteeToken 参数 → $env:GITEE_TOKEN → %TEMP%\gw_gitee_tok.txt
#   CNB     ~/.cnb/token（cnb login 自动写入，直接读里面的 access_token）
#
# 退出码：全部成功 0，有平台失败 1。

[CmdletBinding()]
param(
    [string[]]$Only = @('github', 'gitee', 'cnb'),
    [string]$Branch = 'main',
    [string]$GiteeToken,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 本机有一个全局代理指到 127.0.0.1:29982，对这三个 git 端点都不通，
# 直连反而正常。这里在进程内清掉，不动用户的系统设置。
$env:HTTP_PROXY = ''
$env:HTTPS_PROXY = ''
$env:http_proxy = ''
$env:https_proxy = ''
$env:NO_PROXY = '*'
$env:GIT_TERMINAL_PROMPT = '0'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$platforms = @{
    github = @{
        Name   = 'GitHub'
        Remote = 'origin'
        Url    = 'https://github.com/jichun29/GameWall-Launcher.git'
        Web    = 'https://github.com/jichun29/GameWall-Launcher'
    }
    gitee  = @{
        Name   = 'Gitee'
        Remote = 'gitee'
        Url    = 'https://gitee.com/jichun29/GameWall-Launcher.git'
        Web    = 'https://gitee.com/jichun29/GameWall-Launcher'
    }
    cnb    = @{
        Name   = 'CNB'
        Remote = 'cnb'
        Url    = 'https://cnb.cool/jichun29/GameWall-Launcher.git'
        Web    = 'https://cnb.cool/jichun29/GameWall-Launcher'
    }
}

function Get-GitToken {
    <#
      从 git credential fill 拿某个 host 的凭据。
      返回 @{ User; Password }，取不到返回 $null。
    #>
    param([string]$HostName)

    $input_ = "protocol=https`nhost=$HostName`n`n"
    try {
        $out = $input_ | & git credential fill 2>$null
    } catch {
        return $null
    }
    if (-not $out) { return $null }

    $user = ($out | Where-Object { $_ -like 'username=*' }) -replace '^username=', ''
    $pass = ($out | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
    if ([string]::IsNullOrWhiteSpace($pass)) { return $null }
    return @{ User = $user; Password = $pass }
}

function Get-CnbToken {
    <#
      CNB CLI 登录后把令牌写在 ~/.cnb/token（JSON，auths -> cnb.cool -> access_token）。
    #>
    $path = Join-Path $HOME '.cnb/token'
    if (-not (Test-Path $path)) { return $null }
    try {
        $j = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return $j.auths.'cnb.cool'.access_token
    } catch {
        return $null
    }
}

function Resolve-Credential {
    <#
      按平台取认证信息，返回 @{ Args = <git 额外参数数组> } 或 $null。
      用 http.extraHeader 而不是把令牌拼进 URL，
      免得令牌被写进 .git/config 永久留着。
    #>
    param([string]$Key)

    switch ($Key) {
        'github' {
            $c = Get-GitToken -HostName 'github.com'
            if (-not $c) { return $null }
            # 凭据管理器已经能自动应答，这里不需要塞 header，
            # 但显式给一份能避免 GCM 弹窗。
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::ASCII.GetBytes("$($c.User):$($c.Password)"))
            return @{ Args = @('-c', "http.https://github.com/.extraheader=Authorization: Basic $b64") }
        }
        'gitee' {
            $tok = $GiteeToken
            if ([string]::IsNullOrWhiteSpace($tok)) { $tok = $env:GITEE_TOKEN }
            if ([string]::IsNullOrWhiteSpace($tok)) {
                $f = Join-Path $env:TEMP 'gw_gitee_tok.txt'
                if (Test-Path $f) { $tok = (Get-Content $f -Raw).Trim() }
            }
            if ([string]::IsNullOrWhiteSpace($tok)) { return $null }
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::ASCII.GetBytes("oauth2:$tok"))
            return @{ Args = @('-c', "http.https://gitee.com/.extraheader=Authorization: Basic $b64") }
        }
        'cnb' {
            $tok = Get-CnbToken
            if ([string]::IsNullOrWhiteSpace($tok)) { return $null }
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::ASCII.GetBytes("oauth2:$tok"))
            return @{ Args = @('-c', "http.https://cnb.cool/.extraheader=Authorization: Basic $b64") }
        }
    }
    return $null
}

# ---------- 前置检查 ----------

$head = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($head -ne $Branch) {
    Write-Warning "当前在 $head 分支，不是 $Branch。先切过去再跑，或加 -Branch $head"
    exit 1
}

$dirty = & git status --porcelain
if ($dirty) {
    Write-Warning "工作区有未提交改动，推上去的只是已提交的内容："
    $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
}

$sha = (& git rev-parse --short HEAD).Trim()
$subject = (& git log -1 --pretty=%s).Trim()
Write-Host ""
Write-Host "待同步：$sha  $subject" -ForegroundColor Cyan
Write-Host "分支：$Branch" -ForegroundColor DarkGray
Write-Host ""

# ---------- 逐个平台推送 ----------

$results = @()

foreach ($key in $Only) {
    if (-not $platforms.ContainsKey($key)) {
        Write-Warning "未知平台：$key（可选 github / gitee / cnb）"
        continue
    }
    $p = $platforms[$key]

    # remote 不存在就补上，方便新机器直接跑
    $existing = & git remote 2>$null
    if ($existing -notcontains $p.Remote) {
        if ($DryRun) {
            Write-Host "  [dry] 补 remote $($p.Remote) -> $($p.Url)" -ForegroundColor DarkGray
        } else {
            & git remote add $p.Remote $p.Url 2>$null
        }
    }

    $cred = Resolve-Credential -Key $key
    if (-not $cred) {
        $hint = switch ($key) {
            'gitee' { "缺 Gitee 令牌。加 -GiteeToken，或设 `$env:GITEE_TOKEN，或写进 $env:TEMP\gw_gitee_tok.txt" }
            'cnb' { "缺 CNB 登录态。先跑 cnb login" }
            default { "缺 GitHub 凭据。检查 Windows 凭据管理器里的 git:https://github.com" }
        }
        Write-Host ("  {0,-7} 跳过    {1}" -f $p.Name, $hint) -ForegroundColor Yellow
        $results += [pscustomobject]@{ Name = $p.Name; State = '跳过'; Note = $hint }
        continue
    }

    if ($DryRun) {
        Write-Host ("  {0,-7} [dry]   {1} -> {2}" -f $p.Name, $Branch, $p.Url) -ForegroundColor DarkGray
        $results += [pscustomobject]@{ Name = $p.Name; State = 'dry'; Note = '' }
        continue
    }

    Write-Host ("  {0,-7} 推送中…" -f $p.Name) -ForegroundColor Gray
    $out = & git @($cred.Args) push $p.Remote "$Branch`:$Branch" 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        Write-Host ("  {0,-7} OK      {1}" -f $p.Name, $p.Web) -ForegroundColor Green
        $results += [pscustomobject]@{ Name = $p.Name; State = 'OK'; Note = '' }
    } else {
        Write-Host ("  {0,-7} 失败" -f $p.Name) -ForegroundColor Red
        $out | Where-Object { $_ -and $_ -notmatch '^\s*$' } |
            Select-Object -First 6 | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
        $results += [pscustomobject]@{ Name = $p.Name; State = '失败'; Note = ($out -join ' ').Trim() }
    }
}

# ---------- 汇总 ----------

Write-Host ""
Write-Host "—— 汇总 ——" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = switch ($r.State) {
        'OK' { 'Green' }
        '失败' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ("  {0,-7} {1,-6} {2}" -f $r.Name, $r.State, $r.Note) -ForegroundColor $color
}

$failed = @($results | Where-Object { $_.State -eq '失败' })
$skipped = @($results | Where-Object { $_.State -eq '跳过' })

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "有 $($failed.Count) 个平台没推上去，见上面的报错。" -ForegroundColor Red
    exit 1
}
if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "有 $($skipped.Count) 个平台因缺凭据被跳过，补上令牌后可只重推它们。" -ForegroundColor Yellow
    exit 1
}

$ok = @($results | Where-Object { $_.State -eq 'OK' })
Write-Host ""
Write-Host "完成：$($ok.Count) 个平台已同步到 $sha" -ForegroundColor Green
exit 0
