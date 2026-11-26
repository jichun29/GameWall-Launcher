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

# 网络这块踩过坑，记一下：
#   本机有全局代理（127.0.0.1:29982），GitHub 走它能通，直连会被 reset；
#   Gitee / CNB 反过来，走代理反而 502，必须直连。
# 所以不能一刀切清空代理，而是按平台分别给 git 指定。
$env:GIT_TERMINAL_PROMPT = '0'
$proxyUrl = $env:HTTPS_PROXY
if ([string]::IsNullOrWhiteSpace($proxyUrl)) { $proxyUrl = $env:HTTP_PROXY }
if ([string]::IsNullOrWhiteSpace($proxyUrl)) { $proxyUrl = $env:https_proxy }
if ([string]::IsNullOrWhiteSpace($proxyUrl)) { $proxyUrl = $env:http_proxy }

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$platforms = @{
    github = @{
        Name   = 'GitHub'
        Remote = 'origin'
        Url    = 'https://github.com/jichun29/GameWall-Launcher.git'
        Web    = 'https://github.com/jichun29/GameWall-Launcher'
        Proxy  = $true
    }
    gitee  = @{
        Name   = 'Gitee'
        Remote = 'gitee'
        Url    = 'https://gitee.com/jichun29/GameWall-Launcher.git'
        Web    = 'https://gitee.com/jichun29/GameWall-Launcher'
        Proxy  = $false
    }
    cnb    = @{
        Name   = 'CNB'
        Remote = 'cnb'
        Url    = 'https://cnb.cool/jichun29/GameWall-Launcher.git'
        Web    = 'https://cnb.cool/jichun29/GameWall-Launcher'
        Proxy  = $false
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
      按平台取认证信息，返回 @{ AuthArgs = <git -c 参数数组> } 或 $null。
      用 http.extraHeader 而不是把令牌拼进 URL，
      免得令牌被写进 .git/config 永久留着。
      代理参数由调用方按"先直连再代理"的策略单独拼，这里只管认证。
    #>
    param([string]$Key)

    switch ($Key) {
        'github' {
            $c = Get-GitToken -HostName 'github.com'
            if (-not $c) { return $null }
            # 凭据管理器已经能自动应答，这里显式给一份能避免 GCM 弹窗卡住
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::ASCII.GetBytes("$($c.User):$($c.Password)"))
            return @{ AuthArgs = @('-c', "http.https://github.com/.extraheader=Authorization: Basic $b64") }
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
            return @{ AuthArgs = @('-c', "http.https://gitee.com/.extraheader=Authorization: Basic $b64") }
        }
        'cnb' {
            $tok = Get-CnbToken
            if ([string]::IsNullOrWhiteSpace($tok)) { return $null }
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::ASCII.GetBytes("oauth2:$tok"))
            return @{ AuthArgs = @('-c', "http.https://cnb.cool/.extraheader=Authorization: Basic $b64") }
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

    # 国内直连 GitHub 经常 "Connection was reset" / "missing close_notify"，
    # 走本机代理有时又 502，两边都不稳。所以每一轮先直连、直连不行再走代理，
    # 最多 3 轮退避重试，别让偶发断线算成失败。
    $maxTry = 3
    $out = $null
    $code = 1
    # git 把进度和报错打在 stderr，$ErrorActionPreference=Stop 会把它当
    # 终止性异常抛出来，导致重试循环根本没机会跑。这里临时放宽。
    $savedEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        for ($try = 1; $try -le $maxTry; $try++) {
            foreach ($viaProxy in @($false, $true)) {
                # 平台不许走代理（Gitee / CNB 走代理会 502）就只试直连
                if ($viaProxy -and (-not $proxyUrl -or -not $p.Proxy)) { continue }

                $how = if ($viaProxy) { '走代理' } else { '直连' }
                $label = if ($try -eq 1) { "推送中（$how）…" } else { "重试 $try/$maxTry（$how）…" }
                Write-Host ("  {0,-7} {1}" -f $p.Name, $label) -ForegroundColor Gray

                $useProxy = $viaProxy
                $runArgs = @()
                if ($useProxy) {
                    $runArgs = @('-c', "http.proxy=$proxyUrl", '-c', "https.proxy=$proxyUrl")
                } else {
                    $runArgs = @('-c', 'http.proxy=', '-c', 'https.proxy=')
                }
                $runArgs += $cred.AuthArgs

                # 关键：git -c http.proxy= 只清掉 git 自己的配置，
                # 环境变量里那套 https_proxy / HTTPS_PROXY 照样会被 libcurl 读走。
                # 之前一直"直连被 reset"，其实是代理端口早就换了、请求全打在死代理上。
                # 直连这一轮必须把这些变量临时摘掉，走代理那轮再把它们设回去。
                $proxyVars = @('http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY')
                $savedVars = @{}
                foreach ($v in $proxyVars) { $savedVars[$v] = [Environment]::GetEnvironmentVariable($v) }
                try {
                    if ($useProxy) {
                        foreach ($v in $proxyVars) { [Environment]::SetEnvironmentVariable($v, $proxyUrl) }
                    } else {
                        foreach ($v in $proxyVars) { [Environment]::SetEnvironmentVariable($v, $null) }
                    }

                    $out = & git @runArgs push $p.Remote "$Branch`:$Branch" 2>&1
                    $code = $LASTEXITCODE
                } finally {
                    foreach ($v in $proxyVars) { [Environment]::SetEnvironmentVariable($v, $savedVars[$v]) }
                }
                if ($code -eq 0) { break }

                # 认证失败之类重试无意义
                $text = ($out -join "`n")
                if ($text -match 'Authentication failed|Permission denied|not found|\[rejected\]') {
                    break
                }
            }
            if ($code -eq 0) { break }
            if ($try -lt $maxTry) { Start-Sleep -Seconds (2 * $try) }
        }
    } finally {
        $ErrorActionPreference = $savedEap
    }

    if ($code -eq 0) {
        Write-Host ("  {0,-7} OK      {1}" -f $p.Name, $p.Web) -ForegroundColor Green
        $results += [pscustomobject]@{ Name = $p.Name; State = 'OK'; Note = '' }
    } else {
        # push 报错不等于没推上去：网络在握手阶段断了，但服务端可能早就收到并落库了。
        # 这种误报很讨厌 —— 明明同步好了却报红，逼着人去手工核实。
        # 所以失败后先问一次远端 HEAD，跟本地一致就算成功。
        $remoteSha = $null
        try {
            $savedEap2 = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            # 核实也走直连，同样要摘掉环境变量里的代理，否则又是打在死代理上
            $proxyVars2 = @('http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY')
            $savedVars2 = @{}
            foreach ($v in $proxyVars2) { $savedVars2[$v] = [Environment]::GetEnvironmentVariable($v) }
            try {
                foreach ($v in $proxyVars2) { [Environment]::SetEnvironmentVariable($v, $null) }
                $lsArgs = @('-c', 'http.proxy=', '-c', 'https.proxy=') + $cred.AuthArgs
                $lsOut = & git @lsArgs ls-remote $p.Remote "refs/heads/$Branch" 2>&1
            } finally {
                foreach ($v in $proxyVars2) { [Environment]::SetEnvironmentVariable($v, $savedVars2[$v]) }
            }
            $ErrorActionPreference = $savedEap2

            $headSha = (& git rev-parse HEAD).Trim()
            foreach ($line in $lsOut) {
                if ($line -match '^([0-9a-f]{40})\s') {
                    $remoteSha = $Matches[1]
                    if ($remoteSha -eq $headSha) { $code = 0 }
                    break
                }
            }
        } catch {
            # 核实不了就按失败报，别假装成功
        }

        if ($code -eq 0) {
            Write-Host ("  {0,-7} OK      {1}" -f $p.Name, $p.Web) -ForegroundColor Green
            Write-Host "          （推送时连接断了，但核对远端已是同一提交，内容一致）" -ForegroundColor DarkGray
            $results += [pscustomobject]@{ Name = $p.Name; State = 'OK'; Note = '断线但已同步' }
        } else {
            Write-Host ("  {0,-7} 失败" -f $p.Name) -ForegroundColor Red
            if ($remoteSha) {
                Write-Host ("          远端 HEAD: $($remoteSha.Substring(0,7))，和本地不一致") -ForegroundColor DarkYellow
            }
            $out | Where-Object { $_ -and $_ -notmatch '^\s*$' } |
                Select-Object -First 6 | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
            $results += [pscustomobject]@{ Name = $p.Name; State = '失败'; Note = ($out -join ' ').Trim() }
        }
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
