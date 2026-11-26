# 修复失效路径窗口的独立验证脚本
#
# 目的：确认「去修复」能真的打开 RelinkWindow，并把失效的 exe 列出来。
# 冒烟脚本只验证提示条出现，不验证窗口本身，这里补上。
#
# 用法：.\tools\verify-relink.ps1
#       .\tools\verify-relink.ps1 -ExePath <某个 exe>

param(
    [string]$ProcessName = "GameWall",
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Fail($msg) {
    Write-Host "[FAIL] $msg" -ForegroundColor Red
    Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
    exit 1
}
function Ok($msg) { Write-Host "[ OK ] $msg" -ForegroundColor Green }

Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot "..\dist\GameWall.exe"
}
if (-not (Test-Path $ExePath)) { Fail "找不到 exe：$ExePath" }

# 先算出「应该有几个失效」，跟窗口里列出来的条数对帐
$expected = @()
$libPath = Join-Path $env:APPDATA 'GameWall\library.json'
if (Test-Path $libPath) {
    $lib = Get-Content $libPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $expected = @($lib | Where-Object {
        $_.LaunchKind -eq 'Executable' -and $_.ExecutablePath -and
        -not (Test-Path -LiteralPath $_.ExecutablePath)
    })
}
if ($expected.Count -eq 0) {
    Write-Host "  库里的 exe 路径全都在，没有可修的项。" -ForegroundColor Yellow
    Write-Host "  这个脚本需要至少一个失效项才有意义，先造一个再跑。" -ForegroundColor Yellow
    exit 2
}
Write-Host "  预期失效项：$($expected.Count) 个" -ForegroundColor DarkGray

$proc = Start-Process -FilePath $ExePath -PassThru
for ($i = 0; $i -lt 600; $i++) {
    Start-Sleep -Milliseconds 15
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.MainWindowHandle -eq 0) { Fail "主窗口没起来" }
Start-Sleep -Milliseconds 1800

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Find-ByName($parent, $name) {
    $parent.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

# 提示条是后台算完才出现的，轮询等「去修复」
$btn = $null
for ($i = 0; $i -lt 40; $i++) {
    $btn = Find-ByName $root "去修复"
    if ($btn) { break }
    Start-Sleep -Milliseconds 150
}
if (-not $btn) { Fail "顶栏没出现「去修复」按钮" }
Ok "提示条就绪"

$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()

# 等窗口出现：按标题找
$win = $null
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 100
    $win = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) "修复失效的游戏路径"
    if ($win) { break }
}
if (-not $win) { Fail "点「去修复」之后窗口没打开" }
Ok "修复窗口已打开"

Start-Sleep -Milliseconds 700

# 数列表项。
# 注意：WPF 的 ListBox 自定义了 ItemContainerStyle 之后，UIA 里 ListBoxItem
# 有时不暴露成 ControlType=ListItem（取决于 AutomationPeer 的推导），
# 所以这里先用 ListItem 找，找不到就退回「按行内文本块」判断，
# 免得模板一改就误报「列了 0 项」。
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond))

if ($items.Count -eq 0) {
    # 兜底：按列表容器里的自定义项找
    $listCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $lists = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond))
    foreach ($l in $lists) {
        $kids = @($l.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::ScrollBar })
        if ($kids.Count -gt $items.Count) { $items = $kids }
    }
}

if ($items.Count -ne $expected.Count) {
    Fail "窗口里列了 $($items.Count) 项，但库里实际失效 $($expected.Count) 个"
}
Ok "列出了 $($items.Count) 个失效项"

# 每行必须能读到游戏名（防止模板绑错、渲染成空白行）
$textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)
$named = 0
foreach ($it in $items) {
    $texts = @($it.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Current.Name) })
    # 一行至少有「游戏名 + 可信度标签 + 旧路径」三段文本
    if ($texts.Count -ge 3) { $named++ }
}
if ($named -ne $items.Count) { Fail "有 $($items.Count - $named) 行没渲染出完整信息" }
Ok "每行都渲染出了名称、可信度、路径"

# 关掉窗口，再关主程序
$closeBtn = $win.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "Close")))
Write-Host ""
Write-Host "全部通过" -ForegroundColor Green
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
exit 0
