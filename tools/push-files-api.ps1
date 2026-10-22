# 通过 GitHub Contents API 提交文件。
#
# 用途：当本机直连 github.com:443 不稳定（git push 报
# "Recv failure: Connection was reset" / "Failed to connect"）时，
# api.github.com 通常仍然可达，可以走 REST 接口把改动一个个写上去。
#
# 注意：每调用一次 Put-File 会产生一个独立提交。若一次要提交多个文件，
# 提交后再用 Git Data API 合并（见 README 的可选步骤），或直接等 git push 恢复。
#
# 用法：
#   .\tools\push-files-api.ps1 -Message "fix: xxx" -Files README.md,src/GameWall/App.xaml.cs
#
# 前置：$env:TEMP\gw_tok.txt 里存着 GitHub token（见 README「维护」一节）。

param(
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$Owner = 'jichun29',
    [string]$Repo = 'GameWall-Launcher',
    [string]$Branch = 'main',
    [Parameter(Mandatory = $true)][string[]]$Files
)

$ErrorActionPreference = 'Stop'
$tok = [System.IO.File]::ReadAllText("$env:TEMP\gw_tok.txt").Trim()
$h = @{ Authorization = "token $tok"; 'User-Agent' = 'GameWall'; Accept = 'application/vnd.github+json' }
$repoRoot = Split-Path -Parent $PSScriptRoot

foreach ($relPath in $Files) {
    $local = Join-Path $repoRoot $relPath
    if (-not (Test-Path $local)) { Write-Warning "skip (missing): $relPath"; continue }

    $apiPath = ($relPath -replace '\\', '/')
    $uri = "https://api.github.com/repos/$Owner/$Repo/contents/$apiPath"

    # 已存在的文件必须带上当前 blob 的 sha，否则 GitHub 返回 422
    $sha = $null
    try {
        $cur = Invoke-RestMethod -Uri "$uri`?ref=$Branch" -Headers $h -Method Get
        $sha = $cur.sha
    } catch { }

    $body = @{
        message = $Message
        content = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($local))
        branch  = $Branch
    }
    if ($sha) { $body.sha = $sha }

    # PowerShell 5.1 的 Invoke-RestMethod 默认按 ISO-8859-1 发 string body，
    # 中文提交信息会变成 "?" 并触发 422，所以这里先转成 UTF-8 字节。
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress))
    $r = Invoke-RestMethod -Uri $uri -Headers $h -Method Put -Body $bytes -ContentType 'application/json; charset=utf-8'
    "ok  {0}  -> {1}" -f $apiPath, $r.commit.sha
}
