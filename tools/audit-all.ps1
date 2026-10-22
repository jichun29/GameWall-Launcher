# 全窗口排版审计：启动程序后依次打开每个对话框，逐个体检
#   1) 越界：控件矩形超出窗口边界
#   2) 重叠：两个控件互相压住（排除父子包含关系）
#   3) 溢出：控件被挤出所在容器（窗口内但已跑出可视区，用滚动/裁剪兜住的不算）
# 每档都打印关键坐标并抓一张 PrintWindow 图。
# 用法: & .\tools\audit-all.ps1
param(
    [string]$Exe = "src\GameWall\bin\Release\net8.0-windows\win-x64\GameWall.exe",
    [string]$OutDir = "docs\audit"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

if (-not ("GWAudit" -as [type])) {
Add-Type @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

public class GWAudit
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // 列出该进程下所有可见的 WPF 顶层窗口
    public static List<IntPtr> Windows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) { return true; }
            var cls = new StringBuilder(128);
            GetClassNameW(h, cls, 128);
            if (!cls.ToString().StartsWith("HwndWrapper")) { return true; }
            RECT r;
            if (!GetWindowRect(h, out r)) { return true; }
            if (r.Right - r.Left < 150 || r.Bottom - r.Top < 120) { return true; }
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        return sb.ToString();
    }

    public static bool Save(IntPtr h, string path)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) { return false; }
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) { return false; }
        using (var bmp = new System.Drawing.Bitmap(w, ht))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(h, hdc, 2);
            g.ReleaseHdc(hdc);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return true;
    }
}
"@ -ReferencedAssemblies System.Drawing
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function Audit-Window([IntPtr]$hwnd, [string]$tag) {
    $rect = New-Object GWAudit+RECT
    [GWAudit]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $winW = $rect.Right - $rect.Left
    $winH = $rect.Bottom - $rect.Top
    $title = [GWAudit]::Title($hwnd)

    Write-Host ""
    Write-Host ("=== [{0}] '{1}'  {2}x{3} at ({4},{5})" -f $tag, $title, $winW, $winH, $rect.Left, $rect.Top)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)

    $items = @()
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

    # 取元素所在滚动容器的「可视区」屏幕矩形。ScrollViewer 会把超出视口的内容裁掉，
    # 但 UIA 给出的 BoundingRectangle 是布局矩形、未经裁剪，直接用会误报越界/重叠。
    function Get-ScrollViewport($elem) {
        try {
            $p = $walker.GetParent($elem)
            $guard = 0
            while ($null -ne $p -and $guard -lt 12) {
                $guard++
                if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane) {
                    $sp = $null
                    if ($p.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)) {
                        if ($sp.Current.VerticallyScrollable -or $sp.Current.HorizontallyScrollable) {
                            $vr = $p.Current.BoundingRectangle
                            if ($vr.Width -gt 0 -and $vr.Height -gt 0) { return $vr }
                        }
                    }
                }
                if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { break }
                $p = $walker.GetParent($p)
            }
        } catch { }
        return $null
    }

    # 把所有祖先的矩形逐层求交。UIA 不给裁剪信息，但子元素的布局矩形一旦超出
    # 裁剪容器的矩形，画面上就一定是被裁掉的，求交后才是真正可见的那一块。
    function Clip-ToAncestors($elem, $r, $winRect) {
        try {
            $p = $walker.GetParent($elem)
            $guard = 0
            while ($null -ne $p -and $guard -lt 14) {
                $guard++
                $ct = $p.Current.ControlType
                if ($ct -eq [System.Windows.Automation.ControlType]::Window) { break }
                $ar = $p.Current.BoundingRectangle
                if ($ar.Width -gt 0 -and $ar.Height -gt 0) {
                    $x1 = [Math]::Max($r.X, $ar.X)
                    $y1 = [Math]::Max($r.Y, $ar.Y)
                    $x2 = [Math]::Min($r.X + $r.Width, $ar.X + $ar.Width)
                    $y2 = [Math]::Min($r.Y + $r.Height, $ar.Y + $ar.Height)
                    if ($x2 -le $x1 -or $y2 -le $y1) { return $null }
                    $r = New-Object System.Windows.Rect($x1, $y1, ($x2 - $x1), ($y2 - $y1))
                }
                $p = $walker.GetParent($p)
            }
            return $r
        } catch { }
        return $r
    }

    foreach ($e in $all) {
        try {
            # 完全滚出视口的元素不看
            if ($e.Current.IsOffscreen) { continue }
            $r = $e.Current.BoundingRectangle
            if ($r.Width -le 0 -or $r.Height -le 0) { continue }

            # 与滚动视口求交，只留下真正画在屏幕上的那一块
            $vp = Get-ScrollViewport $e
            if ($null -ne $vp) {
                $ix1 = [Math]::Max($r.X, $vp.X)
                $iy1 = [Math]::Max($r.Y, $vp.Y)
                $ix2 = [Math]::Min($r.X + $r.Width, $vp.X + $vp.Width)
                $iy2 = [Math]::Min($r.Y + $r.Height, $vp.Y + $vp.Height)
                if ($ix2 -le $ix1 -or $iy2 -le $iy1) { continue }   # 完全在视口外
                $r = New-Object System.Windows.Rect($ix1, $iy1, ($ix2 - $ix1), ($iy2 - $iy1))
            }

            # 再与所有祖先容器求交（ClipToBounds 的 Border / Grid 等
            # UIA 一样不会帮你裁，剪掉之后才是屏幕上真正看得到的那部分）
            $clipped = Clip-ToAncestors $e $r $rect
            if ($null -eq $clipped) { continue }
            $r = $clipped

            $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            $nm = $e.Current.Name
            if ([string]::IsNullOrWhiteSpace($nm)) { $nm = "<$ct>" }
            $items += [pscustomobject]@{
                Type = $ct
                Name = ($nm -replace "`r?`n", " ")
                X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height
            }
        } catch { }
    }

    # 1) 越界
    $vis = $items
    $over = @($vis | Where-Object {
        $_.X -lt $rect.Left -or ($_.X + $_.W) -gt $rect.Right -or
        $_.Y -lt $rect.Top -or ($_.Y + $_.H) -gt $rect.Bottom
    })
    Write-Host ("  elements: {0} (visible {1})   overflow: {2}" -f $items.Count, $vis.Count, $over.Count)
    if ($over.Count -gt 0) {
        $over | Sort-Object Y, X | ForEach-Object {
            Write-Host ("    [OVERFLOW] {0,-12} '{1}'  x {2}->{3}  y {4}->{5}" -f $_.Type, $_.Name, $_.X, ($_.X + $_.W), $_.Y, ($_.Y + $_.H))
        }
    }

    # 2) 重叠（只比可见元素，排除 85% 以上包含关系）
    $pairs = 0
    for ($i = 0; $i -lt $vis.Count; $i++) {
        for ($j = $i + 1; $j -lt $vis.Count; $j++) {
            $a = $vis[$i]; $b = $vis[$j]
            $ox = [Math]::Max(0, [Math]::Min($a.X + $a.W, $b.X + $b.W) - [Math]::Max($a.X, $b.X))
            $oy = [Math]::Max(0, [Math]::Min($a.Y + $a.H, $b.Y + $b.H) - [Math]::Max($a.Y, $b.Y))
            if ($ox -le 0 -or $oy -le 0) { continue }
            $inter = $ox * $oy
            if ($inter -ge [Math]::Min($a.W * $a.H, $b.W * $b.H) * 0.85) { continue }
            # 只关心「同级控件被压住」，父子关系（如 TextBox 与其内部 Edit）跳过
            if ($a.Type -eq $b.Type -and $a.Name -eq $b.Name) { continue }
            $pairs++
            Write-Host ("    [OVERLAP] {0} '{1}' {2}..{3}/{4}..{5}  VS  {6} '{7}' {8}..{9}/{10}..{11}" -f `
                $a.Type, $a.Name, $a.X, ($a.X + $a.W), $a.Y, ($a.Y + $a.H), `
                $b.Type, $b.Name, $b.X, ($b.X + $b.W), $b.Y, ($b.Y + $b.H))
        }
    }
    Write-Host ("  overlap pairs: {0}" -f $pairs)

    # 3) 交互控件高度一致性（同一水平带上）
    $bands = $vis | Where-Object { $_.Type -in @("Button", "Edit", "ComboBox", "CheckBox") } |
        Group-Object { [int](($_.Y - $rect.Top) / 10) }
    foreach ($band in $bands) {
        if ($band.Count -lt 2) { continue }
        $heights = ($band.Group | Select-Object -ExpandProperty H | Sort-Object -Unique)
        if ($heights.Count -gt 1) {
            $range = ($heights[-1] - $heights[0])
            if ($range -ge 4) {
                $desc = ($band.Group | ForEach-Object { "$($_.Type)('$($_.Name)')=$($_.H)px" }) -join ", "
                Write-Host ("    [HEIGHT] band y≈{0}: {1}" -f ($band.Group[0].Y - $rect.Top), $desc)
            }
        }
    }

    $safe = ($title -replace '[\\/:*?"<>|\s]', '_')
    if ([string]::IsNullOrWhiteSpace($safe)) { $safe = "untitled" }
    $file = Join-Path $OutDir ("{0}-{1}.png" -f $tag, $safe)
    $ok = [GWAudit]::Save($hwnd, $file)
    Write-Host ("  shot: {0} ok={1}" -f (Split-Path $file -Leaf), $ok)
}

