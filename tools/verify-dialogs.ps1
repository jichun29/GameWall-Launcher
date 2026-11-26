# 各对话框能正常打开的检查
#
# 用途：XAML 里写错资源名（比如 {StaticResource BoolToVis} 但那个窗口取不到）
# 会在布局阶段抛异常，表现是「窗口出来了但里面空白 / 弹出报错框」。
# 编译期查不出来，只能真跑一遍。这个脚本把主要对话框挨个打开再关掉。
#
# 用法：.\tools\verify-dialogs.ps1

param([string]$ExePath = "")

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$fail = 0
function Fail($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red; $script:fail++ }
function Ok($msg) { Write-Host "[ OK ] $msg" -ForegroundColor Green }

Get-Process GameWall -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
Start-Sleep -Milliseconds 600

if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot "..\dist\GameWall.exe" }
if (-not (Test-Path $ExePath)) { Fail "找不到 exe：$ExePath"; exit 1 }

$proc = Start-Process -FilePath $ExePath -PassThru
for ($i = 0; $i -lt 600; $i++) {
    Start-Sleep -Milliseconds 15
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.MainWindowHandle -eq 0) { Fail "主窗口没起来"; exit 1 }
Start-Sleep -Milliseconds 2000
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Find-ByName($parent, $name) {
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

# 顶栏按钮都带图标前缀（"⚙ 设置"、"🎛 工具箱"），按名字精确匹配找不到，
# 这里按「包含」扫一遍。图标是 emoji，不做硬编码。
function Find-Containing($parent, $keyword) {
    $all = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $all) {
        if ($b.Current.Name -like "*$keyword*") { return $b }
    }
    return $null
}

function Find-Window($title) {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 100
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $title)))
        if ($w) {
            # 必须是有内容的窗口，不能是只有标题的空壳
            $n = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition).Count
            if ($n -gt 0) { return $w }
        }
    }
    return $null
}

function Any-ErrorDialog {
    # 未处理异常会弹一个「GameWall 遇到错误」的框，标题里带 GameWall
    $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "#32770")))
    return $w
}

# ── 设置窗口 ────────────────────────────────────────────
# 顶栏「⚙ 设置」按钮
$btn = Find-Containing $root "设置"
if (-not $btn) { Fail "找不到「设置」按钮" }
else {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $w = Find-Window "设置"
    if (-not $w) { Fail "设置窗口没打开或内容为空" }
    else {
        # 本版新增的两个按钮必须真的在界面上
        $exp = Find-ByName $w "导出游戏库…"
        $imp = Find-ByName $w "导入游戏库…"
        if (-not $exp) { Fail "设置里找不到「导出游戏库…」" }
        elseif (-not $imp) { Fail "设置里找不到「导入游戏库…」" }
        else { Ok "设置窗口正常，备份与迁移两个按钮都在" }

        $close = Find-ByName $w "取消"
        if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        Start-Sleep -Milliseconds 600
    }
}

# ── 优化项 / 游戏工具箱 ─────────────────────────────────
$btn = Find-Containing $root "工具箱"
if ($btn) {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $w = Find-Window "游戏工具箱"
    if (-not $w) { Fail "游戏工具箱没打开或内容为空" }
    else {
        Ok "游戏工具箱正常"
        $close = Find-ByName $w "关闭"
        if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        Start-Sleep -Milliseconds 600
    }
}

Write-Host ""
if ($fail -eq 0) {
    Write-Host "全部通过" -ForegroundColor Green
} else {
    Write-Host "$fail 项失败" -ForegroundColor Red
}

Get-Process GameWall -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
exit $fail
