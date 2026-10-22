$ErrorActionPreference = 'Stop'
$tok = [System.IO.File]::ReadAllText("$env:TEMP\gw_tok.txt")

$repoRoot = Split-Path -Parent $PSScriptRoot
$file = Join-Path $repoRoot 'dist\GameWall.exe'
if (-not (Test-Path $file)) { throw "exe not found: $file" }

$relId = 420371167
$url = "https://uploads.github.com/repos/jichun29/GameWall-Launcher/releases/$relId/assets?name=GameWall.exe"

$fi = Get-Item $file
"uploading {0} bytes ..." -f $fi.Length
$fs = [System.IO.File]::OpenRead($file)
try {
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = 'POST'
    $req.ContentType = 'application/octet-stream'
    $req.ContentLength = $fi.Length
    $req.Timeout = 600000
    $req.ReadWriteTimeout = 600000
    $req.AllowWriteStreamBuffering = $false
    $req.Headers.Add('Authorization', "token $tok")
    # Accept / User-Agent 是受限标头，只能通过专用属性设置
    $req.Accept = 'application/vnd.github+json'
    $req.UserAgent = 'GameWall'
    $req.ServicePoint.Expect100Continue = $false

    $body = $req.GetRequestStream()
    $buf = New-Object byte[] 1048576
    while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
        $body.Write($buf, 0, $n)
    }
    $body.Close()
    $resp = $req.GetResponse()
    $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
    $txt = $sr.ReadToEnd()
    $sr.Close()
    $resp.Close()
    $j = $txt | ConvertFrom-Json
    "asset: {0} size={1} state={2}" -f $j.name, $j.size, $j.state
}
finally { $fs.Close() }