# ── 启动 ──────────────────────────────────────────────────────────
Get-Process -Name GameWall -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 200; $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { break } }
if ($proc.MainWindowHandle -eq 0) { throw "主窗口没起来" }
Start-Sleep -Milliseconds 1200

$mainHwnd = $proc.MainWindowHandle
$pid32 = [uint32]$proc.Id
$root = [System.Windows.Automation.AutomationElement]::FromHandle($mainHwnd)

function Invoke-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $btn) { Write-Host "  !! 找不到控件 '$name'"; return $false }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1100
    return $true
}

function Close-TopDialog([IntPtr]$keep) {
    $wins = [GWAudit]::Windows($pid32) | Where-Object { $_ -ne $keep }
    foreach ($w in $wins) {
        # 用 UIA 找 ✕ 按钮关掉
        try {
            $r = [System.Windows.Automation.AutomationElement]::FromHandle($w)
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, "✕")
            $x = $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($x) { $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700 }
        } catch { }
    }
}

# 先关掉可能弹出来的首次运行引导
Close-TopDialog $mainHwnd

# ── 逐个窗口体检 ──────────────────────────────────────────────────
Audit-Window $mainHwnd "01-main-1280x820"

# 主窗口缩窄
[GWAudit]::SetWindowPos($mainHwnd, [IntPtr]::Zero, 40, 40, 900, 700, 0x0040) | Out-Null
Start-Sleep -Milliseconds 900
Audit-Window $mainHwnd "02-main-900x700"

