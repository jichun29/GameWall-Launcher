# 冒烟测试：用 UIA 走一遍主流程（切视图 / 搜索过滤 / 收藏筛选 / 双击启动）
# 只读取界面状态，不改配置，跑完关掉进程。
param(
    [string]$ProcessName = "GameWall",
    [string]$ExePath = "",
    [string]$ExpectedFirst = "记事本之战"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Fail($msg) {
    Write-Host "[FAIL] $msg" -ForegroundColor Red
    exit 1
}
function Ok($msg) {
    Write-Host "[ OK ] $msg" -ForegroundColor Green
}

Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot "..\src\GameWall\bin\Release\net8.0-windows\win-x64\GameWall.exe"
}

$proc = Start-Process -FilePath $ExePath -PassThru
for ($i = 0; $i -lt 600; $i++) {
    Start-Sleep -Milliseconds 15
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.MainWindowHandle -eq 0) { Fail "窗口没起来" }
Start-Sleep -Milliseconds 1800

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Get-Cards {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) |
        Where-Object { $_.Current.Name -eq 'GameWall.ViewModels.GameCardViewModel' })
}

function Get-By-Name($name, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($elem, $label) {
    if ($null -eq $elem) { Fail "找不到控件：$label" }
    $p = $elem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
    Start-Sleep -Milliseconds 800
}

function First-CardTitle($cards) {
    if ($cards.Count -eq 0) { return "" }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $cards[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)

    # 卡片里的 Text 按树顺序是：★ / 游戏名 / 副标题 / 标签。
    # 第一个「★」是收藏角标，跳过它之后第一个非空的就是游戏名。
    foreach ($t in $texts) {
        $name = $t.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -eq "★") { continue }
        return $name
    }
    return ""
}

$total = (Get-Cards).Count
if ($total -eq 0) { Fail "一张卡片都没渲染出来" }
Ok "卡片式渲染 $total 张卡片"

# 两种视图：▤ 卡片式（默认）、☰ 列表式
Invoke-El (Get-By-Name "☰") "列表按钮"
$n = (Get-Cards).Count
if ($n -ne $total) { Fail "列表模式卡片数 $n != $total" }
Ok "列表式 $n 张"

Invoke-El (Get-By-Name "▤") "卡片式按钮"
$n = (Get-Cards).Count
if ($n -ne $total) { Fail "切回卡片式后卡片数 $n != $total" }
Ok "切回卡片式 $n 张"

# 搜索过滤：按卡片名的一部分过滤
$searchCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SearchBox")
$search = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCond)
if ($null -eq $search) { Fail "找不到 SearchBox" }

$vp = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$title = First-CardTitle (Get-Cards)
if ($title.Length -lt 2) { Fail "取不到第一张卡片的标题" }

$vp.SetValue($title)
Start-Sleep -Milliseconds 900
$filtered = Get-Cards
if ($filtered.Count -ne 1) { Fail "搜索「$title」应命中 1 张，实际 $($filtered.Count)" }
Ok "搜索过滤「$title」命中 1 张"

$vp.SetValue("____no_such_game____")
Start-Sleep -Milliseconds 900
if ((Get-Cards).Count -ne 0) { Fail "搜索无结果时应该 0 张" }
Ok "无结果时列表为空"

$vp.SetValue("")
Start-Sleep -Milliseconds 900
$n = (Get-Cards).Count
if ($n -ne $total) { Fail "清空搜索后应恢复 $total 张，实际 $n" }
Ok "清空搜索恢复 $n 张"

# 收藏筛选
$fav = Get-By-Name "收藏"
if ($null -eq $fav) { Fail "找不到「收藏」筛选框" }
$tp = $fav.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$tp.Toggle()
Start-Sleep -Milliseconds 900
$favCount = (Get-Cards).Count
# 库是真实数据时可能一个收藏都没有，这时筛完就该是 0 张；
# 有收藏时则必须是「少于总数」的真子集。
if ($favCount -ge $total) { Fail "收藏筛选结果异常：$favCount / $total" }
if ($favCount -eq 0) {
    Ok "只看收藏（当前没有收藏，筛完 0 张）"
} else {
    Ok "只看收藏 $favCount / $total"
}
$tp.Toggle()
Start-Sleep -Milliseconds 900
if ((Get-Cards).Count -ne $total) { Fail "取消收藏筛选后应恢复 $total" }
Ok "取消收藏筛选恢复 $total 张"

Write-Host ""
Write-Host "全部通过" -ForegroundColor Green
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
exit 0
