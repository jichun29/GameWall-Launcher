# 按主题批量抓图：改动 settings.json 的 Theme 后重启 GameWall，
# 依次切换卡片式 / 列表式两种视图并各抓一张，再抓设置与关于两个对话框。
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

foreach ($n in @('card', 'list', 'dialog-about')) {
    $src = Join-Path $outDir "$n.png"
    if (Test-Path $src) {
        Move-Item -Force $src (Join-Path $outDir "$Prefix-$n.png")
    }
}

# 4) 再抓一张设置对话框（同一主题，避免用旧主题的残留图）
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'shot-dialogs.ps1') -ProcessName GameWall -OutDir $outDir 2>&1 | Out-Null
$dlg = Join-Path $outDir 'dialog-设置.png'
if (Test-Path $dlg) {
    Move-Item -Force $dlg (Join-Path $outDir "$Prefix-dialog-settings.png")
}

# shot-dialogs.ps1 会额外留一张 shot-main.png，内容和 wall 重复，顺手清掉
$stray = Join-Path $outDir 'shot-main.png'
if (Test-Path $stray) { Remove-Item -Force $stray }

# 5) 首次运行引导：把 FirstRunCompleted 复位重启一次，抓完再改回来
$s = Get-Content $sp -Raw -Encoding UTF8 | ConvertFrom-Json
$s.FirstRunCompleted = $false
[System.IO.File]::WriteAllText($sp, (ConvertTo-Json -InputObject $s -Depth 5), $utf8)
Get-Process -Name GameWall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 3
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'capture.ps1') `
    -Title '欢迎使用 GameWall' -Out (Join-Path $outDir "$Prefix-firstrun.png") 2>&1 | Out-Null

$s = Get-Content $sp -Raw -Encoding UTF8 | ConvertFrom-Json
$s.FirstRunCompleted = $true
[System.IO.File]::WriteAllText($sp, (ConvertTo-Json -InputObject $s -Depth 5), $utf8)

# 6) 引导页是 FirstRunCompleted=false 时启动的，改回 true 后必须再重启一次，
#    否则留在前台运行的那个进程仍挂着「欢迎来到 GameWall」对话框
Get-Process -Name GameWall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 2

Get-ChildItem (Join-Path $outDir "$Prefix-*.png") | ForEach-Object {
    Write-Host ("{0}  {1} bytes" -f $_.Name, $_.Length)
}
