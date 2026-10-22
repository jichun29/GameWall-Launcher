# 离线重拍 README 用的界面截图。
#
# 走 tools/snap-host 里的截图宿主：它直接 new 出 GameWall 的窗口，
# 用 RenderTargetBitmap 渲染视觉树再存 PNG。
# 远程桌面 / 没有桌面合成的会话里，PrintWindow 只能拿到黑屏、
# CopyFromScreen 只能拿到壁纸，只有这条路径拿得到真实像素。
#
# 注意：抓出来的画面取决于 %AppData%\GameWall\library.json 里当前有什么游戏。
# README 里那套图用的是 18 个带 Steam 官方竖版封面的示例库；
# 本地库只有两三个游戏时重拍会显得空，先用 tools/seed-demo.ps1 铺一批示例数据再拍。
#
# 用法：
#   .\tools\shoot-offline.ps1              # 全套 16 张覆盖到 docs/screenshots
#   .\tools\shoot-offline.ps1 -OutDir tmp  # 输出到别的目录先看效果
#   .\tools\shoot-offline.ps1 -DarkOnly    # 只出深色那 8 张

param(
    [string]$OutDir = "docs\screenshots",
    [switch]$DarkOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$hostDir = Join-Path $PSScriptRoot "snap-host"
$dotnet = Join-Path $env:LOCALAPPDATA "gamewall-dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"   # 装过全局 SDK 的话直接用它
}

$env:DOTNET_ROOT = Split-Path -Parent $dotnet

if (-not $SkipBuild) {
    Write-Host "==> 编译产品程序（截图宿主引用它的 Release 输出）"
    & $dotnet build (Join-Path $root "GameWall.sln") -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "GameWall 编译失败" }

    Write-Host "==> 编译截图宿主"
    & $dotnet build (Join-Path $hostDir "SnapHost.csproj") -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "截图宿主编译失败" }
}

$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 场景格式：<文件名>:<窗口>:<主题>[:<视图/分类>]
$scenes = @(
    "dark-card:main:Dark:Card",
    "dark-list:main:Dark:List",
    "dark-dialog-settings:settings:Dark",
    "dark-dialog-tidy:tidy:Dark",
    "dark-dialog-toolbox:toolbox:Dark:hardware",
    "dark-dialog-toolbox-opt:toolbox:Dark:游戏性能",
    "dark-dialog-toolbox-power:toolbox:Dark:power",
    "dark-firstrun:firstrun:Dark"
)

if (-not $DarkOnly) {
    $scenes += @(
        "light-card:main:Light:Card",
        "light-list:main:Light:List",
        "light-dialog-settings:settings:Light",
        "light-dialog-tidy:tidy:Light",
        "light-dialog-toolbox:toolbox:Light:hardware",
        "light-dialog-toolbox-opt:toolbox:Light:游戏性能",
        "light-dialog-toolbox-power:toolbox:Light:power",
        "light-firstrun:firstrun:Light"
    )
}

$hostExe = Join-Path $hostDir "bin\Release\net8.0-windows\SnapHost.dll"
Write-Host "==> 渲染 $($scenes.Count) 张到 $out"
& $dotnet $hostExe $out @scenes
if ($LASTEXITCODE -ne 0) { throw "截图宿主执行失败" }

Get-ChildItem $out -Filter *.png |
    Where-Object { $scenes -match ("^" + [regex]::Escape($_.BaseName) + ":") } |
    Sort-Object Name |
    ForEach-Object { "{0,-32} {1,9:N0} 字节" -f $_.Name, $_.Length }
