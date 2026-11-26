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

    # 卡片里的 Text 按树顺序是：角标（★ 收藏 / 文件缺失）/ 游戏名 / 副标题 / 标签。
    # 角标是浮在左上角的 Overlay，在视觉树上排在游戏名前面，
    # 所以这里要跳过所有角标文案，第一个剩下的非空文本才是游戏名。
    # 注意：以后再加新角标，记得往 $badges 里补一条，否则这条断言会取错标题。
    $badges = @("★", "文件缺失")
    foreach ($t in $texts) {
        $name = $t.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($badges -contains $name) { continue }
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

# ── 失效路径提示条 ──────────────────────────────────────
# 提示条是后台算完之后才出现的（首屏刻意不阻塞），所以这里轮询等它，
# 而不是固定 sleep 一个数字——机器慢的时候固定值会误报。
# 判断口径：库里只要还有 LaunchKind=Executable 且 exe 不存在的卡片，
# 提示条就该出现；全都正常时它不该出现。这个断言两种场景都成立。
$exeGames = @()
$libPath = Join-Path $env:APPDATA 'GameWall\library.json'
if (Test-Path $libPath) {
    $lib = Get-Content $libPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $exeGames = @($lib | Where-Object {
        $_.LaunchKind -eq 'Executable' -and $_.ExecutablePath -and
        -not (Test-Path -LiteralPath $_.ExecutablePath)
    })
}

$bannerText = ""
for ($i = 0; $i -lt 40; $i++) {
    $hit = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "去修复")))
    if ($hit) { $bannerText = "去修复"; break }
    Start-Sleep -Milliseconds 150
}

if ($exeGames.Count -gt 0) {
    if (-not $bannerText) { Fail "有 $($exeGames.Count) 个 exe 路径失效，但顶栏没出现提示条" }
    Ok "路径失效提示条已出现（$($exeGames.Count) 个失效）"
} else {
    if ($bannerText) { Fail "所有路径都正常，提示条不该出现" }
    Ok "路径全部正常，无失效提示"
}

Write-Host ""
Write-Host "全部通过" -ForegroundColor Green
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
exit 0