# 恢复
[GWAudit]::SetWindowPos($mainHwnd, [IntPtr]::Zero, 40, 40, 1280, 820, 0x0040) | Out-Null
Start-Sleep -Milliseconds 900

# 设置对话框
if (Invoke-ByName "⚙ 设置") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) {
            # 用窗口自己的尺寸，不要强改大小（强改会把内容压变形，审计结果就不可信了）
            [GWAudit]::SetWindowPos($w, [IntPtr]::Zero, 960, 40, 0, 0, 0x0001) | Out-Null
            Start-Sleep -Milliseconds 800
            Audit-Window $w "03-settings"
        }
    }
    Close-TopDialog $mainHwnd
}

# 搜索添加
if (Invoke-ByName "🔎 搜索添加") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) { Audit-Window $w "04-search-add" }
    }
    Close-TopDialog $mainHwnd
}

# 平台导入
if (Invoke-ByName "⤓ 平台导入") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) { Audit-Window $w "05-import" }
    }
    Close-TopDialog $mainHwnd
}

# 关于对话框
if (Invoke-ByName "ⓘ 关于") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) {
            [GWAudit]::SetWindowPos($w, [IntPtr]::Zero, 960, 40, 0, 0, 0x0001) | Out-Null
            Start-Sleep -Milliseconds 800
            Audit-Window $w "06-about"
        }
    }
    Close-TopDialog $mainHwnd
}

# 整理桌面对话框
if (Invoke-ByName "🧹 整理桌面") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) {
            [GWAudit]::SetWindowPos($w, [IntPtr]::Zero, 960, 40, 0, 0, 0x0001) | Out-Null
            Start-Sleep -Milliseconds 800
            Audit-Window $w "07-tidy"
        }
    }
    Close-TopDialog $mainHwnd
}

# 游戏工具箱（硬件信息 + 优化方案 + 电源方案）
if (Invoke-ByName "🎛 工具箱") {
    foreach ($w in [GWAudit]::Windows($pid32)) {
        if ($w -ne $mainHwnd) {
            [GWAudit]::SetWindowPos($w, [IntPtr]::Zero, 40, 30, 0, 0, 0x0001) | Out-Null
            Start-Sleep -Milliseconds 1200
            Audit-Window $w "08-toolbox"
        }
    }
    Close-TopDialog $mainHwnd
}

# 编辑游戏（右键第一张卡片 → 编辑）
$card = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)))
if ($card) {
    $r = $card.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2)
    $cy = [int]($r.Y + $r.Height / 2)
    # 右键：WM_RBUTTONDOWN/UP + ScreenToClient
    Add-Type -AssemblyName System.Windows.Forms
    $pt = New-Object System.Drawing.Point $cx, $cy
    [System.Windows.Forms.Cursor]::Position = $pt
    Start-Sleep -Milliseconds 200
    Write-Host "  右键卡片 -> ($cx,$cy)"
}

Write-Host ""
Write-Host "done. 结果图在 $OutDir"
