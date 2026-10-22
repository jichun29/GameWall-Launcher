# 按主题批量抓图：改动 settings.json 的 Theme 后重启 GameWall，
# 依次切换大图墙 / 列表 / 紧凑三种视图并各抓一张。
# 用法：powershell -ExecutionPolicy Bypass -File tools\shoot-theme.ps1 -Theme Dark
param(
    [ValidateSet("Dark", "Light", "System")][string]$Theme = "Dark",
    [string]$Prefix = ""
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:AppData 'GameWall'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$repo = Split-Path -Parent $PSScriptRoot

# 1) 写主题到配置
$sp = Join-Path $root 'settings.json'
$s = Get-Content $sp -Raw -Encoding UTF8 | ConvertFrom-Json
$s.Theme = $Theme
$s.MinimizeToTray = $false
$s.FirstRunCompleted = $true
$s.WindowWidth = 1280
$s.WindowHeight = 820
$s | Add-Member -NotePropertyName WindowLeft -NotePropertyValue $null -Force
$s | Add-Member -NotePropertyName WindowTop -NotePropertyValue $null -Force
[System.IO.File]::WriteAllText($sp, (ConvertTo-Json -InputObject $s -Depth 5), $utf8)

# 2) 重启应用（必须完全重启，否则窗口会残留上一帧的像素）
Get-Process -Name GameWall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$exe = Join-Path $repo 'src\GameWall\bin\Release\net8.0-windows\win-x64\GameWall.exe'
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

# 3) 切换视图并抓图
$outDir = Join-Path $repo 'docs\screenshots'
if (-not $Prefix) { $Prefix = $Theme.ToLower() }
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-screenshots.ps1') -OutDir $outDir 2>&1 | Out-Null

foreach ($n in @('wall', 'list', 'compact')) {
    $src = Join-Path $outDir "$n.png"
    if (Test-Path $src) {
        Move-Item -Force $src (Join-Path $outDir "$Prefix-$n.png")
    }
}

Get-ChildItem (Join-Path $outDir "$Prefix-*.png") | ForEach-Object {
    Write-Host ("{0}  {1} bytes" -f $_.Name, $_.Length)
}
